using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using System.Security.Cryptography;

namespace TcpLatencyMonitor.Core;

public sealed record Backbone(long Asn,string Name);
public sealed record BackboneRules(string Version,string Source,List<Backbone> Networks)
{
    public List<PrefixRule> Prefixes {get;init;}=[];
}
[JsonSerializable(typeof(BackboneRules))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive=true)]
internal partial class BackboneJson:JsonSerializerContext{}
public static class BackboneCatalog
{
    private static readonly BackboneRules Rules=Load();
    private static readonly PrefixCatalog Prefixes=new(Rules.Prefixes);
    public static string Version=>Rules.Version;
    public static string Source=>Rules.Source;
    public static string? Name(long? asn)=>Rules.Networks.FirstOrDefault(n=>n.Asn==asn)?.Name;
    public static PrefixRule? Prefix(string address)=>Prefixes.Match(address);
    private static BackboneRules Load()
    {
        using var stream=typeof(BackboneCatalog).Assembly.GetManifestResourceStream("BackboneCatalog.json")!;
        var rules=JsonSerializer.Deserialize(stream,BackboneJson.Default.BackboneRules)!;
        if(rules.Networks.Any(n=>n.Asn<=0||string.IsNullOrWhiteSpace(n.Name))||rules.Networks.Select(n=>n.Asn).Distinct().Count()!=rules.Networks.Count)throw new InvalidDataException("Invalid backbone catalog");
        return rules;
    }
}
public sealed record AnnotatedNode(int Ttl,string Address,bool IsTarget,string Name,string Evidence,NodeMetadata? Metadata)
{
    public NodeIdentity? Identity {get;init;}
}
public sealed record RouteAnnotation(string Id,string RouteId,DateTimeOffset Time,string Origin,string RuleVersion,string RuleSource,
    string Summary,string Sequence,int UnknownHops,int MissingMetadata,List<AnnotatedNode> Nodes)
{
    public string InterpretationVersion {get;init;}="";
    public string EvidenceKey {get;init;}="";
    public string LocationSequence {get;init;}="";
    public int PublicNodes {get;init;}
    public int LocatedNodes {get;init;}
    public int CityNodes {get;init;}
    public int MissingAsn {get;init;}
    public int Conflicts {get;init;}
    public int PendingQueries {get;init;}
}
public static class RouteClassifier
{
    public const string InterpretationVersion="2.6.0";
    public static RouteAnnotation Classify(RouteRun route,IReadOnlyDictionary<string,NodeMetadata> metadata,DateTimeOffset now,string origin)
    {
        var nodes=new List<AnnotatedNode>();var normalized=new Dictionary<string,NodeMetadata>();int unknown=0;
        foreach(var (ip,data) in metadata)if(IPAddress.TryParse(ip,out _))normalized[CidrBlock.Normalize(ip)]=data;
        foreach(var hop in route.Probes.GroupBy(p=>p.Ttl).OrderBy(g=>g.Key))
        {
            var addresses=hop.Where(p=>p.Address is not null&&p.Status is 0 or 11013).Select(p=>CidrBlock.Normalize(p.Address!)).Distinct().Order(StringComparer.Ordinal).ToArray();
            if(addresses.Length==0){unknown++;continue;}
            foreach(var address in addresses)
            {
                bool target=address==CidrBlock.Normalize(route.Address)&&hop.Any(p=>p.Status==0&&p.Address is not null&&CidrBlock.Normalize(p.Address)==address);
                normalized.TryGetValue(address,out var data);nodes.Add(NodeClassifier.Classify(hop.Key,address,target,data,now));
            }
        }
        string NetworkSummary(IGrouping<string?,AnnotatedNode> group)
        {
            var first=group.First();bool addressOnly=group.All(n=>n.Identity!.Kind is "prefix" or "prefix-with-asn");
            bool stale=group.All(n=>n.Metadata is {Success:true} data&&data.Expires<=now)&&!addressOnly;
            return first.Identity!.Network+(addressOnly?"（地址段归属）":"")+(stale?"（旧缓存）":"");
        }
        var networks=nodes.Where(n=>!n.IsTarget&&n.Identity is {Backbone:true,Conflict:false}).GroupBy(n=>n.Identity!.NetworkKey).Select(NetworkSummary).ToArray();
        string summary=networks.Length>0?"可见中间节点："+string.Join(" · ",networks):"暂无可识别的骨干网络中间节点";
        int conflicts=nodes.Count(n=>n.Identity!.Conflict);if(conflicts>0)summary+=$"；{conflicts} 个节点归属待核对";
        var ends=nodes.Where(n=>n.IsTarget&&n.Identity is {Network:not null,Conflict:false}).GroupBy(n=>n.Identity!.NetworkKey).Select(NetworkSummary).ToArray();
        if(ends.Length>0)summary+="；目标归属："+string.Join(" / ",ends);
        var publicNodes=nodes.Where(n=>!n.Identity!.Local).ToArray();
        var result=new RouteAnnotation(Guid.NewGuid().ToString("N"),route.Id,now,origin,BackboneCatalog.Version,BackboneCatalog.Source,summary,
            BuildSequence(route,nodes,false),unknown,publicNodes.Count(n=>n.Identity is {Network:null,Conflict:false}),nodes)
        {
            InterpretationVersion=InterpretationVersion,LocationSequence=BuildSequence(route,nodes,true),PublicNodes=publicNodes.Length,
            LocatedNodes=publicNodes.Count(n=>n.Identity!.Location.Level!="unknown"),CityNodes=publicNodes.Count(n=>n.Identity!.Location.Level=="city"),
            MissingAsn=publicNodes.Count(n=>n.Metadata is not {Success:true,Asn:>0}),Conflicts=conflicts,
            PendingQueries=publicNodes.Where(n=>n.Metadata is null||n.Metadata.Expires<=now).Select(n=>n.Address).Distinct().Count()
        };
        // Exclude processing timestamps and presentation origin; include all frozen evidence and algorithm versions.
        var canonical=result with{Id="",Time=DateTimeOffset.UnixEpoch,Origin="",EvidenceKey=""};
        return result with{EvidenceKey=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical,DataJson.Default.RouteAnnotation)))};
    }
    private static string BuildSequence(RouteRun route,List<AnnotatedNode> nodes,bool geo)
    {
        var segments=new List<string>();string? previous=null;int missing=0,lastTtl=0;
        var ambiguous=nodes.Where(n=>n.Identity!.Location.Level!="unknown").GroupBy(n=>n.Identity!.Location.Label)
            .Where(g=>g.Select(n=>n.Identity!.Location.Key).Distinct().Count()>1).Select(g=>g.Key).ToHashSet();
        void FlushMissing(){if(missing>0){segments.Add(geo?$"［{missing} 跳未回应］":$"?（{missing} 跳未回应）");missing=0;} }
        foreach(int ttl in route.Probes.Select(p=>p.Ttl).Distinct().Order())
        {
            if(ttl>lastTtl+1){FlushMissing();segments.Add($"［{ttl-lastTtl-1} 跳无记录］");previous=null;}lastTtl=ttl;
            var hop=nodes.Where(n=>n.Ttl==ttl).ToArray();
            if(hop.Length==0){missing++;previous=null;continue;}FlushMissing();
            string Label(AnnotatedNode n)
            {
                var identity=n.Identity!;string name=n.Name;
                if(identity.Kind is "prefix" or "prefix-with-asn")name+="（地址段）";
                else if(n.Evidence.Contains("缓存过期"))name+="（旧缓存）";
                if(geo&&!identity.Local)
                {
                    string place=identity.Location.Label;
                    if(ambiguous.Contains(place)&&n.Metadata is not null)
                        place=string.Join(" / ",new[]{n.Metadata.Country,n.Metadata.Region,place}.Where(s=>s.Length>0).Distinct());
                    name=place+" · "+name;
                }
                return name+(n.IsTarget?"（目标）":"");
            }
            var labels=hop.Select(Label).Distinct().ToArray();string label=string.Join(" / ",labels);
            bool collapsible=hop.Length==1&&!hop[0].IsTarget&&hop[0].Identity is {Conflict:false,Kind:not "unknown"}&&
                (!geo||hop[0].Identity!.Local||hop[0].Identity!.Location.Level!="unknown"&&!hop[0].Identity!.Location.Conflict);
            string key=label+"|"+hop[0].Identity!.Location.Key+"|"+hop[0].Identity!.Kind;
            if(!collapsible||key!=previous)segments.Add(hop.Length>1?"{"+label+(labels.Length==1?$" · {hop.Length} 个响应 IP":"")+"}":label);
            previous=collapsible?key:null;
        }
        FlushMissing();return string.Join(" → ",segments);
    }
}

// The database is the recovery source; the bounded queue is only a fast notification path.
public sealed class RouteAnnotationService : IAsyncDisposable
{
    private sealed record Job(RouteRun Route, bool Viewed, TaskCompletionSource<RouteAnnotation?> Completion, int Generation);
    private readonly History _history;
    private readonly NodeMetadataClient _client;
    private readonly Channel<Job> _queue;
    private readonly Dictionary<string, Job> _pending = new();
    private readonly object _sync = new();
    private readonly object _pendingSync = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _active, _maintenanceActive;
    private readonly Task _worker, _maintenance;
    private readonly int _budgetMilliseconds;
    private readonly Func<IReadOnlyCollection<string>?>? _targetKeys;
    private int _mode, _generation, _recoveryOffset;
    private volatile bool _closed;
    public event Action? Refreshed;
    public event Action<RouteAnnotation>? Saved;
    public event Action<string>? Failed;
    public RouteAnnotationService(History history, NodeMetadataClient client, int budgetMilliseconds = 20000,
        Func<IReadOnlyCollection<string>?>? targetKeys = null, int queueCapacity = 128)
    {
        if (budgetMilliseconds is <50 or >20000) throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));
        _history = history; _client = client; _budgetMilliseconds = budgetMilliseconds; _targetKeys = targetKeys;
        _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(Math.Clamp(queueCapacity, 1, 128))
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(WorkAsync); _maintenance = Task.Run(MaintainAsync);
    }
    public int Mode { get => Volatile.Read(ref _mode); set { lock (_sync) { _mode = Math.Clamp(value, 0, 2); CancelRequests(); } } }
    public void CancelRequests() { lock (_sync) { _generation++; _active?.Cancel(); _maintenanceActive?.Cancel(); } }
    public Task<RouteAnnotation?> Request(RouteRun route, bool viewed)
    {
        lock (_pendingSync)
        {
            if (_closed) return Task.FromResult<RouteAnnotation?>(null);
            int generation = Volatile.Read(ref _generation);
            if (_pending.TryGetValue(route.Id, out var old) && old.Generation == generation) return old.Completion.Task;
            var job = new Job(route, viewed, new(TaskCreationOptions.RunContinuationsAsynchronously), generation);
            if (!_queue.Writer.TryWrite(job)) return Task.FromResult<RouteAnnotation?>(null);
            _pending[route.Id] = job; return job.Completion.Task;
        }
    }
    // Startup and periodic bounded recovery also recover crashes between raw commit and event delivery.
    public void RecoverRecent()
    {
        if (_stop.IsCancellationRequested) return;
        var candidates = _history.LoadRoutesNeedingAnnotation(DateTimeOffset.UtcNow,
            targetKeys: _targetKeys?.Invoke() ?? Array.Empty<string>(), includeIncomplete: Mode != 0, limit: 512, refreshHours: _client.Options.RefreshHours);
        if (candidates.Count == 0) return;
        int start = Math.Abs(Interlocked.Add(ref _recoveryOffset, 127) % candidates.Count);
        for (int n = 0; n < candidates.Count; n++) _ = Request(candidates[(start + n) % candidates.Count], false);
    }
    private async Task MaintainAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    RecoverRecent();
                    // Preserve desktop maintenance behavior; the server uses recovered route jobs only.
                    if (_targetKeys is null && Mode == 2)
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        budget.CancelAfter(_budgetMilliseconds);
                        lock (_sync) { if (Mode != 2 || _closed) continue; _maintenanceActive = budget; }
                        try { if (await _client.RefreshRecentAsync(budget.Token).ConfigureAwait(false) > 0) Refreshed?.Invoke(); }
                        catch (OperationCanceledException) { }
                        finally { lock (_sync) _maintenanceActive = null; }
                    }
                }
                catch (Exception) { Failed?.Invoke("定位注释恢复失败；稍后重试"); }
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    private bool Current(Job job) => !_closed && job.Generation == _generation;
    private async Task WorkAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync())
        {
            try
            {
                lock (_sync) { if (!Current(job)) { job.Completion.TrySetResult(null); continue; } }
                var ips = job.Route.Probes.Where(p => p.Address is not null && p.Status is 0 or 11013)
                    .Select(p => p.Address!).Where(ip => IPAddress.TryParse(ip, out _)).Select(CidrBlock.Normalize).Distinct().ToArray();
                var metadata = new Dictionary<string, NodeMetadata>();
                foreach (var ip in ips) if (_history.LoadNodeMetadata(ip) is {} cached) metadata[ip] = cached;
                RouteAnnotation? local;
                lock (_sync)
                {
                    if (!Current(job)) { job.Completion.TrySetResult(null); continue; }
                    local = _history.SaveRouteAnnotation(RouteClassifier.Classify(job.Route, metadata, DateTimeOffset.UtcNow, "后台本地解释"));
                    if (local is not null) Saved?.Invoke(local);
                }
                if (local is null) { job.Completion.TrySetResult(null); continue; }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                budget.CancelAfter(_budgetMilliseconds);
                lock (_sync)
                {
                    if (!Current(job)) { job.Completion.TrySetResult(null); continue; }
                    _active = budget;
                }
                try
                {
                    // One due IP per route slice: rotate targets and least-recently-attempted IPs.
                    // No second network maintenance worker competes for the same 20-second budget.
                    var candidates = _targetKeys is null ? ips.AsEnumerable() : _client.OrderNextTraceCandidates(ips)
                        .Where(ip => _client.NeedsRefresh(ip, DateTimeOffset.UtcNow)).Take(1);
                    foreach (var candidate in candidates)
                    {
                        bool online;
                        lock (_sync) online = Current(job) && !budget.IsCancellationRequested && (Mode == 2 || Mode == 1 && job.Viewed);
                        if (!online) break;
                        metadata[candidate] = await _client.GetAsync(candidate, true, budget.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally { lock (_sync) _active = null; }
                lock (_sync)
                {
                    if (!Current(job)) { job.Completion.TrySetResult(null); continue; }
                    var saved = _history.SaveRouteAnnotation(RouteClassifier.Classify(job.Route, metadata, DateTimeOffset.UtcNow, "后台补全解释"));
                    job.Completion.TrySetResult(saved);
                    if (saved is not null && saved.Id != local.Id) Saved?.Invoke(saved);
                }
                Refreshed?.Invoke();
            }
            catch (Exception) { job.Completion.TrySetResult(null); Failed?.Invoke("定位注释暂不可用；原始测量保留"); }
            finally { lock (_pendingSync) if (_pending.TryGetValue(job.Route.Id, out var current) && ReferenceEquals(current, job)) _pending.Remove(job.Route.Id); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_closed) return; _closed = true; _generation++; _stop.Cancel(); _active?.Cancel(); _maintenanceActive?.Cancel(); _queue.Writer.TryComplete(); }
        await Task.WhenAll(_worker, _maintenance).ConfigureAwait(false); _stop.Dispose();
    }
}
