using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TcpLatencyMonitor.Core;

public sealed class RoutePattern
{
    public string Id {get;}
    public string Description {get;}
    public string Label=>"路径 "+Id;
    internal RoutePattern(string scope,string path)
    {
        Description=scope+"|"+path;
        Id=RouteHistoryIndex.Hash(Description);
    }
}
public sealed record RouteCellReference(RouteObservation Source,RouteObservedHop Hop,bool Later,bool Temporal);

/// <summary>
/// Raw-witnessed decision tree. Only observations satisfying every ancestor in
/// the SAME execution enter a child. Display membership never edits this tree.
/// Build is the deterministic full-build oracle; filtering is a separate step.
/// </summary>
public sealed class RouteHistoryIndex
{
    public const string AlgorithmVersion="route-history-2";
    public static readonly TimeSpan ReferenceLimit=TimeSpan.FromHours(24);
    public IReadOnlyList<RouteObservation> Observations {get;}
    public IReadOnlyList<RoutePattern> Patterns {get;}
    public IReadOnlyDictionary<string,RouteMembership> Memberships {get;}
    public string Revision {get;}
    public string SourceVersion {get;internal set;}="";
    public IReadOnlyDictionary<string,RouteFirstDecision> FirstDecisions {get;internal set;}=new Dictionary<string,RouteFirstDecision>();
    private readonly Dictionary<string,RouteMembership[]> _evidence;
    private readonly DateTimeOffset[] _boundaries;
    private Dictionary<string,string> _signaturePatterns=new(StringComparer.Ordinal);
    private Dictionary<string,string[]> _ambiguousCandidates=new(StringComparer.Ordinal);
    private readonly Dictionary<(string,int,bool),RouteCellReference?> _references=new();
    private readonly object _referenceLock=new();
    private sealed class Node
    {
        public int Feature;
        public Dictionary<string,Node>? Children;
        public RoutePattern? Pattern;
        public Dictionary<int,string[][]>? Constraints;
        public HashSet<string>? Signatures;
    }
    // Feature values are normalized once, not reconstructed for each comparison.
    private sealed record Row(RouteObservation Observation,Dictionary<int,string[]> Values,Dictionary<int,string> Keys);
    internal static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
    private RouteHistoryIndex(RouteObservation[] observations,List<RoutePattern> patterns,Dictionary<string,RouteMembership> members,DateTimeOffset[] boundaries)
    {
        Observations=observations;Patterns=patterns;Memberships=members;_boundaries=boundaries;
        _evidence=members.Values.Where(m=>m.PatternId is not null&&m.Kind is RouteMembershipKind.Exact or RouteMembershipKind.Compatible)
            .GroupBy(m=>m.PatternId!).ToDictionary(g=>g.Key,g=>g.OrderBy(m=>m.Observation.Finished).ThenBy(m=>m.Observation.Id,StringComparer.Ordinal).ToArray());
        Revision=Hash(AlgorithmVersion+string.Join('|',observations.Select(o=>o.Id+":"+o.Started.Ticks+":"+o.Finished.Ticks+":"+o.Signature+":"+string.Join(';',o.Hops.Select(h=>$"{h.Key}/{h.Value.Attempts}/{h.Value.Replies}/{h.Value.Timeouts}/{h.Value.Errors}"))))+string.Join('|',boundaries.Select(b=>b.ToUnixTimeMilliseconds())));
    }
    private static bool Overlaps(string[] a,string[] b)
    {
        int i=0,j=0;while(i<a.Length&&j<b.Length){int n=StringComparer.Ordinal.Compare(a[i],b[j]);if(n==0)return true;if(n<0)i++;else j++;}return false;
    }
    private static bool Subset(string[] a,string[] b)
    {
        int i=0,j=0;while(i<a.Length&&j<b.Length){int n=StringComparer.Ordinal.Compare(a[i],b[j]);if(n==0){i++;j++;}else if(n>0)j++;else return false;}return i==a.Length;
    }
    internal static bool Compatible(RouteObservation a,RouteObservation b)
    {
        if(a.Scope!=b.Scope||a.DestinationTtl is int x&&b.DestinationTtl is int y&&x!=y)return false;
        foreach(var pair in a.Hops)
        {
            if(pair.Value.Addresses.Count==0||!b.Hops.TryGetValue(pair.Key,out var other)||other.Addresses.Count==0)continue;
            bool subset=true;foreach(var ip in pair.Value.Addresses)if(!other.Addresses.Any(p=>p.Address==ip.Address)){subset=false;break;}
            if(subset)continue;
            foreach(var ip in other.Addresses)if(!pair.Value.Addresses.Any(p=>p.Address==ip.Address))return false;
        }
        return true;
    }
    private static Row Normalize(RouteObservation observation)
    {
        var values=new Dictionary<int,string[]>();var keys=new Dictionary<int,string>();
        if(observation.DestinationTtl is int ttl){values[0]=[ttl.ToString(CultureInfo.InvariantCulture)];keys[0]=values[0][0];}
        foreach(var (hop,item) in observation.Hops)if(item.Addresses.Count>0)
        {var ips=item.Addresses.Select(a=>a.Address).Order(StringComparer.Ordinal).ToArray();values[hop]=ips;keys[hop]=string.Join(',',ips);}
        return new(observation,values,keys);
    }
    private static Node BuildNode(List<Row> rows,string scope,string path,HashSet<int> used,List<RoutePattern> patterns,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Earliest second witness wins. Rows are chronological; TTL breaks ties.
        // Nested sets alone are not a new branch (a,b does not invent singleton b).
        var seen=new Dictionary<int,Dictionary<string,string[]>>();int? feature=null;
        foreach(var row in rows)
        {
            foreach(var (ttl,value) in row.Values.OrderBy(p=>p.Key))
            {
                if(used.Contains(ttl))continue;
                if(!seen.TryGetValue(ttl,out var sets))seen[ttl]=sets=new(StringComparer.Ordinal);
                string key=row.Keys[ttl];if(sets.ContainsKey(key))continue;
                if(sets.Values.Any(previous=>!Subset(value,previous)&&!Subset(previous,value))){feature=ttl;break;}
                sets[key]=value;
            }
            if(feature.HasValue)break;
        }
        if(feature is not int discriminator)
        {
            var pattern=new RoutePattern(scope,path);patterns.Add(pattern);
            var constraints=new Dictionary<int,string[][]>();
            foreach(var ttl in rows.SelectMany(r=>r.Values.Keys).Distinct())
                constraints[ttl]=rows.Where(r=>r.Values.ContainsKey(ttl)).DistinctBy(r=>r.Keys[ttl]).Select(r=>r.Values[ttl]).ToArray();
            return new(){Pattern=pattern,Constraints=constraints,Signatures=rows.Select(r=>r.Observation.Signature).ToHashSet(StringComparer.Ordinal)};
        }
        var all=new Dictionary<string,string[]>(StringComparer.Ordinal);
        foreach(var row in rows)if(row.Values.TryGetValue(discriminator,out var value))all.TryAdd(row.Keys[discriminator],value);
        var minimal=new Dictionary<string,string[]>(StringComparer.Ordinal);
        foreach(var pair in all.OrderBy(p=>p.Value.Length).ThenBy(p=>p.Key,StringComparer.Ordinal))
        {
            // The common all-singleton case is O(n), including thousands of paths.
            if(pair.Value.Length>1&&minimal.Values.Any(v=>Subset(v,pair.Value)))continue;
            minimal.Add(pair.Key,pair.Value);
        }
        var partitions=minimal.Keys.ToDictionary(k=>k,_=>new List<Row>(),StringComparer.Ordinal);
        foreach(var row in rows)if(row.Keys.TryGetValue(discriminator,out var key)&&partitions.TryGetValue(key,out var group))group.Add(row);
        var children=new Dictionary<string,Node>(StringComparer.Ordinal);var nextUsed=new HashSet<int>(used){discriminator};
        foreach(var (key,group) in partitions)
            children[key]=BuildNode(group,scope,path+$"/{discriminator}={key}",nextUsed,patterns,token);
        return new(){Feature=discriminator,Children=children};
    }
    private static bool LeafCompatible(Node leaf,Row row)
    {
        foreach(var (ttl,values) in leaf.Constraints!)
            if(row.Values.TryGetValue(ttl,out var observed)&&!values.Any(v=>Subset(v,observed)||Subset(observed,v)))return false;
        return true;
    }
    private static bool MissingIsTimeout(Node node,Row row)
    {
        if(node.Feature>0)return row.Observation.Hops.TryGetValue(node.Feature,out var hop)&&hop.Silent;
        // Missing destination-TTL evidence is inferable only when every possible
        // actual destination position was probed and timed out.
        return node.Children!.Keys.All(k=>int.TryParse(k,out int ttl)&&row.Observation.Hops.TryGetValue(ttl,out var hop)&&hop.Silent);
    }
    private static void Candidates(Node node,Row row,List<Node> leaves,ref bool inferred,ref bool invalid,ref bool multiple)
    {
        if(node.Pattern is not null){if(LeafCompatible(node,row))leaves.Add(node);return;}
        if(row.Keys.TryGetValue(node.Feature,out var key))
        {
            if(node.Children!.TryGetValue(key,out var exact)){Candidates(exact,row,leaves,ref inferred,ref invalid,ref multiple);return;}
            // A non-exact multi-response set must not select a singleton branch.
            multiple|=row.Values[node.Feature].Length>1;
            return;
        }
        inferred=true;
        if(!MissingIsTimeout(node,row)){invalid=true;return;}
        foreach(var child in node.Children!.Values)Candidates(child,row,leaves,ref inferred,ref invalid,ref multiple);
    }
    public static RouteHistoryIndex Build(IEnumerable<RouteObservation> source,CancellationToken token=default,IEnumerable<DateTimeOffset>? boundaries=null)
    {
        token.ThrowIfCancellationRequested();
        var observations=source.GroupBy(o=>(o.ExecutionId,o.Scope)).Select(g=>g.MinBy(o=>o.Id,StringComparer.Ordinal)!)
            .OrderBy(o=>o.Finished).ThenBy(o=>o.Id,StringComparer.Ordinal).ToArray();
        var rows=observations.Select(Normalize).ToArray();var patterns=new List<RoutePattern>();var roots=new Dictionary<string,Node>();
        foreach(var scope in rows.Where(r=>r.Observation.Eligible).GroupBy(r=>r.Observation.Scope))
            roots[scope.Key]=BuildNode(scope.DistinctBy(r=>r.Observation.Signature).ToList(),scope.Key,"root",new(),patterns,token);
        var members=new Dictionary<string,RouteMembership>();var ambiguous=new List<(RouteObservation,Node[])>();
        foreach(var row in rows)
        {
            token.ThrowIfCancellationRequested();var o=row.Observation;var candidates=new List<Node>();bool inferred=false,invalid=false,multiple=false;
            if(o.Eligible&&roots.TryGetValue(o.Scope,out var root))Candidates(root,row,candidates,ref inferred,ref invalid,ref multiple);
            if(!inferred&&!invalid&&!multiple&&candidates.Count==1)
            {var p=candidates[0];members[o.Id]=new(o,p.Pattern!.Id,p.Signatures!.Contains(o.Signature)?RouteMembershipKind.Exact:RouteMembershipKind.Compatible,null,null);}
            else
            {
                members[o.Id]=new(o,null,multiple?RouteMembershipKind.Multiple:RouteMembershipKind.Unknown,null,null);
                if(inferred&&!invalid&&!multiple&&candidates.Count>0)ambiguous.Add((o,candidates.ToArray()));
            }
        }
        var index=new RouteHistoryIndex(observations,patterns,members,boundaries?.Distinct().Order().ToArray()??[]);
        index._signaturePatterns=members.Values.Where(m=>m.Kind is RouteMembershipKind.Exact or RouteMembershipKind.Compatible&&m.PatternId is not null)
            .DistinctBy(m=>m.Observation.Signature).ToDictionary(m=>m.Observation.Signature,m=>m.PatternId!,StringComparer.Ordinal);
        index._ambiguousCandidates=ambiguous.ToDictionary(p=>p.Item1.Id,p=>p.Item2.Select(n=>n.Pattern!.Id).ToArray());
        index.ResolveTemporal(members,token);
        return index;
    }
    private void ResolveTemporal(Dictionary<string,RouteMembership> members,CancellationToken token)
    {
        foreach(var (id,candidates) in _ambiguousCandidates)
        {
            token.ThrowIfCancellationRequested();var o=members[id].Observation;RouteMembership? chosen=null,earlier=null;
            foreach(var pattern in candidates)foreach(var donor in NearestEvidence(pattern,o))
            {
                if(chosen is null||CompareDistance(donor.Observation,chosen.Observation,o)<0)chosen=donor;
                if(donor.Observation.Finished<=o.Finished&&(earlier is null||CompareDistance(donor.Observation,earlier.Observation,o)<0))earlier=donor;
            }
            members[id]=chosen is null?new(o,null,RouteMembershipKind.Unknown,null,null):new(o,chosen.PatternId,RouteMembershipKind.Temporal,chosen.Observation.Id,earlier?.PatternId);
        }
    }
    /// <summary>Copy-on-publish fast path; no branch discovery for already witnessed
    /// signatures. Existing ambiguous dependents are reconsidered against new real
    /// evidence. Out-of-order data, boundary edits, deletes and novel signatures use Build.</summary>
    internal RouteHistoryIndex? TryAppendUnchanged(IReadOnlyList<RouteObservation> additions,IEnumerable<DateTimeOffset> boundaries,CancellationToken token)
    {
        if(additions.Count==0||Observations.Count==0||!_boundaries.SequenceEqual(boundaries.Distinct().Order()))return null;
        var last=Observations[^1];var executions=Observations.Select(o=>(o.ExecutionId,o.Scope)).ToHashSet();
        foreach(var o in additions)
            if(!o.Eligible||o.Finished<=last.Finished||!_signaturePatterns.ContainsKey(o.Signature)||!executions.Add((o.ExecutionId,o.Scope)))return null;
        var observations=Observations.Concat(additions.OrderBy(o=>o.Finished).ThenBy(o=>o.Id,StringComparer.Ordinal)).ToArray();
        var members=new Dictionary<string,RouteMembership>(Memberships);
        foreach(var o in additions){token.ThrowIfCancellationRequested();members.Add(o.Id,new(o,_signaturePatterns[o.Signature],RouteMembershipKind.Exact,null,null));}
        var index=new RouteHistoryIndex(observations,Patterns.ToList(),members,_boundaries)
        {_signaturePatterns=_signaturePatterns,_ambiguousCandidates=_ambiguousCandidates};
        index.ResolveTemporal(members,token);return index;
    }
    private static int CompareDistance(RouteObservation a,RouteObservation b,RouteObservation current)
    {
        int result=(a.Finished-current.Finished).Duration().CompareTo((b.Finished-current.Finished).Duration());
        if(result==0)result=a.Finished.CompareTo(b.Finished);return result==0?StringComparer.Ordinal.Compare(a.Id,b.Id):result;
    }
    private static int LowerBound<T>(IReadOnlyList<T> items,DateTimeOffset time,Func<T,DateTimeOffset> date)
    {
        int low=0,high=items.Count;while(low<high){int mid=low+(high-low)/2;if(date(items[mid])<time)low=mid+1;else high=mid;}return low;
    }
    private IEnumerable<RouteMembership> NearestEvidence(string id,RouteObservation current)
    {
        if(!_evidence.TryGetValue(id,out var entries))yield break;
        int at=LowerBound(entries,current.Finished,m=>m.Observation.Finished);
        for(int direction=-1;direction<=1;direction+=2)
        {
            int start=direction<0?at-1:at;
            if(direction<0&&start>=0)start=LowerBound(entries,entries[start].Observation.Finished,m=>m.Observation.Finished);
            for(int i=start;i>=0&&i<entries.Length;i+=direction)
            {
                var m=entries[i];if(m.Observation.Id==current.Id)continue;
                if((m.Observation.Finished-current.Finished).Duration()>ReferenceLimit)break;
                if(!Barrier(current,m.Observation,id))yield return m;
                break;
            }
        }
    }
    private bool Barrier(RouteObservation current,RouteObservation source,string patternId)
    {
        var low=current.Finished<source.Finished?current.Finished:source.Finished;
        var high=current.Finished>source.Finished?current.Finished:source.Finished;
        int b=LowerBound(_boundaries,low.AddTicks(1),x=>x);if(b<_boundaries.Length&&_boundaries[b]<=high)return true;
        for(int i=LowerBound(Observations,low.AddTicks(1),o=>o.Finished);i<Observations.Count&&Observations[i].Finished<high;i++)
        {
            var o=Observations[i];if(o.Address!=current.Address)continue;
            if(o.Scope!=current.Scope||!Compatible(o,source))return true;
            var m=Memberships[o.Id];if(m.Kind is RouteMembershipKind.Exact or RouteMembershipKind.Compatible&&m.PatternId!=patternId)return true;
        }
        return false;
    }
    public RouteCellReference? Reference(RouteMembership member,int ttl,bool allowLater=true)
    {
        lock(_referenceLock)
        {
            var key=(member.Observation.Id,ttl,allowLater);if(_references.TryGetValue(key,out var cached))return cached;
            var result=FindReference(member,ttl,allowLater);
            // Only visible projections are cached. Bound memory even during long browsing.
            if(_references.Count>=4096)_references.Clear();_references[key]=result;return result;
        }
    }
    private RouteCellReference? FindReference(RouteMembership member,int ttl,bool allowLater)
    {
        var current=member.Observation;
        if(member.PatternId is null||!current.Hops.TryGetValue(ttl,out var missing)||!missing.Silent||!_evidence.TryGetValue(member.PatternId,out var entries))return null;
        RouteMembership? chosen=null;RouteObservedHop? chosenHop=null;bool conflict=false;
        int from=LowerBound(entries,current.Finished-ReferenceLimit,m=>m.Observation.Finished);
        for(int i=from;i<entries.Length&&entries[i].Observation.Finished<=current.Finished+ReferenceLimit;i++)
        {
            var donor=entries[i];var o=donor.Observation;
            if(o.Id==current.Id||!allowLater&&o.Finished>current.Finished||!o.Hops.TryGetValue(ttl,out var hop)||hop.Addresses.Count==0||Barrier(current,o,member.PatternId))continue;
            if(chosenHop is not null&&!hop.Addresses.Select(a=>a.Address).SequenceEqual(chosenHop.Addresses.Select(a=>a.Address)))conflict=true;
            if(chosen is null||CompareDistance(o,chosen.Observation,current)<0){chosen=donor;chosenHop=hop;}
        }
        if(chosen is null||conflict)return null;
        return new(chosen.Observation,chosenHop!,chosen.Observation.Finished>current.Finished,member.Kind==RouteMembershipKind.Temporal);
    }
    public IReadOnlyList<RouteMembership> Window(DateTimeOffset from,DateTimeOffset until)=>Observations.Where(o=>o.Started>=from&&o.Started<until).Select(o=>Memberships[o.Id]).ToArray();
}
