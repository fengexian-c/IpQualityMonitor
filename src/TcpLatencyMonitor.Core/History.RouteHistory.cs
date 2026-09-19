using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed partial class History
{
    public string RouteHistoryStamp(Target target)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT COUNT(*) || ':' || COALESCE(MAX(time_ms),0) || ':' || COALESCE(MIN(time_ms),0) || ':' || COALESCE(MAX(rowid),0) || ':' || (SELECT COUNT(*) || ':' || COALESCE(MAX(rowid),0) || ':' || COALESCE(MAX(time_ms),0) FROM monitor_event WHERE target=$t AND kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged')) FROM route_run WHERE target=$t";
        cmd.Parameters.AddWithValue("$t",target.Key);return (string)cmd.ExecuteScalar()!;
    }
    // Read the retained source universe, not a UI time filter. Parse and release one JSON
    // snapshot at a time; only compact observations survive the reader.
    public RouteHistoryIndex LoadRouteHistory(Target target,CancellationToken token=default)
    {
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT json FROM route_run WHERE target=$t ORDER BY time_ms,id";cmd.Parameters.AddWithValue("$t",target.Key);
        using var reader=cmd.ExecuteReader();var observations=new List<RouteObservation>();
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            observations.Add(RouteObservation.From(JsonSerializer.Deserialize(reader.GetString(0),DataJson.Default.RouteRun)!));
        }
        reader.Close();cmd.CommandText="SELECT time_ms FROM monitor_event WHERE target=$t AND kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged') ORDER BY time_ms";
        var boundaries=new List<DateTimeOffset>();using(var events=cmd.ExecuteReader())while(events.Read()){token.ThrowIfCancellationRequested();boundaries.Add(DateTimeOffset.FromUnixTimeMilliseconds(events.GetInt64(0)));}
        return RouteHistoryIndex.Build(observations,token,boundaries);
    }
    public Dictionary<string,NodeMetadata> LoadRouteHistoryMetadata(IEnumerable<string> addresses,CancellationToken token=default)
    {
        var result=new Dictionary<string,NodeMetadata>();using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT json FROM node_metadata WHERE address=$ip";var parameter=cmd.Parameters.Add("$ip",Microsoft.Data.Sqlite.SqliteType.Text);
        foreach(var address in addresses.Distinct())
        {
            token.ThrowIfCancellationRequested();parameter.Value=address;
            if(cmd.ExecuteScalar() is string json)result[address]=JsonSerializer.Deserialize(json,DataJson.Default.NodeMetadata)!;
        }
        return result;
    }
}
