using System.Globalization;
using System.Net;

namespace TcpLatencyMonitor.Core;

public sealed record RouteTableNode(int Ttl,string? Address,string State,AnnotatedNode? Annotation,
    double? AverageRtt,int ReplyCount,int Attempts,bool MultipleAddresses,IReadOnlyList<HopProbe> Probes)
{
    public bool Target=>Annotation?.IsTarget==true;
    public bool NonPublic=>Address is not null&&NodeMetadataClient.LocalLabel(Address) is not null;
    public string Location=>Annotation?.Identity?.Location.Label??"—";
    public string Network=>State=="no-reply"?"未回应":State=="missing"?"无记录":State=="error"&&Address is null?"探测错误":Annotation?.Name??"线路未识别";
}
public sealed record RouteTableRow(IReadOnlyList<RouteTableNode> Nodes)
{
    public int First=>Nodes[0].Ttl;
    public int Last=>Nodes[^1].Ttl;
    public bool Merged=>Nodes.Count>1;
    public bool Target=>Nodes.Any(n=>n.Target);
    public string Hops=>First==Last?First.ToString(CultureInfo.InvariantCulture):$"{First}–{Last}";
    public string[] Addresses=>Nodes.Where(n=>n.Address is not null).Select(n=>n.Address!).Distinct().ToArray();
    public string Rtt
    {
        get
        {
            var values=Nodes.Where(n=>n.AverageRtt.HasValue).Select(n=>n.AverageRtt!.Value).ToArray();
            if(values.Length==0)return "—";
            string low=FormatRtt(values.Min()),high=FormatRtt(values.Max());
            return (low==high?low:low+"～"+high)+(values.Length<Nodes.Count?" *":"");
        }
    }
    public static string FormatRtt(double? value)=>value is not double v||!double.IsFinite(v)||v<0?"—":v<1?"<1":v.ToString("0.#",CultureInfo.InvariantCulture);
}
public sealed record RouteTableModel(IReadOnlyList<RouteTableRow> Rows,int HiddenNonPublic,int HiddenNoReply,int MergedNodes);
public static class RouteTable
{
    public static RouteTableModel Build(RouteRun route,RouteAnnotation annotation,bool showAll=false)
    {
        if(route.Id!=annotation.RouteId)throw new ArgumentException("Route and annotation do not match");
        var entries=new List<RouteTableNode>();
        var groups=route.Probes.GroupBy(p=>p.Ttl).ToDictionary(g=>g.Key,g=>g.ToArray());
        int last=groups.Count==0?0:groups.Keys.Max();
        for(int ttl=1;ttl<=Math.Min(last,64);ttl++)
        {
            if(!groups.TryGetValue(ttl,out var probes))
            {entries.Add(new(ttl,null,"missing",null,null,0,0,false,Array.Empty<HopProbe>()));continue;}
            var addresses=probes.Where(p=>p.Address is not null&&IPAddress.TryParse(p.Address,out _)).Select(p=>CidrBlock.Normalize(p.Address!)).Distinct().Order(StringComparer.Ordinal).ToArray();
            if(addresses.Length==0)
            {
                entries.Add(new(ttl,null,probes.All(p=>p.Status==11010)?"no-reply":"error",null,null,0,probes.Length,false,probes));continue;
            }
            foreach(var ip in addresses)
            {
                var own=probes.Where(p=>p.Address is not null&&IPAddress.TryParse(p.Address,out _)&&CidrBlock.Normalize(p.Address)==ip).ToArray();
                bool target=ip==CidrBlock.Normalize(route.Address)&&own.Any(p=>p.Status==0);
                var node=annotation.Nodes.FirstOrDefault(n=>n.Ttl==ttl&&CidrBlock.Normalize(n.Address)==ip)
                    ??NodeClassifier.Classify(ttl,ip,target,null,annotation.Time);
                node=node with{IsTarget=target};
                var valid=own.Where(p=>p.Status is 0 or 11013&&p.RttMs is double r&&double.IsFinite(r)&&r>=0).Select(p=>p.RttMs!.Value).ToArray();
                entries.Add(new(ttl,ip,own.All(p=>p.Status is 0 or 11013)?"reply":"error",node,
                    valid.Length==0?null:valid.Average(),own.Count(p=>p.Status is 0 or 11013),probes.Length,addresses.Length>1,own));
            }
        }
        int hiddenLocal=0,hiddenNoReply=0;var rows=new List<RouteTableRow>();
        foreach(var entry in entries)
        {
            if(!showAll&&entry.NonPublic&&!entry.Target){hiddenLocal++;continue;}
            if(!showAll&&entry.State=="no-reply"){hiddenNoReply++;continue;}
            if(rows.Count>0&&CanMerge(rows[^1].Nodes[^1],entry))
                rows[^1]=new(rows[^1].Nodes.Append(entry).ToArray());
            else rows.Add(new(new[]{entry}));
        }
        return new(rows,hiddenLocal,hiddenNoReply,rows.Sum(r=>r.Nodes.Count-1));
    }
    private static bool CanMerge(RouteTableNode a,RouteTableNode b)
    {
        if(a.Ttl+1!=b.Ttl||a.Target||b.Target||a.MultipleAddresses||b.MultipleAddresses||a.State!="reply"||b.State!="reply"||
            a.Probes.Any(p=>p.IsSupplemental)||b.Probes.Any(p=>p.IsSupplemental))return false;
        var x=a.Annotation?.Identity;var y=b.Annotation?.Identity;
        return x is {Local:false,Conflict:false,NetworkKey:not null,Location.Level:"city"}&&
            y is {Local:false,Conflict:false,NetworkKey:not null,Location.Level:"city"}&&
            !x.Location.Conflict&&!y.Location.Conflict&&x.Location.State==y.Location.State&&
            x.Location.Key.Split('|').Take(2).All(s=>s.Length>0)&&y.Location.Key.Split('|').Take(2).All(s=>s.Length>0)&&
            x.NetworkKey==y.NetworkKey&&x.Location.Key==y.Location.Key&&x.Location.Stale==y.Location.Stale&&
            x.Kind==y.Kind&&a.Annotation!.Name==b.Annotation!.Name;
    }
}
