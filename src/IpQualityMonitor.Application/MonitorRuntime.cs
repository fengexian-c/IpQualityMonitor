using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Application;

public sealed record TargetInput(string Name, string Address, string Mode, int Port);
public sealed record ProfileState(TargetProfile Profile, bool Running, MeasurementState? Primary, MeasurementState? Secondary);

/// <summary>Application-scoped runtime. The HTTP request owns only its wait, never the shared collectors.</summary>
public sealed class MonitorRuntime : IAsyncDisposable
{
    private readonly ServerStorage _storage;
    private readonly MonitorResources _resources;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _queries = new(2, 2);
    private ServerConfiguration _configuration;
    private MultiTargetMonitor? _monitor;
    private RouteAnalysisService? _analysis;
    private volatile bool _closing;
    private volatile bool _ready;
    private bool _startAttempted;
    private string? _error;
    public History History { get; }
    public ProbeSite Site => _storage.Site;
    public string? Error => _error;
    public string? AnalysisError => _analysis?.LastError?.Message;
    public bool Ready => _ready;
    public ServerConfiguration Configuration => Volatile.Read(ref _configuration).Copy();
    public MonitorRuntime(ServerStorage storage, MonitorResources resources)
    {
        _storage = storage; _resources = resources; _configuration = storage.Load();
        History = new History(Path.Combine(storage.DirectoryPath, "history.db"));
    }
    public async Task StartAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_closing) throw new InvalidOperationException("后台正在停止。");
            if (Ready) return;
            token.ThrowIfCancellationRequested();
            if (_startAttempted) throw new InvalidOperationException("后台初始化失败或被取消，请重新启动服务。");
            _startAttempted = true;
            // Once initialization starts, wait for it before allowing disposal to touch SQLite.
            await Task.Run(History.Initialize).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _analysis = new RouteAnalysisService(History, Path.Combine(_storage.DirectoryPath, "route-analysis.db"));
            _monitor = new MultiTargetMonitor(History, _resources);
            _monitor.StorageFailed += ex => _error = "存储异常，采集已停止：" + ex.Message;
            _monitor.TargetFailed += (_, ex) => _error = "目标采集异常：" + ex.Message;
            foreach (var p in _configuration.Profiles.Where(p => p.ResumeOnLaunch))
            {
                token.ThrowIfCancellationRequested();
                try { await _monitor.StartAsync(p).ConfigureAwait(false); }
                catch (Exception ex) { _error = "恢复目标失败：" + ex.Message; }
            }
            _ready = true;
        }
        finally { _operations.Release(); }
    }
    public IReadOnlyList<ProfileState> States()
    {
        var monitor = _monitor ?? throw new InvalidOperationException("后台尚未初始化。");
        return Configuration.Profiles.Select(p => new ProfileState(p, monitor.IsRunning(p.Id), monitor.State(p.Primary),
            p.Secondary is {} second ? monitor.State(second) : null)).ToArray();
    }
    private void Check(long revision)
    {
        if (_closing || !Ready) throw new InvalidOperationException("后台未就绪或正在停止。");
        if (_configuration.Revision != revision) throw new ConfigurationConflictException();
    }
    private void Commit(ServerConfiguration next)
    {
        next = next with { Revision = checked(_configuration.Revision + 1) };
        _storage.Save(next); Volatile.Write(ref _configuration, next);
    }
    public async Task<string> AddAsync(TargetInput input, long revision)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Name?.Length > 100 || string.IsNullOrWhiteSpace(input.Address) || input.Address.Length > 100)
            throw new ArgumentException("目标名称或地址不能为空或超过 100 个字符。");
        if (input.Mode is not ("Icmp" or "Tcp" or "Both")) throw new ArgumentException("未知的目标模式。");
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Check(revision);
            if (_configuration.Profiles.Count >= 20) throw new ArgumentException("预览版最多支持 20 个目标。");
            var next = _configuration.Copy();
            var p = new TargetProfile { Name = input.Name?.Trim() ?? "", Address = input.Address?.Trim() ?? "",
                Mode = input.Mode, Port = input.Port, ResumeOnLaunch = false };
            next.Monitoring.Apply(p, false); p.Validate(); next.Profiles.Add(p);
            Commit(next); return p.Id;
        }
        finally { _operations.Release(); }
    }
    public async Task SetRunningAsync(string id, bool running, long revision)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Check(revision);
            var next = _configuration.Copy();
            var p = next.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("目标不存在。");
            p.ResumeOnLaunch = running;
            // Persist intent first. A failed start remains visible and retryable, including after restart.
            Commit(next);
            if (running)
            {
                // Core keeps ownership of a faulted worker; remove stale ownership before retrying.
                if (!_monitor!.IsRunning(id)) await _monitor.StopAsync(id).ConfigureAwait(false);
                await _monitor.StartAsync(p).ConfigureAwait(false);
            }
            else await _monitor!.StopAsync(id).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }
    public async Task RemoveAsync(string id, long revision)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Check(revision);
            var next = _configuration.Copy();
            if (next.Profiles.RemoveAll(p => p.Id == id) == 0) throw new KeyNotFoundException("目标不存在。");
            // Deletion removes configuration only; original samples and route decisions remain retained.
            Commit(next); await _monitor!.StopAsync(id).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }
    public async Task SetPolicyAsync(GlobalMonitorSettings policy, long revision)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate(); await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Check(revision);
            var next = _configuration.Copy() with { Monitoring = policy };
            foreach (var p in next.Profiles) policy.Apply(p);
            Commit(next);
            await _monitor!.StopAllAsync().ConfigureAwait(false);
            foreach (var p in next.Profiles.Where(p => p.ResumeOnLaunch))
                try { await _monitor.StartAsync(p).ConfigureAwait(false); }
                catch (Exception ex) { _error = "应用设置后启动失败：" + ex.Message; }
        }
        finally { _operations.Release(); }
    }
    public bool RequestRoute(string id) => !_closing && Ready && (_monitor?.RequestRoute(id) ?? false);
    public Target Resolve(string id, ProbeProtocol protocol)
    {
        var p = Configuration.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("目标不存在。");
        return p.ForProtocol(protocol) ?? throw new ArgumentException("目标未启用此协议。");
    }
    public Target RouteTarget(string id) => Configuration.Profiles.SingleOrDefault(p => p.Id == id)?.Primary
        ?? throw new KeyNotFoundException("目标不存在。");
    public async Task<T> QueryAsync<T>(Func<History, T> query, CancellationToken token)
    {
        if (_closing || !Ready) throw new InvalidOperationException("后台未就绪或正在停止。");
        await _queries.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_closing || !Ready) throw new InvalidOperationException("后台未就绪或正在停止。");
            return await Task.Run(() => query(History), token).ConfigureAwait(false);
        }
        finally { _queries.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closing) return;
            _closing = true; _ready = false;
            // Synchronous SQLite reads cannot be interrupted safely. Drain their owners before
            // disposing History, and hold the dataset lease until all readers/writers are done.
            await _queries.WaitAsync().ConfigureAwait(false);
            await _queries.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    if (_monitor is not null) await _monitor.DisposeAsync().ConfigureAwait(false);
                    else await _resources.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try { if (_analysis is not null) await _analysis.DisposeAsync().ConfigureAwait(false); }
                    finally { await History.DisposeAsync().ConfigureAwait(false); }
                }
            }
            finally
            {
                try { _storage.Dispose(); }
                finally { _queries.Release(2); }
            }
        }
        finally { _operations.Release(); }
    }
}
