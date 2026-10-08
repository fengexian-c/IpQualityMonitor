using IpQualityMonitor.Application;
using IpQualityMonitor.Linux;
using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Web;

public sealed class MonitoringHost(MonitorRuntime runtime, LinuxNetworkContext network) : IHostedService
{
    public async Task StartAsync(CancellationToken token)
    {
        await Task.WhenAll(runtime.Configuration.Profiles.Where(p => p.ResumeOnLaunch)
            .Select(p => network.RefreshAsync(p.Address, token)));
        await runtime.StartAsync(token);
    }
    public async Task StopAsync(CancellationToken token) => await runtime.DisposeAsync();
}

/// <summary>A 15-second immutable result per series/window; cancelling a viewer never cancels shared work.</summary>
public sealed class SharedTimelineReader(MonitorRuntime runtime)
{
    private readonly Dictionary<string, (long Slot, Lazy<Task<Timeline>> Value)> _cache = new();
    private readonly object _gate = new();
    public async Task<Timeline> ReadAsync(Target target, int days, TimeZoneInfo zone, CancellationToken token)
    {
        if (days is not (1 or 7)) throw new ArgumentException("只支持近 24 小时或近 7 天。");
        var slot = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 15;
        var key = $"{target.Key}|{days}|{zone.Id}|{slot}";
        Lazy<Task<Timeline>> value;
        lock (_gate)
        {
            foreach (var stale in _cache.Where(p => p.Value.Slot < slot - 4 && p.Value.Value.IsValueCreated &&
                         p.Value.Value.Value.IsCompleted).Select(p => p.Key).ToArray()) _cache.Remove(stale);
            if (!_cache.TryGetValue(key, out var entry))
            {
                if (_cache.Count >= 64)
                {
                    var evict = _cache.FirstOrDefault(p => p.Value.Value.IsValueCreated && p.Value.Value.Value.IsCompleted).Key;
                    if (evict is null) throw new InvalidOperationException("时间轴查询繁忙，请稍后重试。");
                    _cache.Remove(evict);
                }
                var until = DateTimeOffset.FromUnixTimeSeconds(slot * 15);
                value = new(() => runtime.QueryAsync(h => h.LoadTimeline(target, until, days, zone), CancellationToken.None));
                _cache[key] = (slot, value);
            }
            else value = entry.Value;
        }
        return await value.Value.WaitAsync(token);
    }
}
