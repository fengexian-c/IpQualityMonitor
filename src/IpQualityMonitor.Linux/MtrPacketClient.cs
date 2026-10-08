using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IpQualityMonitor.Linux;

/// <summary>A bounded private-pipe transport. All pending requests belong to one process generation.</summary>
public sealed class MtrPacketClient : IAsyncDisposable
{
    private sealed class Session(Process process)
    {
        public Process Process { get; } = process;
        public CancellationTokenSource Stop { get; } = new();
        public ConcurrentDictionary<int, TaskCompletionSource<PacketReply>> Pending { get; } = new();
        public ConcurrentDictionary<string, bool> Features { get; } = new();
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public Task Output { get; set; } = Task.CompletedTask;
        public Task Error { get; set; } = Task.CompletedTask;
        public int Closed;
    }
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _slots = new(64, 64);
    private readonly CancellationTokenSource _stop = new();
    private Session? _session;
    private int _nextToken;
    private long _retryAfter;
    private string? _lastError;
    public string? LastError => Volatile.Read(ref _lastError);
    public int Pending => _session?.Pending.Count ?? 0;
    public MtrPacketClient(string executable, string[]? arguments = null)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new ArgumentException("探测组件必须使用绝对路径。");
        _executable = executable; _arguments = arguments ?? [];
    }
    private async Task<Session> GetSessionAsync(CancellationToken token)
    {
        await _startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_session is { Closed: 0 } current && !current.Process.HasExited) return current;
            if (Environment.TickCount64 < Interlocked.Read(ref _retryAfter)) throw new IOException("探测组件重启冷却中。");
            if (_session is {} old)
            {
                Fail(old, new IOException("探测组件已退出。"));
                await Task.WhenAll(old.Output, old.Error).ConfigureAwait(false);
                old.Process.Dispose(); old.Stop.Dispose();
            }
            var start = new ProcessStartInfo(_executable) { UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in _arguments) start.ArgumentList.Add(argument);
            try
            {
                var process = Process.Start(start) ?? throw new IOException("无法启动探测组件。");
                process.StandardInput.AutoFlush = true;
                var session = new Session(process); _session = session;
                session.Output = ReadOutputAsync(session);
                session.Error = DrainErrorAsync(session);
                return session;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message; Interlocked.Exchange(ref _retryAfter, Environment.TickCount64 + 5000); throw;
            }
        }
        finally { _startGate.Release(); }
    }
    private async Task ReadOutputAsync(Session s)
    {
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                var line = await ReadLineAsync(s.Process.StandardOutput, s.Stop.Token).ConfigureAwait(false);
                if (line is null) throw new IOException("探测组件输出已关闭。");
                var reply = PacketReply.Parse(line);
                if (s.Pending.TryRemove(reply.Token, out var completion)) completion.TrySetResult(reply);
                // A late/duplicate response from this generation is not assigned to another probe.
            }
        }
        catch (Exception ex) { Fail(s, ex); }
    }
    private async Task DrainErrorAsync(Session s)
    {
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                var line = await ReadLineAsync(s.Process.StandardError, s.Stop.Token).ConfigureAwait(false);
                if (line is null) break;
                _lastError = line.Length <= 512 ? line : line[..512];
            }
        }
        catch (Exception ex) { if (!s.Stop.IsCancellationRequested) Fail(s, ex); }
    }
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= 8192) throw new InvalidDataException("探测组件输出超过限制。");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : throw new InvalidDataException("探测组件输出被截断。");
    }
    private void Fail(Session s, Exception error)
    {
        if (Interlocked.Exchange(ref s.Closed, 1) != 0) return;
        _lastError = error.Message; Interlocked.Exchange(ref _retryAfter, Environment.TickCount64 + 5000);
        s.Stop.Cancel();
        try { if (!s.Process.HasExited) s.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        foreach (var entry in s.Pending)
            if (s.Pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(new IOException("本地探测组件异常。", error));
    }
    private static async Task WriteAsync(Session s, string line, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, s.Stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await s.WriteGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { await s.Process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false); }
        finally { s.WriteGate.Release(); }
    }
    private async Task<PacketReply> CommandAsync(Session s, string command, int timeoutMs, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(false))
            throw new IOException("本地探测队列已满。");
        int id = Interlocked.Increment(ref _nextToken);
        var completion = new TaskCompletionSource<PacketReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (id <= 0) throw new IOException("请求编号耗尽，请重启服务。");
            if (Volatile.Read(ref s.Closed) != 0) throw new IOException("探测组件已关闭。");
            if (!s.Pending.TryAdd(id, completion)) throw new InvalidOperationException("重复的请求编号。");
            // Register before writing: loopback replies may arrive before WriteLineAsync completes.
            await WriteAsync(s, id.ToString(CultureInfo.InvariantCulture) + " " + command, _stop.Token).ConfigureAwait(false);
            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 5000), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                if (command.StartsWith("send-probe ", StringComparison.Ordinal) && Volatile.Read(ref s.Closed) == 0)
                {
                    // Wait for the owned request to be removed in the helper before releasing a slot.
                    try
                    {
                        await WriteAsync(s, id.ToString(CultureInfo.InvariantCulture) + " cancel-probe", _stop.Token).ConfigureAwait(false);
                        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Fail(s, ex); }
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception ex) { Fail(s, ex); throw; }
        finally { s.Pending.TryRemove(id, out _); _slots.Release(); }
    }
    private async Task RequireFeatureAsync(Session s, string name, CancellationToken token)
    {
        if (s.Features.TryGetValue(name, out var cached))
        { if (!cached) throw new IOException("探测组件缺少能力：" + name); return; }
        var response = await CommandAsync(s, "check-support feature " + name, 1000, token).ConfigureAwait(false);
        var supported = response.Kind == "feature-support" && response.Get("support") == "ok";
        s.Features[name] = supported;
        if (!supported)
        {
            _lastError = "探测组件缺少能力（不使用有损回退）：" + name;
            throw new IOException(_lastError);
        }
    }
    public async Task<PacketReply> ProbeAsync(IPAddress ip, int ttl, int timeoutMs, CancellationToken token)
    {
        if (ttl is < 1 or > 255 || timeoutMs is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        if (ip.IsIPv6LinkLocal) throw new NotSupportedException("本预览版尚未验证链路本地 IPv6；请使用公网 IPv6 或 IPv4。");
        var s = await GetSessionAsync(token).ConfigureAwait(false);
        await RequireFeatureAsync(s, "iqm-contract-v1", token).ConfigureAwait(false);
        var v4 = ip.AddressFamily == AddressFamily.InterNetwork;
        await RequireFeatureAsync(s, v4 ? "iqm-raw-ip-4" : "iqm-raw-ip-6", token).ConfigureAwait(false);
        // Explicit 32-byte zero payload; size includes IP and ICMP headers.
        var command = FormattableString.Invariant($"send-probe {(v4 ? "ip-4" : "ip-6")} {ip} protocol icmp ttl {ttl} size {(v4 ? 60 : 80)} bit-pattern 0 timeout-ms {timeoutMs}");
        return await CommandAsync(s, command, timeoutMs, token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is {} s)
            {
                Fail(s, new IOException("探测组件停止。"));
                await Task.WhenAll(s.Output, s.Error).ConfigureAwait(false);
                s.Process.Dispose(); s.Stop.Dispose(); _session = null;
            }
        }
        finally { _startGate.Release(); }
    }
}
