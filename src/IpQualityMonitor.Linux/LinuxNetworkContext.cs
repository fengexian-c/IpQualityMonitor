using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Linux;

/// <summary>The synchronous Core callback only reads cache and schedules a shared refresh; it never launches/waits on ip.</summary>
public sealed class LinuxNetworkContext : IAsyncDisposable
{
    private sealed record Entry(NetworkContext Context, long Updated);
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, Task> _refreshing = new();
    private readonly SemaphoreSlim _workers = new(2, 2);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _ipExecutable;
    private readonly string _site;
    public LinuxNetworkContext(string ipExecutable, string site)
    {
        if (!Path.IsPathFullyQualified(ipExecutable)) throw new ArgumentException("ip 必须使用绝对路径。");
        _ipExecutable = ipExecutable; _site = site;
    }
    public NetworkContext Read(string address)
    {
        if (_cache.TryGetValue(address, out var cached))
        {
            var age = Environment.TickCount64 - cached.Updated;
            if (age >= 10000) Schedule(address);
            // Short stale-while-revalidate avoids a fabricated network change every ten seconds.
            // A failed refresh replaces the entry with unknown; routes explicitly await refresh.
            if (age < 30000) return cached.Context;
        }
        Schedule(address);
        return new("unknown", "Docker bridge · 系统选路正在刷新（不是宿主机命名空间）");
    }
    public async Task<NetworkContext> RefreshAsync(string address, CancellationToken token)
    {
        await Schedule(address).WaitAsync(token).ConfigureAwait(false);
        return _cache.TryGetValue(address, out var entry) ? entry.Context : new("unknown", "选路信息不可用");
    }
    private Task Schedule(string address)
    {
        // Admission and shutdown share the lock: queued work is bounded as well as processes.
        lock (_refreshing)
        {
            if (_stop.IsCancellationRequested) return Task.CompletedTask;
            if (_refreshing.TryGetValue(address, out var existing)) return existing;
            if (_refreshing.Count >= 128) return Task.CompletedTask;
            var task = Task.Run(() => RefreshCoreAsync(address)); _refreshing[address] = task;
            _ = task.ContinueWith(_ => { lock (_refreshing) _refreshing.TryRemove(address, out var ignored); }, TaskScheduler.Default);
            return task;
        }
    }
    private void Store(string address, NetworkContext context)
    {
        lock (_refreshing)
        {
            if (_stop.IsCancellationRequested) return;
            if (_cache.Count >= 128 && !_cache.ContainsKey(address))
            {
                var oldest = _cache.OrderBy(pair => pair.Value.Updated).First().Key;
                _cache.TryRemove(oldest, out _);
            }
            _cache[address] = new(context, Environment.TickCount64);
        }
    }
    private async Task RefreshCoreAsync(string address)
    {
        var acquired = false;
        try
        {
            await _workers.WaitAsync(_stop.Token).ConfigureAwait(false); acquired = true;
            var ip = IPAddress.Parse(address);
            var start = new ProcessStartInfo(_ipExecutable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-j", ip.AddressFamily == AddressFamily.InterNetwork ? "-4" : "-6", "route", "get", ip.ToString() })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("无法启动 ip。");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); limit.CancelAfter(2000);
            var output = ReadBoundedAsync(process.StandardOutput, limit.Token);
            var errors = ReadBoundedAsync(process.StandardError, limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false); await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("内核选路查询失败。");
                var context = Parse(text, _site);
                Store(address, context);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch (Exception) { }
            }
        }
        catch (Exception ex)
        { Store(address, new("unknown", "Docker bridge · 系统选路不可用：" + ex.Message)); }
        finally { if (acquired) _workers.Release(); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[1024]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        { if (text.Length + count > 16384) throw new InvalidDataException("选路输出过长。"); text.Append(buffer, 0, count); }
        return text.ToString();
    }
    public static NetworkContext Parse(string text, string site)
    {
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != 1)
            throw new InvalidDataException("非唯一系统选路。");
        var route = json.RootElement[0];
        string Field(string key) => route.TryGetProperty(key, out var value) ? value.ToString() : "";
        var device = Field("dev"); var source = Field("prefsrc");
        if (source.Length == 0) source = Field("src");
        var gateway = Field("gateway"); var table = Field("table");
        if (device.Length == 0 || !IPAddress.TryParse(source, out _)) throw new InvalidDataException("系统选路缺少接口或源地址。");
        var identity = string.Join('|', site, "bridge", device, source, gateway, table);
        var key = "linux-bridge:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(key, $"Docker bridge · 接口 {device} · 源 {source} · 下一跳 {(gateway.Length == 0 ? "直连" : gateway)} · 仅为容器系统选路参考");
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_refreshing) { _stop.Cancel(); tasks = _refreshing.Values.ToArray(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
