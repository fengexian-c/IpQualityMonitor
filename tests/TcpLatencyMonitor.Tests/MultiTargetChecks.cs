using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class MultiTargetChecks
{
    private sealed class FakeProbe(Func<Target,TimeSpan,CancellationToken,Task<Sample>> action):IProbe
    {public Task<Sample> RunAsync(Target target,TimeSpan timeout,CancellationToken token)=>action(target,timeout,token);}
    private sealed class SlowMetadata:HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {await Task.Delay(10000,token);return new(System.Net.HttpStatusCode.ServiceUnavailable);}
    }
    private static async Task WaitFor(Func<bool> condition,int milliseconds=6000)
    {
        var clock=Stopwatch.StartNew();while(!condition()){if(clock.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Multi-target check did not reach its expected state.");await Task.Delay(30);}
    }
    private static RouteRun Route(Target t,string context,string reason)=>new(Guid.NewGuid().ToString("N"),t.Key,t.Address,context,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,reason,"fixture",true,300,3,new[]{new HopProbe(1,1,t.Address,0,3)});
    public static async Task Run(Action<bool,string> check)
    {
        var folder=Path.Combine(Path.GetTempPath(),"IpQuality-multi-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var profile=new TargetProfile{Address="203.0.113.1",Name="A",Mode="Both",Port=443,EnableRoutes=false};profile.Validate();
        var renamed=profile.Copy();renamed.Name="new name";
        check(renamed.Id==profile.Id&&renamed.Primary.Key==profile.Primary.Key,"renaming preserves profile and measurement identities");
        var port=profile.Copy();port.Port=8443;port.RetainPrevious(profile);
        check(port.Primary.Key==profile.Primary.Key&&port.Secondary!.Key!=profile.Secondary!.Key&&port.PreviousMeasurements.Count==1,"same IP can retain distinct TCP ports and old configuration history");
        var alias=profile.Copy();alias.Address="::ffff:203.0.113.1";
        check(alias.Primary.Key==profile.Primary.Key,"normalized IP aliases share measurement identity");
        await using(var history=new History(Path.Combine(folder,"writer.db")))
        {
            history.Initialize();var time=DateTimeOffset.UtcNow;
            await Task.WhenAll(Enumerable.Range(0,600).Select(i=>history.RecordAsync(()=>history.Add(profile.Primary,new(time.AddMilliseconds(-i),ProbeStatus.Success,i%100,1,"parallel writer fixture")))));
            var saved=history.Load(profile.Primary,DateTimeOffset.UtcNow);
            check(saved.Hour.Attempts==600&&saved.Hour.Successes==600,"shared writer commits every concurrent sample before acknowledgment");
            check(history.CommittedBatches<610,"writer supports batched commits across concurrent producers");
            int reservations=0;
            await Task.WhenAll(Enumerable.Range(0,40).Select(_=>Task.Run(()=>{if(history.ReserveMetadataRequest(time))Interlocked.Increment(ref reservations);})));
            check(reservations==40,"metadata request reservations share the same transactional writer");
            var b=profile.Copy();b.Id=Guid.NewGuid().ToString("N");b.Address="203.0.113.2";b.Mode="Icmp";
            var snap=history.LoadOverview(new[]{profile,b},ProbeProtocol.Icmp,time,7);
            check(snap.Targets.Count==2&&snap.Targets[0].Timeline!.Until==snap.Targets[1].Timeline!.Until,"multi-target statistics use one database snapshot and one cutoff");
            check(snap.Targets[0].Timeline!.Points.Sum(p=>p.Attempts)==snap.Targets[0].Timeline!.Hours.Sum(p=>p.Attempts),"comparison lines and hourly grid use equal sample counts");
            check(snap.Targets[0].P95Hour==94&&snap.Targets[1].P95Hour is null,"hour P95 uses raw successful samples and leaves empty histories blank");
            check(snap.Targets[1].Timeline!.Total.Availability is null,"no history is not converted into zero latency or healthy availability");
            check(history.LoadOverview(new[]{b},ProbeProtocol.Tcp,time,1).Targets[0].Target is null,"disabled comparison protocol is represented as unavailable, not failed");
            await history.RecordAsync(()=>{for(int i=0;i<4200;i++)history.Add(b.Primary,new(time.AddDays(-32),ProbeStatus.Success,10,10,"old fixture"));});
            history.PruneStep(time);
            using(var db=new SqliteConnection("Data Source="+Path.Combine(folder,"writer.db")))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM sample WHERE detail='old fixture'";
                check((long)cmd.ExecuteScalar()!>=2200,"one maintenance pass bounds raw history deletion to 2000 rows");
            }
            history.Prune(time);check(history.Load(b.Primary,time).Month.Attempts==0,"chunked retention preserves the 31-day policy");
        }
        await using(var history=new History(Path.Combine(folder,"queued-annotations.db")))
        {
            history.Initialize();using var client=new NodeMetadataClient(history,new SlowMetadata());
            await using var annotations=new RouteAnnotationService(history,client,120);annotations.Mode=2;
            var a=Route(profile.Primary,"fixture","fixture") with{Probes=new[]{new HopProbe(1,1,"1.1.1.1",11013,5)}};
            var b=a with{Id=Guid.NewGuid().ToString("N"),Probes=new[]{new HopProbe(1,1,"8.8.8.8",11013,5)}};
            history.SaveRoute(a);history.SaveRoute(b);var clock=Stopwatch.StartNew();
            await Task.WhenAll(annotations.Request(a,false),annotations.Request(b,false));
            check(clock.ElapsedMilliseconds>=200&&history.LoadRouteAnnotation(a.Id) is not null&&history.LoadRouteAnnotation(b.Id) is not null,"each queued annotation receives its own execution budget and retains its local explanation");
        }
        int routeCalls=0;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var routes=new RouteScheduler(async(t,c,r,o,ct,p)=>{Interlocked.Increment(ref routeCalls);entered.TrySetResult();await release.Task.WaitAsync(ct);return Route(t,c,r);}))
        {
            using var cancelled=new CancellationTokenSource();var a=routes.RunAsync(profile.Primary,"A","手动检查",new(),cancelled.Token);
            await entered.Task;var b=routes.RunAsync(profile.Secondary!,"A","手动检查",new(),CancellationToken.None);
            cancelled.Cancel();try{await a;}catch(OperationCanceledException){}
            release.TrySetResult();var result=await b;
            check(routeCalls==1&&result.TargetKey==profile.Secondary!.Key,"ICMP and TCP route requests coalesce while retaining the caller's history key");
            check(result.Reached,"cancelling one route subscriber leaves another subscriber's task alive");
        }
        var order=new ConcurrentQueue<string>();var blocker=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var active=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var routes=new RouteScheduler(async(t,c,r,o,ct,p)=>{order.Enqueue(t.Address);if(t.Address.EndsWith(".1")){active.TrySetResult();await blocker.Task.WaitAsync(ct);}return Route(t,c,r);}))
        {
            var first=routes.RunAsync(profile.Primary,"A","开始监控",new(),CancellationToken.None);await active.Task;
            var periodic=routes.RunAsync(Target.Parse("203.0.113.2",0,ProbeProtocol.Icmp,3000),"A","定期检查",new(),CancellationToken.None);
            var urgent=Enumerable.Range(3,5).Select(i=>routes.RunAsync(Target.Parse($"203.0.113.{i}",0,ProbeProtocol.Icmp,3000),"A","手动检查",new(),CancellationToken.None)).ToArray();
            using var cancel=new CancellationTokenSource();var removed=routes.RunAsync(Target.Parse("203.0.113.9",0,ProbeProtocol.Icmp,3000),"A","定期检查",new(),cancel.Token);cancel.Cancel();
            blocker.TrySetResult();await Task.WhenAll(urgent.Append(first).Append(periodic));try{await removed;}catch(OperationCanceledException){}
            var sequence=order.ToArray();check(Array.IndexOf(sequence,"203.0.113.2")<=4,"periodic route work is served after at most three urgent jobs");
            check(!sequence.Contains("203.0.113.9"),"cancelled queued routes do not send probes");
        }
        string currentContext="A";int dispatched=0;var dispatchEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var dispatchRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var resources=new MonitorResources(contexts:_=>new(currentContext,"mock"),routeProbe:async(t,c,r,o,ct,p)=>
        {Interlocked.Increment(ref dispatched);if(t.Address.EndsWith(".1")){dispatchEntered.TrySetResult();await dispatchRelease.Task.WaitAsync(ct);}return Route(t,c,r);}))
        {
            var first=resources.Routes.RunAsync(profile.Primary,"A","开始监控",new(),CancellationToken.None);await dispatchEntered.Task;
            var second=resources.Routes.RunAsync(Target.Parse("203.0.113.2",0,ProbeProtocol.Icmp,3000),"A","定期检查",new(),CancellationToken.None);
            currentContext="B";dispatchRelease.TrySetResult();await first;bool invalidated=false;try{await second;}catch(OperationCanceledException){invalidated=true;}
            check(invalidated&&dispatched==1,"queued routes recheck the network context before sending any probes");
        }
        await using(var history=new History(Path.Combine(folder,"isolated.db")))
        {
            history.Initialize();var resources=new MonitorResources(4,_=>new FakeProbe(async(t,timeout,ct)=>
            {await Task.Delay(30,ct);if(t.Address.EndsWith(".1"))throw new IOException("simulated target probe failure");return new(DateTimeOffset.UtcNow,ProbeStatus.Success,30,30,"healthy target");}),_=>new("fixture","mock"));
            await using var manager=new MultiTargetMonitor(history,resources);var failed=new ConcurrentQueue<string>();manager.TargetFailed+=(id,_)=>failed.Enqueue(id);
            var a=new TargetProfile{Address="203.0.113.1",EnableRoutes=false,IntervalSeconds=1};var b=a.Copy();b.Id=Guid.NewGuid().ToString("N");b.Address="203.0.113.2";
            await manager.StartAsync(a);await manager.StartAsync(b);await WaitFor(()=>failed.Count==1&&manager.State(b.Primary)?.Success>=2);
            check(failed.Single()==a.Id&&manager.IsRunning(b.Id)&&history.WriteFailure is null,"an individual probe exception does not stop unrelated targets or the shared writer");
        }
        int inflight=0,maximum=0;var probes=new ConcurrentDictionary<string,int>();
        await using(var history=new History(Path.Combine(folder,"twenty.db")))
        {
            history.Initialize();
            var resources=new MonitorResources(32,_=>new FakeProbe(async(t,timeout,ct)=>
            {
                int current=Interlocked.Increment(ref inflight);int observed;
                do{observed=maximum;if(current<=observed)break;}while(Interlocked.CompareExchange(ref maximum,current,observed)!=observed);
                var started=DateTimeOffset.UtcNow;
                try{await Task.Delay(timeout,ct);probes.AddOrUpdate(t.Key,1,(_,n)=>n+1);return new(started,ProbeStatus.Timeout,null,timeout.TotalMilliseconds,"simulated endpoint timeout");}
                finally{Interlocked.Decrement(ref inflight);}
            }),_=>new("fixture","mock context"));
            await using var manager=new MultiTargetMonitor(history,resources);
            var targets=Enumerable.Range(1,20).Select(i=>new TargetProfile{Address=$"203.0.113.{i}",Mode="Both",EnableRoutes=false,IntervalSeconds=5,TimeoutMilliseconds=3000}).ToArray();
            foreach(var target in targets)await manager.StartAsync(target);
            await WaitFor(()=>targets.All(t=>manager.State(t.Primary)?.Failure>=1&&manager.State(t.Secondary!)?.Failure>=1),14000);
            check(manager.RunningProfiles==20&&maximum<=32&&maximum>=20,"20 dual-protocol timeout targets run with bounded global concurrency");
            var stopped=targets[0];await manager.StopAsync(stopped.Id);long before=manager.State(stopped.Primary)!.Failure;
            var remaining=targets[1];long other=manager.State(remaining.Primary)!.Failure;
            await WaitFor(()=>manager.State(remaining.Primary)!.Failure>other,9000);
            check(manager.State(stopped.Primary)!.Failure==before&&!manager.IsRunning(stopped.Id)&&manager.RunningProfiles==19,"pausing one target leaves nineteen others sampling");
            manager.PowerChanged(true);await Task.Delay(3500);
            long suspended=manager.State(remaining.Primary)!.Failure;await Task.Delay(1100);
            check(manager.State(remaining.Primary)!.Failure==suspended,"suspension creates no fabricated timeout records");
            manager.PowerChanged(false);await WaitFor(()=>manager.State(remaining.Primary)!.Failure>suspended,9000);
            check(manager.IsRunning(remaining.Id),"sampling resumes after a power transition");
            await manager.StopAllAsync();check(inflight==0&&manager.RunningProfiles==0,"stop-all drains every in-flight probe before returning");
            var snapshot=history.LoadOverview(targets,ProbeProtocol.Icmp,DateTimeOffset.UtcNow,7);
            check(snapshot.Targets.All(t=>t.Timeline!.Total.Attempts>0),"all twenty targets retain independently queryable history");
        }
        await using(var history=new History(Path.Combine(folder,"shared.db")))
        {
            history.Initialize();var resources=new MonitorResources(4,_=>new FakeProbe(async(t,timeout,ct)=>{await Task.Delay(10,ct);return new(DateTimeOffset.UtcNow,ProbeStatus.Success,10,10,"shared fixture");}),_=>new("fixture","mock"));
            await using var manager=new MultiTargetMonitor(history,resources);
            var a=new TargetProfile{Address="203.0.113.1",EnableRoutes=false,IntervalSeconds=1};var b=a.Copy();b.Id=Guid.NewGuid().ToString("N");
            await manager.StartAsync(a);await manager.StartAsync(b);await WaitFor(()=>manager.State(a.Primary)?.Success>=2);
            var count=manager.State(a.Primary)!.Success;await manager.StopAsync(a.Id);await WaitFor(()=>manager.State(b.Primary)!.Success>count);
            check(manager.IsRunning(b.Id)&&!manager.IsRunning(a.Id),"identical measurements use reference-counted ownership");
            var incompatible=a.Copy();incompatible.Id=Guid.NewGuid().ToString("N");incompatible.IntervalSeconds=2;
            bool rejected=false;try{await manager.StartAsync(incompatible);}catch(ArgumentException){rejected=true;}
            check(rejected&&manager.RunningProfiles==1,"incompatible sampling parameters cannot silently overwrite a shared series");
        }
        await using(var history=new History(Path.Combine(folder,"busy.db")))
        {
            history.Initialize();var resources=new MonitorResources(1,_=>new FakeProbe(async(t,timeout,ct)=>{await Task.Delay(timeout,ct);return new(DateTimeOffset.UtcNow-timeout,ProbeStatus.Timeout,null,timeout.TotalMilliseconds,"timeout");}),_=>new("fixture","mock"));
            await using var manager=new MultiTargetMonitor(history,resources);var targets=Enumerable.Range(1,6).Select(i=>new TargetProfile{Address=$"203.0.113.{i}",EnableRoutes=false,IntervalSeconds=1,TimeoutMilliseconds=3000}).ToArray();
            foreach(var target in targets)await manager.StartAsync(target);
            await WaitFor(()=>targets.Sum(t=>manager.State(t.Primary)?.Skipped??0)>=3);
            await manager.StopAllAsync();var snapshot=history.LoadOverview(targets,ProbeProtocol.Icmp,DateTimeOffset.UtcNow,1);
            check(snapshot.Targets.Sum(t=>t.Timeline!.Total.Attempts)==0,"local concurrency exhaustion records gaps without inventing remote failures");
        }
        await using(var history=new History(Path.Combine(folder,"failure.db")))
        {
            history.Initialize();var resources=new MonitorResources(4,_=>new FakeProbe(async(t,timeout,ct)=>{await Task.Delay(20,ct);return new(DateTimeOffset.UtcNow,ProbeStatus.Success,20,20,"failure fixture");}),_=>new("fixture","mock"));
            await using var manager=new MultiTargetMonitor(history,resources);int failed=0;manager.StorageFailed+=_=>Interlocked.Increment(ref failed);
            using(var db=new SqliteConnection("Data Source="+Path.Combine(folder,"failure.db"))){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TRIGGER fail_sample BEFORE INSERT ON sample BEGIN SELECT RAISE(ABORT,'simulated storage failure'); END";cmd.ExecuteNonQuery();}
            var a=new TargetProfile{Address="203.0.113.1",EnableRoutes=false,IntervalSeconds=1};var b=a.Copy();b.Id=Guid.NewGuid().ToString("N");b.Address="203.0.113.2";
            await manager.StartAsync(a);await manager.StartAsync(b);await WaitFor(()=>failed>0);
            await manager.StopAllAsync();check(manager.RunningProfiles==0&&history.WriteFailure is not null,"storage failure is global and stops every producer without deadlock");
            check(history.Load(a.Primary,DateTimeOffset.UtcNow).Hour.Attempts==0,"failed transactions do not acknowledge or display uncommitted samples");
        }
        Console.WriteLine("Multi-target fixtures: "+folder);
    }
}
