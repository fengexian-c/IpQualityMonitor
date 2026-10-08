using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed partial class History
{
    /// <summary>
    /// Read-only startup/maintenance recovery within a bounded recent window. Only
    /// the newest limit raw rows for configured targets are examined (512 by default),
    /// so a large old database cannot turn startup into an unbounded historical scan.
    /// A caller can rotate the returned candidates rather than repeatedly choosing
    /// only the first incomplete route. Deleted/expired raw routes are never returned.
    /// </summary>
    public IReadOnlyList<RouteRun> LoadRoutesNeedingAnnotation(DateTimeOffset now,IReadOnlyCollection<string> targetKeys,
        bool includeIncomplete,int limit=512,TimeSpan? lookback=null,int refreshHours=24,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(targetKeys);
        if(refreshHours is <1 or >168)throw new ArgumentOutOfRangeException(nameof(refreshHours));
        if(limit is <1 or >2048)throw new ArgumentOutOfRangeException(nameof(limit));
        TimeSpan window=lookback??TimeSpan.FromDays(1);
        if(window<=TimeSpan.Zero||window>TimeSpan.FromDays(31))throw new ArgumentOutOfRangeException(nameof(lookback));
        string[] targets=targetKeys.Where(key=>!string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal).ToArray();
        if(targets.Length==0)return [];
        if(targets.Length>1024)throw new ArgumentOutOfRangeException(nameof(targetKeys));
        token.ThrowIfCancellationRequested();
        using var db=Open();using var cmd=db.CreateCommand();
        string parameters=string.Join(',',targets.Select((_,i)=>"$target"+i));
        // Materialize the small raw-route window before joining optional JSON. No
        // JSON SQL predicates can fail on a malformed legacy annotation document.
        cmd.CommandText=$"""
            WITH recent AS MATERIALIZED (
                SELECT id,target,json,time_ms,rowid AS ordinal FROM route_run
                WHERE time_ms >= $from AND time_ms <= $until AND target IN ({parameters})
                ORDER BY time_ms DESC,rowid DESC LIMIT $limit)
            SELECT r.id,r.target,r.json,a.json FROM recent r
            LEFT JOIN route_annotation a ON a.rowid=(
                SELECT rowid FROM route_annotation WHERE route_id=r.id ORDER BY time_ms DESC,rowid DESC LIMIT 1)
            ORDER BY r.time_ms DESC,r.ordinal DESC
            """;
        cmd.Parameters.AddWithValue("$from",(now-window).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$until",now.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$limit",limit);
        for(int i=0;i<targets.Length;i++)cmd.Parameters.AddWithValue("$target"+i,targets[i]);
        var result=new List<RouteRun>();using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var annotation=reader.IsDBNull(3)?null:ReadAnnotation(reader.GetString(3));
            if(annotation is not null&&annotation.RouteId!=reader.GetString(0))annotation=null;
            if(annotation is not null&&(!includeIncomplete||!AnnotationIncomplete(annotation,now,refreshHours)))continue;
            RouteRun? route;
            try{route=JsonSerializer.Deserialize(reader.GetString(2),DataJson.Default.RouteRun);}
            catch(JsonException){continue;}
            // Corrupt raw evidence is not repaired by a derived recovery operation.
            if(route is null||route.Id!=reader.GetString(0)||route.TargetKey!=reader.GetString(1)||route.Probes is null)continue;
            result.Add(route);
        }
        return result;
    }
    private static bool AnnotationIncomplete(RouteAnnotation annotation,DateTimeOffset now,int refreshHours)=>
        annotation.RuleVersion!=BackboneCatalog.Version||annotation.InterpretationVersion!=RouteClassifier.InterpretationVersion||
        string.IsNullOrEmpty(annotation.EvidenceKey)||annotation.PendingQueries>0||annotation.MissingMetadata>0||annotation.MissingAsn>0||
        annotation.LocatedNodes<annotation.PublicNodes||annotation.Nodes.Any(node=>NodeMetadataClient.LocalLabel(node.Address) is null&&
            (node.Metadata is not {Success:true} data||data.Expires<=now||data.Queried<=now.AddHours(-refreshHours)));
}
