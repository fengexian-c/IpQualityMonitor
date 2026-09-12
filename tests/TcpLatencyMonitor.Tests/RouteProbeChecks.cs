using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using TcpLatencyMonitor.Core;

internal static class RouteProbeChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var target=Target.Parse("127.0.0.1",0,ProbeProtocol.Icmp,1000);
        var options=new RouteOptions(2,3,100,5){InitialSpacingMs=40,SupplementSpacingMs=60};
        Task<RouteRun> Run(FakeProbe fake,RouteOptions? settings=null,CancellationToken token=default)=>new RouteProbe(fake).RunAsync(target,"test","fixture",settings??options,token);
        var paced=new FakeProbe((ttl,seq)=>ttl==2?Reply(ttl,seq,target.Address,0):seq==1?Reply(ttl,seq,"10.0.0.1"):Silent(ttl,seq));
        var pacedRun=await Run(paced);
        check(pacedRun.Reached&&pacedRun.Probes.Count==6&&!pacedRun.Probes.Any(p=>p.IsSupplemental),"1/3 already identifies a hop and is not retried to inflate its response rate");
        check(paced.Calls.Zip(paced.Calls.Skip(1)).All(pair=>pair.Second.Time-pair.First.Time>=38),"initial probes are paced across both queries and TTL boundaries");
        check(paced.Maximum<=3&&paced.Active==0&&pacedRun.Probes.All(p=>p.SentAt.HasValue),"paced probes retain send times and leave no calls in flight");

        var capped=await Run(new FakeProbe(Silent),options with{MaxHops=1,InitialSpacingMs=0,SupplementSpacingMs=0});
        check(capped.Probes.Count==10&&capped.Probes.Count(p=>p.IsSupplemental)==7&&capped.Probes.Select(p=>p.Sequence).SequenceEqual(Enumerable.Range(1,10)),"a wholly silent TTL gets at most ten total actual attempts, including three initial attempts");
        check(capped.Probes.Take(3).All(p=>!p.IsSupplemental)&&capped.Probes.Take(3).All(p=>p.Status==11010),"supplemental measurements append without replacing original timeouts");

        var fair=new FakeProbe((ttl,seq)=>ttl==3?Reply(ttl,seq,target.Address,0):ttl==1&&seq==4?Reply(ttl,seq,"59.43.250.50"):ttl==2&&seq==5?Reply(ttl,seq,"59.43.246.178"):Silent(ttl,seq));
        var filled=await Run(fair,options with{MaxHops=3});
        var supplements=filled.Probes.Where(p=>p.IsSupplemental).ToArray();
        check(filled.Probes.Take(9).All(p=>!p.IsSupplemental)&&supplements.Select(p=>p.Ttl).SequenceEqual(new[]{1,2,2}),"discovery completes first, then silent TTLs receive retries in round-robin order");
        check(supplements.Length==3&&filled.SupplementOutcome.Contains("补全 2 跳"),"each TTL stops supplementing immediately after its first response");
        var retryCalls=fair.Calls.Where(c=>c.Sequence>3).ToArray();
        check(retryCalls.Zip(retryCalls.Skip(1)).All(pair=>pair.Second.Time-pair.First.Time>=58),"supplemental pacing also applies between different silent TTLs");

        var fatal=await Run(new FakeProbe((ttl,seq)=>new(ttl,seq,null,11050,null)));
        check(fatal.Probes.Count==3&&!fatal.Probes.Any(p=>p.IsSupplemental)&&fatal.Outcome.Contains("错误"),"local errors stop discovery without starting a timeout retry storm");
        var unreachable=await Run(new FakeProbe((ttl,seq)=>new(ttl,seq,"10.0.0.1",11003,null)));
        check(unreachable.Probes.Count==3&&!unreachable.Probes.Any(p=>p.IsSupplemental)&&unreachable.Outcome.Contains("不可达"),"explicit unreachable replies are not treated as silent nodes");
        var retryError=await Run(new FakeProbe((ttl,seq)=>seq<=3?Silent(ttl,seq):new(ttl,seq,null,11050,null)),options with{MaxHops=1});
        check(retryError.Probes.Count==4&&retryError.SupplementOutcome.Contains("11050"),"supplemental local errors are retained and stop further retries");

        var eventualTarget=await Run(new FakeProbe((ttl,seq)=>seq<=3?Silent(ttl,seq):Reply(ttl,seq,target.Address,0)),options with{MaxHops=1});
        check(eventualTarget.Reached&&eventualTarget.InitialReached==false&&eventualTarget.Outcome.Contains("补测收到"),"a target discovered during supplementation retains the initial reachability observation");

        var deadlineProbe=new FakeProbe(Silent){HonorTimeout=true};var timer=Stopwatch.StartNew();
        var bounded=await Run(deadlineProbe,options with{MaxHops=1,TimeoutMs=350,BudgetSeconds=1,InitialSpacingMs=0,SupplementSpacingMs=0});
        check(timer.ElapsedMilliseconds<1400&&bounded.Probes.Count<10&&bounded.SupplementOutcome.Contains("预算"),"the combined discovery and retry work obeys the original one-second total deadline");
        check(deadlineProbe.Calls.Any(c=>c.Timeout<350),"last native call receives the remaining budget rather than a fresh full timeout");
        timer.Restart();
        var supplementalBound=await Run(new FakeProbe(Silent){HonorTimeout=true},options with{MaxHops=1,TimeoutMs=250,BudgetSeconds=5,InitialSpacingMs=0,SupplementSpacingMs=0,SupplementBudgetSeconds=1});
        check(timer.ElapsedMilliseconds<1650&&supplementalBound.Probes.Count<10&&supplementalBound.SupplementOutcome.Contains("预算"),"the separate supplemental deadline applies even with ample total budget");
        var partial=await Run(new FakeProbe((ttl,seq)=>Reply(ttl,seq,"10.0.0.1")),options with{MaxHops=1,BudgetSeconds=1,InitialSpacingMs=600});
        check(partial.Probes.Count==2&&RouteHopDisplay.Title(1,partial.Probes).Contains("回应 2/2"),"budget-truncated initial queries do not fabricate unsent attempts or timeouts");

        using(var cancel=new CancellationTokenSource(40))
        {
            var active=new FakeProbe(Silent){DelayMs=130};bool stopped=false;
            try{await Run(active,options with{InitialSpacingMs=400},cancel.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped&&active.Active==0&&active.Calls.Count==1,"cancellation during initial pacing drains the native call before returning");
        }
        using(var cancel=new CancellationTokenSource())
        {
            var active=new FakeProbe(Silent){SupplementDelayMs=100,OnSupplement=()=>cancel.CancelAfter(20)};bool stopped=false;
            try{await Run(active,options with{MaxHops=1,InitialSpacingMs=0,SupplementSpacingMs=0},cancel.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped&&active.Active==0&&active.Calls.Count==4,"cancellation during supplemental work drains the in-flight call and creates no later probes");
        }
        var oneReply=new[]{Reply(7,1,"202.97.43.82"),Silent(7,2),Silent(7,3)};
        check(RouteHopDisplay.Title(7,oneReply)=="第 7 跳 · 回应 1/3 · 超时 2 次","partial-response title matches the requested compact wording");
        check(RouteHopDisplay.Title(5,Enumerable.Range(1,3).Select(i=>Silent(5,i)))=="第 5 跳 · 回应 0/3 · 超时 3 次","all-silent title has the real timeout count with no duplicated placeholder words");
        check(RouteHopDisplay.Title(1,eventualTarget.Probes).Contains("回应 1/4 · 超时 3 次（初测 0/3，补测 1/1）"),"combined title distinguishes initial and supplemental numerators and denominators");
        check(RouteHopDisplay.Title(1,retryError.Probes).Contains("超时 3 次 · 探测错误 11050 × 1"),"local errors remain visible without being counted as timeouts");
        check(RouteComparer.Compare(pacedRun,pacedRun with{ProbeOptions=null}).Kind=="Incomparable","historical and paced policies are not compared as a route change");
        var extra=pacedRun with{Probes=pacedRun.Probes.Concat(new[]{Reply(1,4,"10.0.0.99") with{IsSupplemental=true}}).ToArray()};
        check(RouteComparer.Compare(pacedRun,extra).Kind=="Same","supplemental discoveries cannot introduce a false route-change candidate");

        var directory=Path.Combine(Path.GetTempPath(),"IpQuality-route-retries-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var history=new History(Path.Combine(directory,"history.db"));history.Initialize();history.SaveRoute(filled);
        var stored=history.LoadRoute(target,filled.Id)!;
        check(stored.ProbeOptions==filled.ProbeOptions&&stored.InitialReached==filled.InitialReached&&stored.Probes.SequenceEqual(filled.Probes)&&stored.SupplementOutcome==filled.SupplementOutcome,"SQLite JSON roundtrip preserves pacing policy, phases, timestamps, and every original result");
        var legacy=filled with{Id=Guid.NewGuid().ToString("N"),ProbeOptions=null,InitialReached=null,Probes=oneReply};history.SaveRoute(legacy);
        check(history.LoadRoute(target,legacy.Id) is {ProbeOptions:null,InitialReached:null} old&&old.Probes.All(p=>!p.IsSupplemental&&p.SentAt is null),"legacy observations retain unknown policy and are not reinterpreted as supplemental probes");
        var zipPath=Path.Combine(directory,"export.zip");history.ExportBundle(target,zipPath,DateTimeOffset.UtcNow);
        using(var zip=ZipFile.OpenRead(zipPath))
        using(var json=JsonDocument.Parse(zip.GetEntry("routes.json")!.Open()))
            check(json.RootElement.EnumerateArray().Any(r=>r.GetProperty("Probes").EnumerateArray().Any(p=>p.GetProperty("IsSupplemental").GetBoolean())),"diagnostic export includes supplemental provenance");
        bool rejected=false;try{await Run(new FakeProbe(Silent),options with{MaxAttemptsPerHop=11});}catch(ArgumentException){rejected=true;}
        check(rejected,"configuration rejects more than ten total attempts per TTL");
        Console.WriteLine("Route retry fixtures: "+directory);
    }
    private static HopProbe Silent(int ttl,int sequence)=>new(ttl,sequence,null,11010,null);
    private static HopProbe Reply(int ttl,int sequence,string address="10.0.0.1",int status=11013)=>new(ttl,sequence,address,status,5);
    private sealed class FakeProbe(Func<int,int,HopProbe> response):IHopProbe
    {
        private readonly Stopwatch _clock=Stopwatch.StartNew();
        public List<(int Ttl,int Sequence,long Time,int Timeout)> Calls {get;}=new();
        public int Active,Maximum,DelayMs=1,SupplementDelayMs;
        public bool HonorTimeout;
        public Action? OnSupplement;
        public async Task<HopProbe> SendAsync(IPAddress target,int ttl,int sequence,int timeout,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add((ttl,sequence,_clock.ElapsedMilliseconds,timeout));Maximum=Math.Max(Maximum,Interlocked.Increment(ref Active));
            try
            {
                if(sequence>3)OnSupplement?.Invoke();
                // Simulate non-interruptible native work with a bounded timeout.
                await Task.Delay(HonorTimeout?timeout:sequence>3&&SupplementDelayMs>0?SupplementDelayMs:DelayMs);
                token.ThrowIfCancellationRequested();return response(ttl,sequence);
            }
            finally{Interlocked.Decrement(ref Active);}
        }
    }
}
