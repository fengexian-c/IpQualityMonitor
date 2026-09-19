namespace TcpLatencyMonitor.Core;

// Immutable audit fact: computed time is distinct from the original probe time.
public sealed record RouteFirstDecision(string RouteId,string ExecutionId,string TargetKey,string Scope,
    string Algorithm,string InputVersion,DateTimeOffset ComputedAt,DateTimeOffset EvidenceCutoff,
    string? PatternId,string? PatternDescription,RouteMembershipKind Kind,string? EvidenceId,string Origin)
{
    public string SourceStamp {get;init;}="";
    public string Changes(RouteMembership current)
    {
        var changes=new List<string>();
        if(PatternId!=current.PatternId)changes.Add("归属修正");
        if(Kind!=current.Kind)changes.Add("状态变化");
        if(EvidenceId!=current.EvidenceId)changes.Add("依据更新");
        return string.Join(" · ",changes);
    }
}
