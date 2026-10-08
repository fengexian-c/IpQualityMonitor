using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Application;

public sealed record AnnotationSettings(string Mode = "offline", int RefreshHours = 24)
{
    public void Validate()
    {
        if (Mode is not ("offline" or "v3" or "v4") || RefreshHours is <1 or >168)
            throw new ArgumentException("定位模式须为 offline、v3 或 v4；缓存须为 1–168 小时。");
    }
}

/// <summary>Deployment-only secrets. This object is never a configuration or API DTO.</summary>
public sealed class AnnotationEnvironment(string helperPath = "/usr/local/libexec/iqm-nexttrace-geo", string? tokenFile = null)
{
    public INextTraceLookup CreateLookup() => new NextTraceProcessClient(helperPath);
    public string ReadToken(string mode)
    {
        if (mode != "v4") return "";
        try
        {
            if (string.IsNullOrWhiteSpace(tokenFile)) throw new IOException();
            using var stream = File.OpenRead(tokenFile);
            if (stream.Length is <1 or >4098) throw new IOException();
            using var reader = new StreamReader(stream);
            var value = reader.ReadToEnd().TrimEnd('\r', '\n');
            if (value.Length is <1 or >4096 || value.Any(char.IsControl)) throw new IOException();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidOperationException("v4 密钥文件缺失、不可读或格式无效；不会切换至 v3。"); }
    }
}

public sealed record AnnotationNodeView(int Ttl, string Address, string Region, long? Asn, string Organization,
    string Source, DateTimeOffset? QueriedAt, DateTimeOffset? ExpiresAt, string State,
    bool Stale, DateTimeOffset? LastAttemptAt, string AttemptState);
public sealed record AnnotationView(string RouteId, DateTimeOffset MeasuredAt, DateTimeOffset? AnnotatedAt,
    DateTimeOffset? FirstAnnotatedAt, string State, IReadOnlyList<AnnotationNodeView> Nodes);

public static class AnnotationViews
{
    // No classifier, save, queue or provider call is permitted on this read path.
    public static AnnotationView Read(History history, RouteRun route, string mode, Func<string, NextTraceAddressState>? queryState = null)
    {
        var latest = history.LoadRouteAnnotation(route.Id);
        var first = history.LoadRouteAnnotation(route.Id, original: true);
        var now = DateTimeOffset.UtcNow;
        var nodes = latest?.Nodes.Select(n =>
        {
            var data = n.Metadata;
            string state = NodeMetadataClient.LocalLabel(n.Address) is not null ? "local" :
                data is null ? (mode == "offline" ? "offline" : "pending") :
                data.Success ? (data.Expires <= now ? "stale" : "cached") :
                string.IsNullOrEmpty(data.QueryState) ? "failed" : data.QueryState;
            var attempt = queryState?.Invoke(n.Address);
            return new AnnotationNodeView(n.Ttl, n.Address,
                data is { Success: true } ? string.Join(" / ", new[] { data.Country, data.Region, data.City }.Where(s => !string.IsNullOrEmpty(s)).Distinct()) : "",
                data?.Asn, data?.Organization ?? "", data?.ProviderId ?? "",
                data?.Queried, data?.Expires, state, data is { Success: true } && data.Expires <= now,
                attempt?.LastAttempt, attempt?.Status ?? "unattempted");
        }).ToArray() ?? [];
        string state = latest is null ? "pending" : nodes.Any(n => n.State == "stale") ? "stale" :
            latest.PendingQueries > 0 || nodes.Any(n => n.State is "failed" or "timeout" or "authentication-blocked" or "rate-limited") ? (mode == "offline" ? "offline" : "partial") : "complete";
        return new(route.Id, route.Finished, latest?.Time, first?.Time, state, nodes);
    }
}
