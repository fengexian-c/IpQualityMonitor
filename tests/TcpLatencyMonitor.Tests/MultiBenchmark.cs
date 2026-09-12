using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class MultiBenchmark
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"history.db");
        if(File.Exists(path))throw new IOException("Benchmark requires a fresh directory.");
        await using var history=new History(path);history.Initialize();
        var profiles=Enumerable.Range(1,20).Select(i=>new TargetProfile{Name="Fixture "+i,Address=$"203.0.113.{i}",Mode="Both",EnableRoutes=false}).ToArray();
        var now=DateTimeOffset.UtcNow;var seed=Stopwatch.StartNew();
        using(var db=new SqliteConnection("Data Source="+path))
        {
            db.Open();using(var setup=db.CreateCommand()){setup.CommandText="PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";setup.ExecuteNonQuery();}
            for(int day=0;day<7;day++)
            {
                foreach(var profile in profiles)
                foreach(var target in new[]{profile.Primary,profile.Secondary!})
                {
                    using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandTimeout=30;
                    cmd.CommandText="""
                        WITH RECURSIVE ticks(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM ticks WHERE n<17279)
                        INSERT INTO sample(target,time_ms,status,latency,elapsed,detail,context,native_status)
                        SELECT $target,$now-($day*17280+n)*5000,CASE WHEN n%97=0 THEN 2 ELSE 0 END,
                          CASE WHEN n%97=0 THEN NULL ELSE 20+n%200 END,CASE WHEN n%97=0 THEN 3000 ELSE 20+n%200 END,
                          'offline benchmark','fixture',0 FROM ticks;
                        """;
                    cmd.Parameters.AddWithValue("$target",target.Key);cmd.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$day",day);cmd.ExecuteNonQuery();tx.Commit();
                }
                Console.WriteLine($"SEEDED day={day+1}/7 rows={(day+1)*691200} elapsed={seed.Elapsed.TotalSeconds:0.0}s");
            }
        }
        Console.WriteLine($"SEED COMPLETE rows=4838400 seconds={seed.Elapsed.TotalSeconds:0.0}");
        foreach(int days in new[]{7,1,7})
        {
            var clock=Stopwatch.StartNew();var result=history.LoadOverview(profiles,ProbeProtocol.Icmp,now,days);clock.Stop();
            if(result.Targets.Count!=20||result.Targets.Any(t=>t.Timeline!.Total.Attempts==0||t.P95Hour is null))throw new InvalidOperationException("Missing benchmark statistics");
            if(result.Targets.Any(t=>t.Timeline!.Points.Sum(p=>p.Attempts)!=t.Timeline.Hours.Sum(p=>p.Attempts)))throw new InvalidOperationException("Inconsistent benchmark timeline");
            Console.WriteLine($"OVERVIEW days={days} targets=20 milliseconds={clock.Elapsed.TotalMilliseconds:0.0} samples={result.Targets.Sum(t=>t.Timeline!.Total.Attempts)}");
        }
        var detailClock=Stopwatch.StartNew();_=history.Load(profiles[0].Primary,now);detailClock.Stop();
        Console.WriteLine($"DETAIL milliseconds={detailClock.Elapsed.TotalMilliseconds:0.0}");
        long committed=history.CommittedBatches;
        var writes=Stopwatch.StartNew();await Task.WhenAll(profiles.SelectMany(p=>new[]{p.Primary,p.Secondary!}).Select(t=>history.RecordAsync(()=>history.Add(t,new(DateTimeOffset.UtcNow,ProbeStatus.Success,10,10,"live write fixture")))));writes.Stop();
        Console.WriteLine($"WRITE samples=40 milliseconds={writes.Elapsed.TotalMilliseconds:0.0} batches={history.CommittedBatches-committed}");
        Console.WriteLine($"DATABASE bytes={new FileInfo(path).Length} working_set={Process.GetCurrentProcess().WorkingSet64}");
        Console.WriteLine("BENCHMARK PASSED · generated data only; no network traffic");
    }
}
