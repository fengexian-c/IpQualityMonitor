using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class AnnotationSchedulerChecks
{
    public static async Task RunAsync(Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(Path.GetTempPath(),"iqm-annotation-scheduler-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            await Overflow(Path.Combine(folder,"overflow.db"),check);
            await Restart(Path.Combine(folder,"restart.db"),check);
            await Generation(Path.Combine(folder,"generation.db"),check);
            await Fairness(Path.Combine(folder,"fairness.db"),check);
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
    private static readonly TimeSpan Deadline=TimeSpan.FromSeconds(10);
    private static async Task Until(Func<bool> condition,string message)
    {
        using var timeout=new CancellationTokenSource(Deadline);
        try{while(!condition())await Task.Delay(10,timeout.Token);}
        catch(OperationCanceledException){throw new TimeoutException(message);}
    }
    private static RouteRun Route(string id,Target target,params string[] addresses)
    {
        var now=DateTimeOffset.UtcNow.AddSeconds(-1);
        return new(id,target.Key,target.Address,"fixture",now,now,"fixture","partial",false,500,32,
            addresses.Select((ip,i)=>new HopProbe(i+1,1,ip,11013,i+1)).ToArray());
    }
    private static string Raw(string path,string id)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString());db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT json FROM route_run WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);return (string)cmd.ExecuteScalar()!;
    }
    private static NodeMetadataClient Client(History history,Lookup lookup,NoHttp http)
    {
        var client=new NodeMetadataClient(history,http,lookup,new("v3",TimeSpan.FromSeconds(5)));
        client.Configure(new MetadataOptions(Primary:"nexttrace"));return client;
    }
    private static async Task Overflow(string path,Action<bool,string> check)
    {
        var target=Target.Parse("10.0.0.1",0,ProbeProtocol.Icmp,500);
        await using var history=new History(path);history.Initialize();
        var routes=Enumerable.Range(0,32).Select(i=>Route("overflow-"+i,target,"10.1.0."+(i+1))).ToArray();
        foreach(var route in routes)history.SaveRoute(route);
        var raw=routes.ToDictionary(route=>route.Id,route=>Raw(path,route.Id));
        using var http=new NoHttp();using var lookup=new Lookup((ip,token)=>Task.FromResult(Json(ip)));
        using var client=Client(history,lookup,http);
        bool recover=false;
        await using var service=new RouteAnnotationService(history,client,targetKeys:()=>recover?[target.Key]:[],queueCapacity:1);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held=0;
        service.Saved+=_=>
        {
            if(Interlocked.CompareExchange(ref held,1,0)!=0)return;
            entered.TrySetResult();if(!release.Wait(Deadline))throw new TimeoutException("Fixture worker was not released");
        };
        var initial=service.Request(routes[0],false);Task<RouteAnnotation?>[] pending=[];
        try
        {
            await entered.Task.WaitAsync(Deadline);
            pending=routes.Skip(1).Select(route=>service.Request(route,false)).ToArray();
            check(pending.Count(task=>task.IsCompletedSuccessfully&&task.Result is null)>=routes.Length-3,
                "capacity-one annotation queue rejects overflow without retaining an unbounded backlog");
        }
        finally{release.Set();}
        await initial.WaitAsync(Deadline);await Task.WhenAll(pending).WaitAsync(Deadline);
        recover=true;
        using(var deadline=new CancellationTokenSource(Deadline))
        {
            while(routes.Any(route=>history.LoadRouteAnnotation(route.Id) is null))
            {
                deadline.Token.ThrowIfCancellationRequested();service.RecoverRecent();await Task.Delay(10,deadline.Token);
            }
        }
        check(routes.All(route=>history.LoadRouteAnnotation(route.Id) is not null)&&lookup.Addresses.IsEmpty&&http.Calls==0,
            "bounded recovery fills dropped private-route jobs without any provider calls");
        check(routes.All(route=>Raw(path,route.Id)==raw[route.Id]),"overflow recovery keeps every raw route JSON byte-for-byte unchanged");
    }
    private static async Task Restart(string path,Action<bool,string> check)
    {
        var target=Target.Parse("10.2.0.1",0,ProbeProtocol.Icmp,500);var route=Route("committed-before-crash",target,"10.2.0.2");
        await using(var before=new History(path)){before.Initialize();before.SaveRoute(route);}
        string raw=Raw(path,route.Id);
        await using(var after=new History(path))
        {
            after.Initialize();using var http=new NoHttp();using var lookup=new Lookup((ip,token)=>Task.FromResult(Json(ip)));
            using var client=Client(after,lookup,http);
            await using var service=new RouteAnnotationService(after,client,targetKeys:()=>[target.Key]);
            await Until(()=>after.LoadRouteAnnotation(route.Id) is not null,"Startup failed to recover a committed raw route");
            check(lookup.Addresses.IsEmpty&&http.Calls==0&&Raw(path,route.Id)==raw,
                "startup repairs the raw-commit/event-delivery crash gap offline without changing evidence");
        }
        await using(var reopened=new History(path))
        {
            reopened.Initialize();check(reopened.LoadRouteAnnotation(route.Id) is not null,
                "a recovered startup annotation survives another process restart");
        }
    }
    private static async Task Generation(string path,Action<bool,string> check)
    {
        var target=Target.Parse("8.8.8.8",0,ProbeProtocol.Icmp,500);var route=Route("generation",target,"1.1.1.1");
        await using var history=new History(path);history.Initialize();history.SaveRoute(route);string raw=Raw(path,route.Id);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http=new NoHttp();using var lookup=new Lookup((ip,token)=>{entered.TrySetResult();return response.Task;});
        using var client=Client(history,lookup,http);
        await using var service=new RouteAnnotationService(history,client,targetKeys:()=>[],budgetMilliseconds:2000);
        service.Mode=2;int saved=0;service.Saved+=_=>Interlocked.Increment(ref saved);
        var request=service.Request(route,false);
        try
        {
            await entered.Task.WaitAsync(Deadline);int before=saved;
            service.Mode=0;response.TrySetResult(Json("1.1.1.1"));
            check(await request.WaitAsync(Deadline) is null&&saved==before,
                "generation cancellation suppresses late provider completion and Saved publication");
            check(history.LoadProviderMetadata("1.1.1.1","nexttrace") is null&&Raw(path,route.Id)==raw&&http.Calls==0,
                "a canceled provider generation cannot persist late metadata or modify raw probes");
        }
        finally{response.TrySetResult(Json("1.1.1.1"));}
    }
    private static async Task Fairness(string path,Action<bool,string> check)
    {
        var target=Target.Parse("8.8.4.4",0,ProbeProtocol.Icmp,500);
        var route=Route("fairness",target,"1.1.1.1","8.8.8.8","9.9.9.9");
        await using var history=new History(path);history.Initialize();history.SaveRoute(route);string raw=Raw(path,route.Id);
        using var http=new NoHttp();using var lookup=new Lookup(async(ip,token)=>
        {
            if(ip=="1.1.1.1")await Task.Delay(Timeout.Infinite,token);
            return Json(ip);
        });
        using var client=Client(history,lookup,http);bool recover=false;
        await using var service=new RouteAnnotationService(history,client,budgetMilliseconds:450,targetKeys:()=>recover?[target.Key]:[]);
        service.Mode=2;int slices=0;service.Refreshed+=()=>Interlocked.Increment(ref slices);recover=true;
        for(int pass=1;pass<=3;pass++)
        {
            service.RecoverRecent();int expected=pass;
            await Until(()=>Volatile.Read(ref slices)>=expected,"Recovery slice failed to finish");
        }
        check(lookup.Addresses.ToArray().SequenceEqual(new[]{"1.1.1.1","8.8.8.8","9.9.9.9"}),
            "successive bounded recovery slices reach later IPs after a slow first IP exhausts its route budget");
        var saved=history.LoadRouteAnnotation(route.Id);
        check(saved is not null&&saved.Nodes.Count(node=>node.Metadata is {Success:true})==2&&saved.PendingQueries==1&&
            history.MetadataCooldown("nexttrace")<=DateTimeOffset.UtcNow&&http.Calls==0,
            "partial enrichment persists completed IPs while a route-budget cancellation avoids provider cooldown");
        check(Raw(path,route.Id)==raw,"partial enrichment leaves raw measured addresses and timings unchanged");
        check(client.OrderNextTraceCandidates(new[]{"1.1.1.1","8.8.8.8","9.9.9.9"}).First()=="1.1.1.1",
            "least-recently-attempted ordering eventually returns to the slow IP after serving later nodes");
    }
    private static string Json(string ip)=>JsonSerializer.Serialize(new{ip,asnumber="13335",country="US",prov="CA",city="Los Angeles",owner="fixture"});
    private sealed class Lookup(Func<string,CancellationToken,Task<string>> lookup):INextTraceLookup
    {
        public ConcurrentQueue<string> Addresses {get;}=new();
        public Task<string> LookupAsync(string address,CancellationToken token){Addresses.Enqueue(address);return lookup(address,token);}
        public void Dispose(){}
    }
    private sealed class NoHttp:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Interlocked.Increment(ref Calls);throw new InvalidOperationException("Real HTTP is prohibited in scheduler fixtures");}
    }
}
