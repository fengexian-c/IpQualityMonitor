using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class ChecksV2
{
    public static async Task Run(Action<bool,string> check)
    {
        var t=Target.Parse("127.0.0.1",double.NaN,ProbeProtocol.Icmp,1000);
        check(t.Port==0&&t.Key!=Target.Parse("127.0.0.1",443).Key,"ICMP does not require a TCP port or share its history");
        check(t.Key!=(t with{TimeoutMs=2000}).Key,"timeout parameters isolate measurement series");
        check(IcmpProbe.MapStatus(IPStatus.TtlExpired)!=ProbeStatus.Success,"TTL expiry is not endpoint success");
        {
            var ping=new IcmpProbe();
            var sample=await ping.RunAsync(t,TimeSpan.FromSeconds(1),CancellationToken.None);
            check(sample.Status==ProbeStatus.Success&&sample.LatencyMs>=0,"native Windows ICMP loopback includes valid zero RTT");
            int before=Process.GetCurrentProcess().HandleCount;
            for(int i=0;i<200;i++)await ping.RunAsync(t,TimeSpan.FromSeconds(1),CancellationToken.None);
            await Task.Delay(100);int after=Process.GetCurrentProcess().HandleCount;
            check(after-before<30,$"native endpoint ICMP keeps handle growth bounded ({before} to {after})");
        }
        foreach(var ip in new[]{IPAddress.Loopback,IPAddress.IPv6Loopback})
        {
            var hop=await new WindowsHopProbe().SendAsync(ip,1,1,1000,CancellationToken.None);
            check(hop.Status==0&&hop.Address==ip.ToString()&&hop.RttMs>=0,$"native route reply layout {ip}");
        }
        var fake=new FakeHop();var routeProbe=new RouteProbe(fake);
        var partial=await routeProbe.RunAsync(t,"ctx","test",new RouteOptions(3,3,100,3){InitialSpacingMs=0,SupplementBudgetSeconds=0},CancellationToken.None);
        check(partial.Probes.Count==9&&!partial.Reached&&fake.Maximum<=3,"route query budget and in-flight bound");
        using(var cancel=new CancellationTokenSource(20))
        {
            bool cancelled=false;try{await routeProbe.RunAsync(t,"ctx","test",new RouteOptions(32,3,100,3),cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            check(cancelled&&fake.Active==0,"cancel route drains in-flight probes before returning");
        }
        var stamp=DateTimeOffset.UtcNow;
        RouteRun Route(string[] ips,string ctx="ctx")=>new(Guid.NewGuid().ToString("N"),t.Key,t.Address,ctx,stamp,stamp,"test","complete",true,1500,32,ips.Select((ip,i)=>new HopProbe(i+1,1,ip=="?"?null:ip,ip=="?"?11010:11013,ip=="?"?null:10)).ToList());
        var original=Route(["10.0.0.1","10.0.0.2","10.0.0.3","10.0.0.4","127.0.0.1"]);
        var changed=Route(["10.0.0.1","10.0.0.2","10.0.0.9","10.0.0.4","127.0.0.1"]);
        check(RouteComparer.Compare(original,changed).Kind=="Candidate","path change requires confirmation");
        check(RouteComparer.Compare(original,Route(["10.0.0.1","?","10.0.0.3","10.0.0.4","127.0.0.1"])).Kind=="Visibility","missing hop is visibility change, not route change");
        check(RouteComparer.Compare(original,original with{Context="new"}).Kind=="Incomparable","network environments do not compare as remote route changes");
        check(RouteComparer.Compare(original,original with{Probes=original.Probes.Concat([new HopProbe(2,2,"10.0.0.99",11013,20)]).ToList()}).Kind=="Same","overlapping ECMP node sets do not trigger change");
        check(RouteComparer.Compare(original,Route(["10.0.0.1","10.0.0.2","10.0.0.9","10.0.0.3","10.0.0.4","127.0.0.1"])).Kind=="Candidate","inserted hop aligns common nodes");
        var detector=new IncidentDetector(TimeSpan.FromSeconds(5));int tick=0;
        Sample S(ProbeStatus status,double? value=null)=>new(stamp.AddSeconds(tick++*5),status,value,status==ProbeStatus.Success?value??10:1000,"test"){Context="ctx"};
        var initial=Enumerable.Range(0,6).SelectMany(_=>detector.Observe(S(ProbeStatus.Timeout))).ToArray();
        check(initial.Count(e=>e.Kind=="Unverified")==1&&!initial.Any(e=>e.Kind=="Unavailable"),"initial blocked ICMP is unverified, not repeated offline alerts");
        detector.Observe(S(ProbeStatus.Success,10));var down=Enumerable.Range(0,4).SelectMany(_=>detector.Observe(S(ProbeStatus.Timeout))).ToArray();
        check(down.Count(e=>e.Kind=="Unavailable")==1,"three failures create one incident");
        var recovered=Enumerable.Range(0,3).SelectMany(_=>detector.Observe(S(ProbeStatus.Success,10))).ToArray();
        check(recovered.Count(e=>e.Kind=="Recovered")==1,"recovery requires three successes");
        detector.Reset();tick=0;for(int i=0;i<140;i++)detector.Observe(S(ProbeStatus.Success,10));
        var high=Enumerable.Range(0,30).SelectMany(_=>detector.Observe(S(ProbeStatus.Success,200))).ToArray();
        check(high.Count(e=>e.Kind=="LatencyHigh")==1,"persistent latency detects once against stable baseline");
        var normal=Enumerable.Range(0,30).SelectMany(_=>detector.Observe(S(ProbeStatus.Success,10))).ToArray();
        check(normal.Count(e=>e.Kind=="LatencyRecovered")==1,"latency recovery has hysteresis");
        var gap=detector.Observe(new Sample(stamp.AddHours(1),ProbeStatus.Timeout,null,1000,"gap"){Context="ctx"});
        check(gap.Any(e=>e.Kind=="Gap")&&!gap.Any(e=>e.Kind=="Unavailable"),"unobserved gap resets incident state");

        var dir=Path.Combine(Path.GetTempPath(),"IpQuality-v2-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var file=Path.Combine(dir,"history.db");
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file}.ToString()))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE sample(id INTEGER PRIMARY KEY,target TEXT NOT NULL,time_ms INTEGER NOT NULL,status INTEGER NOT NULL,latency REAL,elapsed REAL NOT NULL,detail TEXT NOT NULL); INSERT INTO sample(target,time_ms,status,latency,elapsed,detail) VALUES('[127.0.0.1]:443',$time,0,50,50,'legacy')";
            cmd.Parameters.AddWithValue("$time",stamp.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();
        }
        var history=new History(file);history.Initialize();history.Initialize();
        check(File.Exists(file+".before-v2.bak")&&history.Load(Target.Parse("127.0.0.1",443),stamp).Day.Successes==1,"legacy migration creates backup, preserves data, and is repeatable");
        check(history.Load(t,stamp).Day.Attempts==0,"legacy data remains separate from new ICMP profile");
        var sampleNow=new Sample(stamp,ProbeStatus.Success,0,1,"quote, \"newline\"\ntext"){Context="network"};history.Add(t,sampleNow);
        history.SaveRoute(original);history.SaveRoute(changed);history.AddEvent(new MonitorEvent("test",t.Key,stamp,"RouteChanged","comparison",changed.Id,original.Id));
        check(history.LoadRoute(t,original.Id)?.Probes.Count==5&&history.LoadRoutes(t).Count==2,"route JSON roundtrip preserves all hops");
        check(history.LoadRoute(Target.Parse("127.0.0.2",0,ProbeProtocol.Icmp,1000),original.Id) is null,"route lookup cannot cross target identity");
        var bundle=Path.Combine(dir,"bundle.zip");history.ExportBundle(t,bundle,stamp);
        using(var zip=ZipFile.OpenRead(bundle))
        {
            using var json=JsonDocument.Parse(zip.GetEntry("routes.json")!.Open());
            check(zip.GetEntry("samples.csv") is not null&&json.RootElement.GetArrayLength()==2,"diagnostic ZIP contains samples, routes, events and profile");
        }
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file}.ToString()))
        {
            db.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO sample(target,time_ms,status,latency,elapsed,detail) VALUES($t,$time,$s,$ms,1,'bulk')";
            cmd.Parameters.AddWithValue("$t",t.Key);var ptime=cmd.Parameters.Add("$time",SqliteType.Integer);var pstatus=cmd.Parameters.Add("$s",SqliteType.Integer);var pms=cmd.Parameters.Add("$ms",SqliteType.Real);cmd.Prepare();
            for(int i=1;i<=535680;i++){ptime.Value=stamp.AddSeconds(-i*5L).ToUnixTimeMilliseconds();pstatus.Value=i%20==0?2:0;pms.Value=i%20==0?DBNull.Value:i%100;cmd.ExecuteNonQuery();}tx.Commit();
            var watch=Stopwatch.StartNew();var summary=history.Load(t,stamp);watch.Stop();
            cmd.Transaction=null;cmd.CommandText="SELECT COUNT(*),COUNT(CASE WHEN status=0 THEN 1 END),AVG(CASE WHEN status=0 THEN latency END) FROM sample WHERE target=$t AND time_ms >= $from AND time_ms <= $until";
            cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$t",t.Key);cmd.Parameters.AddWithValue("$from",stamp.AddDays(-30).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$until",stamp.ToUnixTimeMilliseconds());
            using var reader=cmd.ExecuteReader();reader.Read();
            check(summary.Month.Attempts==reader.GetInt64(0)&&summary.Month.Successes==reader.GetInt64(1)&&Math.Abs(summary.Month.Average!.Value-reader.GetDouble(2))<.00001,$"31-day aggregate agrees with raw SQL ({watch.ElapsedMilliseconds} ms dashboard)");
        }
        using(var cts=new CancellationTokenSource())
        {
            var monitor=new MonitorCoordinator(history,t,new MonitorOptions(1,1000,1,false));int count=0;monitor.SampleSaved+=_=>Interlocked.Increment(ref count);
            var task=monitor.RunAsync(cts.Token);await Task.Delay(1300);monitor.PowerChanged(true);await Task.Delay(400);int paused=count;await Task.Delay(1200);
            check(count==paused,"power suspension produces no samples");monitor.PowerChanged(false);await Task.Delay(1300);cts.Cancel();await task;
            check(count>paused&&history.LoadEvents(t,20).Any(e=>e.Kind=="Resume"),"resume records gap and restarts sampling");
            int stopped=count;await Task.Delay(200);check(stopped==count&&history.LoadEvents(t,2).Any(e=>e.Kind=="Stopped"),"coordinator stop drains records and ends sampling");
        }
        Console.WriteLine("V2 test artifacts: "+dir);
    }
    private sealed class FakeHop:IHopProbe
    {
        public int Active,Maximum;
        public async Task<HopProbe> SendAsync(IPAddress target,int ttl,int seq,int timeout,CancellationToken token)
        {int n=Interlocked.Increment(ref Active);Maximum=Math.Max(Maximum,n);try{await Task.Delay(50,token);return new(ttl,seq,null,11010,null);}finally{Interlocked.Decrement(ref Active);}}
    }
}
