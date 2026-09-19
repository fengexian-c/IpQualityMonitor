using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed record RouteSourceEntry(string Id,string Stamp,long SeenMs);
public sealed record RouteSourceDelta(string Version,IReadOnlyList<RouteSourceEntry> Entries,IReadOnlyList<RouteObservation> Changed,IReadOnlyList<DateTimeOffset> Boundaries);

public sealed partial class History
{
    // Tiny source revision side tables; raw route JSON and monitor events are never
    // rewritten. Random mutation stamps also detect restoring an older source DB.
    // Installation runs on the analysis worker, not in History.WriteLoop.
    internal void InitializeRouteSource()
    {
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS route_analysis_source(kind TEXT NOT NULL,id TEXT NOT NULL,target TEXT NOT NULL,stamp TEXT NOT NULL,seen_ms INTEGER NOT NULL,PRIMARY KEY(kind,id));
            CREATE INDEX IF NOT EXISTS ix_route_analysis_source_target ON route_analysis_source(target,kind);
            INSERT OR IGNORE INTO route_analysis_source SELECT 'route',id,target,hex(randomblob(16)),0 FROM route_run;
            INSERT OR IGNORE INTO route_analysis_source SELECT 'boundary',id,target,hex(randomblob(16)),0 FROM monitor_event WHERE kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged');
            CREATE TRIGGER IF NOT EXISTS route_analysis_insert AFTER INSERT ON route_run BEGIN
              INSERT OR REPLACE INTO route_analysis_source VALUES('route',NEW.id,NEW.target,hex(randomblob(16)),CAST(unixepoch('subsec')*1000 AS INTEGER)); END;
            CREATE TRIGGER IF NOT EXISTS route_analysis_update AFTER UPDATE ON route_run BEGIN
              DELETE FROM route_analysis_source WHERE kind='route' AND id=OLD.id;
              INSERT OR REPLACE INTO route_analysis_source VALUES('route',NEW.id,NEW.target,hex(randomblob(16)),CAST(unixepoch('subsec')*1000 AS INTEGER)); END;
            CREATE TRIGGER IF NOT EXISTS route_analysis_delete AFTER DELETE ON route_run BEGIN DELETE FROM route_analysis_source WHERE kind='route' AND id=OLD.id; END;
            CREATE TRIGGER IF NOT EXISTS route_analysis_event_insert AFTER INSERT ON monitor_event WHEN NEW.kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged') BEGIN
              INSERT OR REPLACE INTO route_analysis_source VALUES('boundary',NEW.id,NEW.target,hex(randomblob(16)),CAST(unixepoch('subsec')*1000 AS INTEGER)); END;
            CREATE TRIGGER IF NOT EXISTS route_analysis_event_update AFTER UPDATE ON monitor_event BEGIN
              DELETE FROM route_analysis_source WHERE kind='boundary' AND id=OLD.id;
              INSERT OR REPLACE INTO route_analysis_source SELECT 'boundary',NEW.id,NEW.target,hex(randomblob(16)),CAST(unixepoch('subsec')*1000 AS INTEGER) WHERE NEW.kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged'); END;
            CREATE TRIGGER IF NOT EXISTS route_analysis_event_delete AFTER DELETE ON monitor_event BEGIN DELETE FROM route_analysis_source WHERE kind='boundary' AND id=OLD.id; END;
            DELETE FROM route_analysis_source WHERE kind='route' AND NOT EXISTS(SELECT 1 FROM route_run r WHERE r.id=route_analysis_source.id);
            DELETE FROM route_analysis_source WHERE kind='boundary' AND NOT EXISTS(SELECT 1 FROM monitor_event e WHERE e.id=route_analysis_source.id AND e.kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged'));
            """;
        cmd.ExecuteNonQuery();tx.Commit();
    }
    internal string[] RouteSourceTargets()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT DISTINCT target FROM route_analysis_source ORDER BY target";
        using var reader=cmd.ExecuteReader();var targets=new List<string>();while(reader.Read())targets.Add(reader.GetString(0));return targets.ToArray();
    }
    internal string RouteSourceVersion(string target,CancellationToken token)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT kind,id,stamp FROM route_analysis_source WHERE target=$t ORDER BY kind,id";cmd.Parameters.AddWithValue("$t",target);
        var version=new System.Text.StringBuilder(RouteHistoryIndex.AlgorithmVersion);using var reader=cmd.ExecuteReader();
        while(reader.Read()){token.ThrowIfCancellationRequested();version.Append('|').Append(reader.GetString(0)).Append(':').Append(reader.GetString(1)).Append(':').Append(reader.GetString(2));}
        return RouteHistoryIndex.Hash(version.ToString());
    }
    internal RouteSourceDelta ReadRouteSource(string target,IReadOnlyDictionary<string,string> known,CancellationToken token)
    {
        using var db=Open();using var tx=db.BeginTransaction(deferred:true);using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT kind,id,stamp,seen_ms FROM route_analysis_source WHERE target=$t ORDER BY kind,id";cmd.Parameters.AddWithValue("$t",target);
        var entries=new List<RouteSourceEntry>();var version=new System.Text.StringBuilder(RouteHistoryIndex.AlgorithmVersion);
        using(var reader=cmd.ExecuteReader())while(reader.Read())
        {
            token.ThrowIfCancellationRequested();string kind=reader.GetString(0),id=reader.GetString(1),stamp=reader.GetString(2);
            version.Append('|').Append(kind).Append(':').Append(id).Append(':').Append(stamp);
            if(kind=="route")entries.Add(new(id,stamp,reader.GetInt64(3)));
        }
        cmd.CommandText="SELECT time_ms FROM monitor_event WHERE target=$t AND kind IN ('Started','Stopped','Suspend','Resume','NetworkChanged') ORDER BY time_ms";
        var boundaries=new List<DateTimeOffset>();using(var reader=cmd.ExecuteReader())while(reader.Read()){token.ThrowIfCancellationRequested();boundaries.Add(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)));}
        cmd.CommandText="SELECT json FROM route_run WHERE target=$t AND id=$id";var parameter=cmd.Parameters.Add("$id",SqliteType.Text);
        var changed=new List<RouteObservation>();
        foreach(var entry in entries)
        {
            token.ThrowIfCancellationRequested();if(known.TryGetValue(entry.Id,out string? old)&&old==entry.Stamp)continue;
            parameter.Value=entry.Id;
            if(cmd.ExecuteScalar() is string json)changed.Add(RouteObservation.From(JsonSerializer.Deserialize(json,DataJson.Default.RouteRun)!));
        }
        return new(RouteHistoryIndex.Hash(version.ToString()),entries,changed,boundaries);
    }
}
