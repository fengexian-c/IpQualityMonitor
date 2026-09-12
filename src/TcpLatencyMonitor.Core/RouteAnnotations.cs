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

// One worker, bounded jobs, and a shared client: no HTTP work runs on the probe writer or UI thread.
public sealed class RouteAnnotationService: IAsyncDisposable
{
    private sealed record Job(RouteRun Route,bool Viewed,TaskCompletionSource<RouteAnnotation?> Completion,string Key,int Generation);
    private readonly History _history;private readonly NodeMetadataClient _client;
    private readonly Channel<Job> _queue=Channel.CreateBounded<Job>(128);
    private readonly int _budgetMilliseconds;
    private readonly Dictionary<string,Task<RouteAnnotation?>> _pending=new();private readonly object _sync=new();
    private readonly CancellationTokenSource _stop=new();private CancellationTokenSource? _active;
    private readonly Task _worker,_maintenance;private int _mode,_generation;
    private CancellationTokenSource? _maintenanceActive;
    public event Action? Refreshed;
    public event Action<RouteAnnotation>? Saved;
    public event Action<string>? Failed;
    public RouteAnnotationService(History history,NodeMetadataClient client,int budgetMilliseconds=20000)
    {if(budgetMilliseconds is <50 or >20000)throw new ArgumentOutOfRangeException(nameof(budgetMilliseconds));_history=history;_client=client;_budgetMilliseconds=budgetMilliseconds;_worker=Task.Run(WorkAsync);_maintenance=Task.Run(MaintainAsync);}
    public int Mode {get=>Volatile.Read(ref _mode);set{lock(_sync){Volatile.Write(ref _mode,Math.Clamp(value,0,2));CancelRequests();}}}
    public void CancelRequests(){lock(_sync){Interlocked.Increment(ref _generation);_active?.Cancel();_maintenanceActive?.Cancel();}}
    private async Task MaintainAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30),_stop.Token).ConfigureAwait(false);
                if(Mode!=2)continue;
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);budget.CancelAfter(TimeSpan.FromSeconds(20));
                lock(_sync){if(Mode!=2)continue;_maintenanceActive=budget;}
                bool changed=false;
                try{changed=await _client.RefreshRecentAsync(budget.Token).ConfigureAwait(false)>0;}
                catch(OperationCanceledException){changed=true;}
                catch(Exception){Failed?.Invoke("后台定位缓存刷新失败；稍后重试");}
                finally{lock(_sync)_maintenanceActive=null;}
                if(changed&&!_stop.IsCancellationRequested)Refreshed?.Invoke();
            }
        }
        catch(OperationCanceledException){}
    }
    public Task<RouteAnnotation?> Request(RouteRun route,bool viewed)
    {
        lock(_sync)
        {
            string key=route.Id+(viewed?"view":"auto")+_generation;if(_pending.TryGetValue(key,out var old))return old;
            var completion=new TaskCompletionSource<RouteAnnotation?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(!_queue.Writer.TryWrite(new(route,viewed,completion,key,_generation))){completion.SetResult(null);return completion.Task;}
            _pending.Add(key,completion.Task);return completion.Task;
        }
    }
    private async Task WorkAsync()
    {
        await foreach(var job in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if(_stop.IsCancellationRequested){job.Completion.TrySetResult(null);continue;}
                var metadata=new Dictionary<string,NodeMetadata>();var ips=job.Route.Probes.Where(p=>p.Address is not null&&p.Status is 0 or 11013).Select(p=>p.Address!).Distinct().ToArray();
                foreach(var ip in ips)if(_history.LoadNodeMetadata(ip) is NodeMetadata cache)metadata[ip]=cache;
                var local=_history.SaveRouteAnnotation(RouteClassifier.Classify(job.Route,metadata,DateTimeOffset.UtcNow,
                    job.Viewed?"查看时本地解释":DateTimeOffset.UtcNow-job.Route.Finished>TimeSpan.FromSeconds(45)?"后台延后本地解释":"采集时本地注释"));
                Saved?.Invoke(local);
                // Waiting behind other targets does not consume this route's lookup budget.
                using var budget=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);budget.CancelAfter(_budgetMilliseconds);
                lock(_sync)_active=budget;
                try
                {
                    foreach(var ip in ips)
                    {
                        if(NodeMetadataClient.LocalLabel(ip) is not null)continue;
                        bool online=job.Generation==Volatile.Read(ref _generation)&&!budget.IsCancellationRequested&&(Mode==2||Mode==1&&job.Viewed);
                        if(!online)break;
                        metadata[ip]=await _client.GetAsync(ip,true,budget.Token).ConfigureAwait(false);
                    }
                }
                catch(OperationCanceledException){}
                finally{lock(_sync)_active=null;}
                string origin=job.Viewed?"查看时补全":DateTimeOffset.UtcNow-job.Route.Finished>TimeSpan.FromSeconds(45)?"后台延后补全":"采集时注释";
                var result=RouteClassifier.Classify(job.Route,metadata,DateTimeOffset.UtcNow,origin);
                var saved=_history.SaveRouteAnnotation(result);job.Completion.TrySetResult(saved);if(saved.Id!=local.Id)Saved?.Invoke(saved);
            }
            catch(Exception ex){job.Completion.TrySetResult(null);Failed?.Invoke(ex.Message);}
            finally{lock(_sync)_pending.Remove(job.Key);}
        }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();_queue.Writer.TryComplete();await Task.WhenAll(_worker,_maintenance).ConfigureAwait(false);_stop.Dispose();}
}
