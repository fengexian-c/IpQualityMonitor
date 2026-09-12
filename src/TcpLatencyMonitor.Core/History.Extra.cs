using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed record MonitorEvent(string Id,string TargetKey,DateTimeOffset Time,string Kind,string Detail,string? RouteId=null,string? PreviousRouteId=null);
[JsonSerializable(typeof(RouteRun))]
[JsonSerializable(typeof(List<RouteRun>))]
[JsonSerializable(typeof(List<MonitorEvent>))]
[JsonSerializable(typeof(Target))]
[JsonSerializable(typeof(NodeMetadata))]
[JsonSerializable(typeof(MonitorEvent))]
[JsonSerializable(typeof(RouteAnnotation))]
[JsonSerializable(typeof(NodeCalibration))]
[JsonSerializable(typeof(NextTraceImport))]
internal partial class DataJson : JsonSerializerContext { }

public sealed partial class History
{
    private static void Upgrade(SqliteConnection db)
    {
        using var check=db.CreateCommand();check.CommandText="PRAGMA user_version";long version=(long)check.ExecuteScalar()!;
        if(version>5)throw new InvalidOperationException("此数据库由更新版本创建，请使用对应版本打开。");
        check.CommandText="SELECT count(*) FROM sqlite_master WHERE name='sample'";
        if(version==0&&(long)check.ExecuteScalar()!>0)
        {
            string backup=db.DataSource+".before-v2.bak";
            if(!File.Exists(backup)){using var copy=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup}.ToString());copy.Open();db.BackupDatabase(copy);}
        }
    }
    private static void InitializeExtra(SqliteConnection db)
    {
        using var versionCommand=db.CreateCommand();versionCommand.CommandText="PRAGMA user_version";
        if((long)versionCommand.ExecuteScalar()!>=2)return;
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            ALTER TABLE sample ADD COLUMN context TEXT NOT NULL DEFAULT '';
            ALTER TABLE sample ADD COLUMN native_status INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE minute_summary(target TEXT NOT NULL,bucket_ms INTEGER NOT NULL,attempts INTEGER NOT NULL,successes INTEGER NOT NULL,errors INTEGER NOT NULL,total REAL,low REAL,high REAL,PRIMARY KEY(target,bucket_ms));
            INSERT INTO minute_summary SELECT target,(time_ms/60000)*60000,COUNT(CASE WHEN status<>4 THEN 1 END),COUNT(CASE WHEN status=0 THEN 1 END),COUNT(CASE WHEN status=4 THEN 1 END),SUM(CASE WHEN status=0 THEN latency END),MIN(CASE WHEN status=0 THEN latency END),MAX(CASE WHEN status=0 THEN latency END) FROM sample GROUP BY target,time_ms/60000;
            CREATE TRIGGER sample_rollup AFTER INSERT ON sample BEGIN
              INSERT INTO minute_summary VALUES(NEW.target,(NEW.time_ms/60000)*60000,NEW.status<>4,NEW.status=0,NEW.status=4,CASE WHEN NEW.status=0 THEN NEW.latency END,CASE WHEN NEW.status=0 THEN NEW.latency END,CASE WHEN NEW.status=0 THEN NEW.latency END)
              ON CONFLICT(target,bucket_ms) DO UPDATE SET attempts=attempts+excluded.attempts,successes=successes+excluded.successes,errors=errors+excluded.errors,
              total=COALESCE(total,0)+COALESCE(excluded.total,0),low=CASE WHEN low IS NULL THEN excluded.low WHEN excluded.low IS NULL THEN low ELSE MIN(low,excluded.low) END,
              high=CASE WHEN high IS NULL THEN excluded.high WHEN excluded.high IS NULL THEN high ELSE MAX(high,excluded.high) END;
            END;
            CREATE TABLE route_run(id TEXT PRIMARY KEY,target TEXT NOT NULL,time_ms INTEGER NOT NULL,json TEXT NOT NULL);
            CREATE INDEX ix_route_target_time ON route_run(target,time_ms);
            CREATE TABLE monitor_event(id TEXT PRIMARY KEY,target TEXT NOT NULL,time_ms INTEGER NOT NULL,kind TEXT NOT NULL,detail TEXT NOT NULL,route_id TEXT,previous_route_id TEXT);
            CREATE INDEX ix_event_target_time ON monitor_event(target,time_ms);
            PRAGMA user_version=2;
            """;cmd.ExecuteNonQuery();tx.Commit();
    }
    public void SaveRoute(RouteRun route)
    {
        Write(db=>
        {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO route_run VALUES($id,$t,$time,$json)";
        cmd.Parameters.AddWithValue("$id",route.Id);cmd.Parameters.AddWithValue("$t",route.TargetKey);cmd.Parameters.AddWithValue("$time",route.Started.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(route,DataJson.Default.RouteRun));cmd.ExecuteNonQuery();
        RememberRouteNodes(db,route);
            });
    }
    public List<RouteRun> LoadRoutes(Target target,int limit=10000)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM route_run WHERE target=$t ORDER BY time_ms DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$t",target.Key);cmd.Parameters.AddWithValue("$limit",limit);using var reader=cmd.ExecuteReader();var result=new List<RouteRun>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize(reader.GetString(0),DataJson.Default.RouteRun)!);return result;
    }
    public RouteRun? LoadRoute(Target target,string id)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM route_run WHERE target=$t AND id=$id";
        cmd.Parameters.AddWithValue("$t",target.Key);cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize(json,DataJson.Default.RouteRun):null;
    }
    public void AddEvent(MonitorEvent item)
    {
        Write(db=>
        {
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO monitor_event VALUES($id,$t,$time,$kind,$detail,$route,$previous)";
        cmd.Parameters.AddWithValue("$id",item.Id);cmd.Parameters.AddWithValue("$t",item.TargetKey);cmd.Parameters.AddWithValue("$time",item.Time.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$kind",item.Kind);cmd.Parameters.AddWithValue("$detail",item.Detail);
        cmd.Parameters.AddWithValue("$route",(object?)item.RouteId??DBNull.Value);cmd.Parameters.AddWithValue("$previous",(object?)item.PreviousRouteId??DBNull.Value);cmd.ExecuteNonQuery();
            });
    }
    public List<MonitorEvent> LoadEvents(Target target,int limit=5000)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,time_ms,kind,detail,route_id,previous_route_id FROM monitor_event WHERE target=$t ORDER BY time_ms DESC,rowid DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$t",target.Key);cmd.Parameters.AddWithValue("$limit",limit);using var reader=cmd.ExecuteReader();var result=new List<MonitorEvent>();
        while(reader.Read())result.Add(new(reader.GetString(0),target.Key,DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5)));return result;
    }
    public long ExportBundle(Target target,string path,DateTimeOffset now,Target? routeTarget=null)
    {
        // All exports are local. No dependency on an online reporting service.
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
        var temporary=path+".csv.tmp";
        try
        {
            long count=Export(target,temporary,now);zip.CreateEntryFromFile(temporary,"samples.csv");
            void Text(string name,string value){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(value);}
            Text("target.json",JsonSerializer.Serialize(target,DataJson.Default.Target));
            routeTarget??=target;
            Text("route-profile.json",JsonSerializer.Serialize(routeTarget,DataJson.Default.Target));
            // Stream all retained records; UI paging limits must not truncate diagnostic exports.
            using var db=Open();
            var nodes=new HashSet<string>(StringComparer.Ordinal);
            using(var stream=zip.CreateEntry("routes.json").Open())
            using(var writer=new Utf8JsonWriter(stream))
            using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="SELECT json FROM route_run WHERE target=$t AND time_ms >= $from AND time_ms <= $until ORDER BY time_ms";
                cmd.Parameters.AddWithValue("$t",routeTarget.Key);cmd.Parameters.AddWithValue("$from",now.AddDays(-31).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$until",now.ToUnixTimeMilliseconds());
                using var reader=cmd.ExecuteReader();writer.WriteStartArray();
                while(reader.Read())
                {
                    var json=reader.GetString(0);writer.WriteRawValue(json);
                    var run=JsonSerializer.Deserialize(json,DataJson.Default.RouteRun)!;
                    foreach(var hop in run.Probes)if(hop.Address is not null)nodes.Add(hop.Address);
                    writer.Flush();
                }
                writer.WriteEndArray();
            }
            using(var stream=zip.CreateEntry("events.json").Open())
            using(var writer=new Utf8JsonWriter(stream))
            using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="SELECT id,target,time_ms,kind,detail,route_id,previous_route_id FROM monitor_event WHERE target IN ($t,$r) AND time_ms >= $from AND time_ms <= $until ORDER BY time_ms,rowid";
                cmd.Parameters.AddWithValue("$t",target.Key);cmd.Parameters.AddWithValue("$r",routeTarget.Key);cmd.Parameters.AddWithValue("$from",now.AddDays(-31).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$until",now.ToUnixTimeMilliseconds());
                using var reader=cmd.ExecuteReader();writer.WriteStartArray();
                while(reader.Read())
                {
                    var item=new MonitorEvent(reader.GetString(0),reader.GetString(1),DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6));
                    JsonSerializer.Serialize(writer,item,DataJson.Default.MonitorEvent);writer.Flush();
                }
                writer.WriteEndArray();
            }
            using(var stream=zip.CreateEntry("node-metadata.json").Open())
            using(var writer=new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();foreach(var ip in nodes)
                    if(LoadNodeMetadata(ip) is NodeMetadata data){JsonSerializer.Serialize(writer,data,DataJson.Default.NodeMetadata);writer.Flush();}
                writer.WriteEndArray();
            }
            using(var stream=zip.CreateEntry("route-annotations.json").Open())
            using(var writer=new Utf8JsonWriter(stream))
            using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="SELECT a.json FROM route_annotation a JOIN route_run r ON r.id=a.route_id WHERE r.target=$t AND r.time_ms >= $from AND r.time_ms <= $until ORDER BY a.time_ms,a.rowid";
                cmd.Parameters.AddWithValue("$t",routeTarget.Key);cmd.Parameters.AddWithValue("$from",now.AddDays(-31).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$until",now.ToUnixTimeMilliseconds());
                using var reader=cmd.ExecuteReader();writer.WriteStartArray();while(reader.Read()){writer.WriteRawValue(reader.GetString(0));writer.Flush();}writer.WriteEndArray();
            }
            Text("说明.txt",$"导出时间：{now:O}\n目标：{target.Label}\n采样：{count} 条；保留期 31 天。\n旧版 timeout=0 表示历史参数未知。ICMP 数据区 32 字节；路由使用 ICMP，探测参数在各快照内。\n路由为本地到目标的观测路径；未知跳、负载均衡、VPN/TUN 可能影响结果。\n异常与路由同时变化不证明因果关系。采样比例不等于在线时长。导出中各文件在生成时读取，持续采样下末尾时间可能略有差异。\n");
            Text("节点注释说明.txt","node-metadata.json 包含相关节点当前缓存；route-annotations.json 包含每份路由已保存的注释及后补修订，保存规则版本、来源与查询时间，旧解释不会因缓存刷新被覆盖。查看 Origin 区分采集时注释与后补；查询时间不等于路由发生时间。空数组表示尚无保存结果。导出本身不联网。地区为数据库估算，名称是节点归属，不认证 GIA/GT，也不代表 BGP AS_PATH 或真实反向路径。\n");
            return count;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
