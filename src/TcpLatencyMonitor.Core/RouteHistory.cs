using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TcpLatencyMonitor.Core;

public sealed record RouteObservedAddress(string Address,int Replies,double? RttMs,bool Supplemental);
public sealed record RouteObservedHop(int Ttl,int Attempts,int Replies,int Timeouts,int Errors,IReadOnlyList<RouteObservedAddress> Addresses)
{
    public bool Silent=>Attempts>0&&Timeouts==Attempts;
}

// Compact read model: never keeps the original per-probe JSON or annotation trees alive.
public sealed record RouteObservation(string Id,string ExecutionId,string TargetKey,string Address,string Scope,string Context,
    DateTimeOffset Started,DateTimeOffset Finished,bool Reached,string Outcome,IReadOnlyDictionary<int,RouteObservedHop> Hops,string Signature)
{
    public int Known=>Hops.Values.Count(h=>h.Addresses.Count>0);
    public int? DestinationTtl {get;init;}
    public int LastTtl=>Hops.Count==0?0:Hops.Keys.Max();
    public bool Eligible=>Known>=3||(Reached&&Hops.Count>0&&Known==Hops.Count&&Hops.Keys.Min()==1);
    public static RouteObservation From(RouteRun run)
    {
        var hops=new SortedDictionary<int,RouteObservedHop>();
        foreach(var g in run.Probes.Where(p=>p.Ttl is >=1 and <=64).GroupBy(p=>p.Ttl))
        {
            var valid=g.Where(p=>p.Status is 0 or 11013&&System.Net.IPAddress.TryParse(p.Address,out _)).ToArray();
            var addresses=valid.GroupBy(p=>CidrBlock.Normalize(p.Address!)).OrderBy(a=>a.Key,StringComparer.Ordinal).Select(a=>
            {
                var times=a.Where(p=>p.RttMs is double r&&double.IsFinite(r)&&r>=0).Select(p=>p.RttMs!.Value).ToArray();
                return new RouteObservedAddress(a.Key,a.Count(),times.Length==0?null:times.Average(),a.Any(p=>p.IsSupplemental));
            }).ToArray();
            hops[g.Key]=new(g.Key,g.Count(),valid.Length,g.Count(p=>p.Status==11010),g.Count()-valid.Length-g.Count(p=>p.Status==11010),addresses);
        }
        string address=CidrBlock.Normalize(run.Address);
        string policy=run.ProbeOptions is {} p?FormattableString.Invariant($"{p.MaxHops}/{p.Queries}/{p.TimeoutMs}/{p.BudgetSeconds}/{p.InitialSpacingMs}/{p.SupplementSpacingMs}/{p.MaxAttemptsPerHop}/{p.SupplementBudgetSeconds}"):"legacy";
        string scope=$"{address}|ICMP|{run.Context}|{run.TimeoutMs}|{run.MaxHops}|{run.BudgetSeconds}|{policy}";
        if(run.Context.Length==0||run.Context=="unknown"||run.Context.StartsWith("unavailable:",StringComparison.Ordinal))scope+="|unresolved:"+run.Id;
        var reached=run.Probes.Where(p=>p.Status==0&&p.Address is not null&&CidrBlock.Normalize(p.Address)==address).Select(p=>p.Ttl).Distinct().Order().ToArray();
        string signature=scope+"|dest="+string.Join(',',reached)+"|"+string.Join(';',hops.Select(h=>$"{h.Key}:{string.Join(',',h.Value.Addresses.Select(a=>a.Address))}"));
        return new(run.Id,run.ProbeExecutionId??run.Id,run.TargetKey,address,scope,run.ContextDescription,run.Started,run.Finished,run.Reached,run.Outcome,hops,signature)
            {DestinationTtl=reached.Length==1?reached[0]:null};
    }
}

public enum RouteMembershipKind { Exact, Compatible, Temporal, Multiple, Unknown }
public sealed record RouteMembership(RouteObservation Observation,string? PatternId,RouteMembershipKind Kind,string? EvidenceId,string? EarlierPatternId)
{
    public string Label=>Kind switch{RouteMembershipKind.Exact=>"签名匹配",RouteMembershipKind.Compatible=>"部分相容",RouteMembershipKind.Temporal=>"时间推定",RouteMembershipKind.Multiple=>"多回应 · 无唯一归属",_=>"证据不足"};
    public bool Revised=>Kind==RouteMembershipKind.Temporal&&EarlierPatternId is not null&&EarlierPatternId!=PatternId;
}
