using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;
public sealed partial class History
{
    private readonly object _annotationSync=new();
    private static void InitializeAnnotations(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";
        if((long)cmd.ExecuteScalar()!>=4)return;
        using var tx=db.BeginTransaction();cmd.Transaction=tx;
        cmd.CommandText="CREATE TABLE route_annotation(id TEXT PRIMARY KEY,route_id TEXT NOT NULL,time_ms INTEGER NOT NULL,json TEXT NOT NULL); CREATE INDEX ix_annotation_route ON route_annotation(route_id,time_ms); PRAGMA user_version=4";cmd.ExecuteNonQuery();tx.Commit();
    }
    public RouteAnnotation SaveRouteAnnotation(RouteAnnotation annotation)
    {
        RouteAnnotation saved=annotation;
        Write(db=>
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT json FROM route_annotation WHERE route_id=$route ORDER BY time_ms DESC,rowid DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$route",annotation.RouteId);
            if(annotation.EvidenceKey.Length>0&&cmd.ExecuteScalar() is string json)
            {
                var previous=JsonSerializer.Deserialize(json,DataJson.Default.RouteAnnotation)!;
                if(previous.EvidenceKey==annotation.EvidenceKey){saved=previous;return;}
            }
            cmd.CommandText="INSERT INTO route_annotation SELECT $id,$route,$time,$json WHERE EXISTS(SELECT 1 FROM route_run WHERE id=$route)";
            cmd.Parameters.AddWithValue("$id",annotation.Id);cmd.Parameters.AddWithValue("$time",annotation.Time.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(annotation,DataJson.Default.RouteAnnotation));cmd.ExecuteNonQuery();
        });
        return saved;
    }
    /// <summary>Explicitly offline. Old evidence is frozen; a current cache is used only if no annotation ever existed.</summary>
    public RouteAnnotation CurrentRouteAnnotation(RouteRun route,DateTimeOffset now)
    {
        lock(_annotationSync)
        {
            var old=LoadRouteAnnotation(route.Id);
            if(old is not null&&old.RuleVersion==BackboneCatalog.Version&&old.InterpretationVersion==RouteClassifier.InterpretationVersion)return old;
            var metadata=new Dictionary<string,NodeMetadata>();
            if(old is not null)
            {
                foreach(var node in old.Nodes)if(node.Metadata is not null)metadata[node.Address]=node.Metadata;
            }
            else
            {
                foreach(var ip in route.Probes.Where(p=>p.Address is not null).Select(p=>CidrBlock.Normalize(p.Address!)).Distinct())
                    if(LoadNodeMetadata(ip) is NodeMetadata data)metadata[ip]=data;
            }
            return SaveRouteAnnotation(RouteClassifier.Classify(route,metadata,now,old is null?
                "当前缓存解释 · 无当时注释快照":"按当前规则重新解释 · 使用已保存证据"));
        }
    }
    public RouteAnnotation? LoadRouteAnnotation(string id,bool original=false)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT json FROM route_annotation WHERE route_id=$id ORDER BY time_ms "+(original?"ASC,rowid ASC":"DESC,rowid DESC")+" LIMIT 1";
        cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize(json,DataJson.Default.RouteAnnotation):null;
    }
}
