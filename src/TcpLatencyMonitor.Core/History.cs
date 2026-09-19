using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TcpLatencyMonitor.Core;

public sealed record Summary(long Attempts, long Successes, long LocalErrors, double? Average, double? Minimum, double? Maximum)
{
    public double? Availability => Attempts == 0 ? null : Successes * 100d / Attempts;
}
public sealed record Bucket(DateTimeOffset Start, long Attempts, long Successes, long LocalErrors, double? Average, double? Minimum=null,double? Maximum=null)
{
    public double? Availability => Attempts == 0 ? null : Successes * 100d / Attempts;
}
public sealed record Dashboard(Summary Minute, Summary FiveMinutes, Summary Hour, Summary Day, Summary Week, Summary Month,
    double? P95Hour, IReadOnlyList<Bucket> Minutes, IReadOnlyList<Bucket> Hours, IReadOnlyList<Sample> Recent)
{public double? P50Hour {get;init;}}

public sealed partial class History
{
    private readonly string _connectionString;
    public History(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return connection;
    }
    public void Initialize()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        Upgrade(db);
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sample (
              id INTEGER PRIMARY KEY, target TEXT NOT NULL, time_ms INTEGER NOT NULL,
              status INTEGER NOT NULL, latency REAL, elapsed REAL NOT NULL, detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_sample_target_time ON sample(target,time_ms);
            CREATE INDEX IF NOT EXISTS ix_sample_time ON sample(time_ms);
            """;
        cmd.ExecuteNonQuery();
        InitializeExtra(db);
        InitializeMetadata(db);
        InitializeAnnotations(db);
        InitializeGeoEvidence(db);
        InitializeNodeActivity(db);
        cmd.CommandText="CREATE INDEX IF NOT EXISTS ix_minute_time ON minute_summary(bucket_ms); CREATE INDEX IF NOT EXISTS ix_route_time ON route_run(time_ms); CREATE INDEX IF NOT EXISTS ix_event_time ON monitor_event(time_ms); CREATE INDEX IF NOT EXISTS ix_metadata_expiry ON node_metadata(expires_ms)";
        cmd.ExecuteNonQuery();
        PruneStep(DateTimeOffset.UtcNow);
    }
    public void Add(Target target, Sample sample)
    {
        Write(db=>
        {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sample(target,time_ms,status,latency,elapsed,detail,context,native_status) VALUES($t,$time,$s,$l,$e,$d,$c,$n)";
        cmd.Parameters.AddWithValue("$t", target.Key); cmd.Parameters.AddWithValue("$time", sample.Time.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$s", (int)sample.Status); cmd.Parameters.AddWithValue("$l", (object?)sample.LatencyMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", sample.ElapsedMs); cmd.Parameters.AddWithValue("$d", sample.Detail);
        cmd.Parameters.AddWithValue("$c",sample.Context);cmd.Parameters.AddWithValue("$n",sample.NativeStatus);
        cmd.ExecuteNonQuery();
            });
    }
    public void Prune(DateTimeOffset now)
    {
        while(PruneStep(now)) { }
    }
    public bool PruneStep(DateTimeOffset now)
    {
        bool more=false;
        foreach(var table in new[]{"sample","minute_summary","route_run","monitor_event","node_metadata","metadata_request","route_annotation","geo_observation","geo_request","node_route_seen"})
        {
            int deleted=0;
            Write(db=>
            {
                using var cmd=db.CreateCommand();
                var filter=table=="route_annotation"?"NOT EXISTS(SELECT 1 FROM route_run r WHERE r.id=route_annotation.route_id)":
                    table=="minute_summary"?"bucket_ms < $cutoff-$cutoff%60000":table=="node_route_seen"?"last_seen_ms < $cutoff":table is "node_metadata" or "geo_observation"?"expires_ms < $cutoff":"time_ms < $cutoff";
                cmd.CommandText=$"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {filter} LIMIT 2000)";
                cmd.Parameters.AddWithValue("$cutoff",now.AddDays(-31).ToUnixTimeMilliseconds());deleted=cmd.ExecuteNonQuery();
                if(deleted>0&&table is "route_run" or "monitor_event")RouteSourceChanged(null);
            });
            more|=deleted==2000;
        }
        return more;
    }
    private static SqliteCommand Range(SqliteConnection db, Target target, DateTimeOffset from, DateTimeOffset until, string sql)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$t", target.Key); cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$until", until.ToUnixTimeMilliseconds()); return cmd;
    }
    private const string Filter = " target=$t AND time_ms >= $from AND time_ms < $until ";
    private static Summary Summarize(SqliteConnection db, Target target, DateTimeOffset from, DateTimeOffset until)
    {
        using var cmd = Range(db, target, from, until, """
            SELECT COALESCE(SUM(attempts),0),COALESCE(SUM(successes),0),COALESCE(SUM(errors),0),
              SUM(total)/NULLIF(SUM(successes),0),MIN(low),MAX(high) FROM (
              SELECT attempts,successes,errors,total,low,high FROM minute_summary
                WHERE target=$t AND bucket_ms >= (($from+59999)/60000)*60000 AND bucket_ms < ($until/60000)*60000
              UNION ALL
              SELECT COUNT(CASE WHEN status<>4 THEN 1 END),COUNT(CASE WHEN status=0 THEN 1 END),COUNT(CASE WHEN status=4 THEN 1 END),
                SUM(CASE WHEN status=0 THEN latency END),MIN(CASE WHEN status=0 THEN latency END),MAX(CASE WHEN status=0 THEN latency END)
              FROM sample WHERE target=$t AND time_ms >= $from AND time_ms < $until
                AND (time_ms < (($from+59999)/60000)*60000 OR time_ms >= ($until/60000)*60000))
            """);
        using var reader = cmd.ExecuteReader(); reader.Read();
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), NullableDouble(reader, 3), NullableDouble(reader, 4), NullableDouble(reader, 5));
    }
    private static double? NullableDouble(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetDouble(index);
    private static IReadOnlyList<Bucket> Buckets(SqliteConnection db, Target target, DateTimeOffset now, int count, int seconds)
    {
        long span = seconds * 1000L;
        var endMs = (now.ToUnixTimeMilliseconds() / span + 1) * span;
        var startMs = endMs - count * span;
        var result = Enumerable.Range(0,count).Select(i => new Bucket(DateTimeOffset.FromUnixTimeMilliseconds(startMs+i*span),0,0,0,null)).ToArray();
        using var cmd = Range(db,target,DateTimeOffset.FromUnixTimeMilliseconds(startMs),now.AddMilliseconds(1),"""
            SELECT (bucket_ms-$from)/$span,SUM(attempts),SUM(successes),SUM(errors),SUM(total)/NULLIF(SUM(successes),0),MIN(low),MAX(high) FROM (
             SELECT bucket_ms,attempts,successes,errors,total,low,high FROM minute_summary WHERE target=$t AND bucket_ms >= $from AND bucket_ms < ($until/60000)*60000
             UNION ALL SELECT (time_ms/60000)*60000,COUNT(CASE WHEN status<>4 THEN 1 END),COUNT(CASE WHEN status=0 THEN 1 END),COUNT(CASE WHEN status=4 THEN 1 END),
             SUM(CASE WHEN status=0 THEN latency END),MIN(CASE WHEN status=0 THEN latency END),MAX(CASE WHEN status=0 THEN latency END)
             FROM sample WHERE target=$t AND time_ms >= ($until/60000)*60000 AND time_ms < $until GROUP BY time_ms/60000)
             GROUP BY (bucket_ms-$from)/$span
            """);
        cmd.Parameters.AddWithValue("$span", span);
        using var r = cmd.ExecuteReader();
        while(r.Read()) { int index = r.GetInt32(0); if(index>=0 && index<count) result[index] = result[index] with { Attempts=r.GetInt64(1),Successes=r.GetInt64(2),LocalErrors=r.GetInt64(3),Average=NullableDouble(r,4),Minimum=NullableDouble(r,5),Maximum=NullableDouble(r,6) }; }
        return result;
    }
    private static Sample ReadSample(SqliteDataReader r) => new(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), (ProbeStatus)r.GetInt32(1), NullableDouble(r,2), r.GetDouble(3),r.GetString(4)) {Context=r.FieldCount>5?r.GetString(5):"",NativeStatus=r.FieldCount>6?r.GetInt32(6):0};
    public Dashboard Load(Target target, DateTimeOffset now)
    {
        using var db = Open();
        // A read transaction gives all cards and charts a consistent SQLite snapshot.
        using var tx = db.BeginTransaction(deferred:true);
        var until = now.AddMilliseconds(1);
        Summary S(TimeSpan span) => Summarize(db,target,now-span,until);
        var recent = new List<Sample>();
        using(var cmd = Range(db,target,now.AddDays(-31),until,"SELECT time_ms,status,latency,elapsed,detail,context,native_status FROM sample WHERE "+Filter+" ORDER BY time_ms DESC,id DESC LIMIT 100"))
        using(var r=cmd.ExecuteReader()) while(r.Read()) recent.Add(ReadSample(r));
        var values = new List<double>();
        using(var cmd=Range(db,target,now.AddHours(-1),until,"SELECT latency FROM sample WHERE "+Filter+" AND status=0 ORDER BY latency"))
        using(var r=cmd.ExecuteReader()) while(r.Read()) values.Add(r.GetDouble(0));
        return new(S(TimeSpan.FromMinutes(1)),S(TimeSpan.FromMinutes(5)),S(TimeSpan.FromHours(1)),S(TimeSpan.FromDays(1)),S(TimeSpan.FromDays(7)),S(TimeSpan.FromDays(30)),
            Percentile95(values),Buckets(db,target,now,60,60),Buckets(db,target,now,24,3600),recent){P50Hour=values.Count==0?null:values[(int)Math.Ceiling(values.Count*.5)-1]};
    }
    public static double? Percentile95(IReadOnlyList<double> sorted) => sorted.Count==0 ? null : sorted[(int)Math.Ceiling(sorted.Count*.95)-1];
    public long Export(Target target, string path, DateTimeOffset now)
    {
        using var db = Open();
        using var cmd = Range(db,target,now.AddDays(-31),now.AddMilliseconds(1),"SELECT time_ms,status,latency,elapsed,detail,context,native_status FROM sample WHERE "+Filter+" ORDER BY time_ms,id");
        using var r=cmd.ExecuteReader(); using var writer=new StreamWriter(path,false,new UTF8Encoding(true));
        writer.WriteLine("时间,目标IP,端口,探测方式,状态,延迟毫秒,尝试耗时毫秒,详情,网络环境,原始状态,配置超时毫秒");
        long count=0;
        while(r.Read())
        {
            var sample=ReadSample(r);
            writer.WriteLine(string.Join(",", Csv(sample.Time.ToLocalTime().ToString("O")),Csv(target.Address),target.Port==0?"":target.Port.ToString(),target.Protocol==ProbeProtocol.Tcp?"TCP connect":"ICMP",Csv(Sample.Label(sample.Status)),
                sample.LatencyMs?.ToString("F3",CultureInfo.InvariantCulture)??"",sample.ElapsedMs.ToString("F3",CultureInfo.InvariantCulture),Csv(sample.Detail),Csv(sample.Context),sample.NativeStatus,target.TimeoutMs==0?"未知":target.TimeoutMs.ToString())); count++;
        }
        return count;
    }
    private static string Csv(string text) => "\""+text.Replace("\"","\"\"")+"\"";
}
