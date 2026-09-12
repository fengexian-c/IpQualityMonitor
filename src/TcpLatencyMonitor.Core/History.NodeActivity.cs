using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed partial class History
{
    // Additive schema: the previous app can still read the same database.
    private static void InitializeNodeActivity(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();
        cmd.CommandText="CREATE TABLE IF NOT EXISTS node_route_seen(address TEXT PRIMARY KEY,last_seen_ms INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS ix_node_route_seen ON node_route_seen(last_seen_ms); SELECT value FROM metadata_state WHERE name='node_route_seen_initialized'";
        if(cmd.ExecuteScalar() is long)return;
        var routes=new List<RouteRun>();
        cmd.CommandText="SELECT json FROM route_run WHERE time_ms >= $since";cmd.Parameters.AddWithValue("$since",DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
        using(var reader=cmd.ExecuteReader())while(reader.Read())routes.Add(JsonSerializer.Deserialize(reader.GetString(0),DataJson.Default.RouteRun)!);
        using var tx=db.BeginTransaction();
        foreach(var route in routes)RememberRouteNodes(db,route);
        cmd.Transaction=tx;cmd.CommandText="INSERT INTO metadata_state VALUES('node_route_seen_initialized',1)";cmd.Parameters.Clear();cmd.ExecuteNonQuery();tx.Commit();
    }
    private static void RememberRouteNodes(SqliteConnection db,RouteRun route)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO node_route_seen VALUES($ip,$seen) ON CONFLICT(address) DO UPDATE SET last_seen_ms=MAX(last_seen_ms,excluded.last_seen_ms)";
        var ip=cmd.Parameters.Add("$ip",SqliteType.Text);cmd.Parameters.AddWithValue("$seen",Math.Min(route.Finished.ToUnixTimeMilliseconds(),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        foreach(var address in route.Probes.Where(p=>p.Address is not null&&p.Status is 0 or 11013).Select(p=>p.Address!).Where(a=>NodeMetadataClient.LocalLabel(a) is null).Select(CidrBlock.Normalize).Distinct())
        {ip.Value=address;cmd.ExecuteNonQuery();}
    }
    public List<string> RecentRouteNodes(DateTimeOffset now,int limit=512)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT address FROM node_route_seen WHERE last_seen_ms >= $since AND last_seen_ms <= $now ORDER BY last_seen_ms DESC,address LIMIT $limit";
        cmd.Parameters.AddWithValue("$since",now.AddDays(-1).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,4096));
        using var reader=cmd.ExecuteReader();var ips=new List<string>();while(reader.Read())ips.Add(reader.GetString(0));return ips;
    }
    public List<string> DueRouteNodes(DateTimeOffset now,MetadataOptions options,int limit=128)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT n.address FROM node_route_seen n LEFT JOIN geo_observation g ON g.address=n.address AND g.provider=$provider
            WHERE n.last_seen_ms >= $since AND n.last_seen_ms <= $now
            AND (g.address IS NULL OR g.success_json IS NULL OR julianday(json_extract(g.success_json,'$.Expires')) <= julianday($nowText)
              OR julianday(json_extract(g.success_json,'$.Queried')) <= julianday($due))
            AND (g.address IS NULL OR NOT (json_extract(g.attempt_json,'$.Success')=0 AND julianday(json_extract(g.attempt_json,'$.Expires')) > julianday($nowText)))
            ORDER BY COALESCE(json_extract(g.success_json,'$.Queried'),''),n.last_seen_ms DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$provider",options.Primary);cmd.Parameters.AddWithValue("$since",now.AddDays(-1).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$nowText",now.ToString("O"));cmd.Parameters.AddWithValue("$due",now.AddHours(-options.RefreshHours).ToString("O"));cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,1024));
        using var reader=cmd.ExecuteReader();var result=new List<string>();while(reader.Read())result.Add(reader.GetString(0));return result;
    }
    public long? MetadataValue(string key)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM metadata_state WHERE name=$key";cmd.Parameters.AddWithValue("$key",key);
        return cmd.ExecuteScalar() is long value?value:null;
    }
    public void SetMetadataValue(string key,long value)=>Write(db=>
    {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO metadata_state VALUES($key,$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$key",key);cmd.Parameters.AddWithValue("$value",value);cmd.ExecuteNonQuery();
    });
}
