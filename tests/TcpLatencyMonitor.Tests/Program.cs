using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TcpLatencyMonitor.Core;
Console.OutputEncoding=System.Text.Encoding.UTF8;
if(args.Length>0&&args[0]=="--route-history")
{
    int checks=0;await RouteHistoryChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} ROUTE HISTORY CHECKS PASSED.");return;
}

if(args.Length>0&&args[0]=="--nexttrace")
{
    int checks=0;await NextTraceChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} NEXTTRACE CHECKS PASSED.");return;
}
if(args.Length==2&&args[0]=="--nexttrace-live"){await NextTraceChecks.Live(args[1]);return;}
if(args.Length==1&&args[0]=="--nexttrace-v4-live"){await NextTraceChecks.LiveV4();return;}

if(args.Length==2&&args[0]=="--annotation-dbcheck")
{
    // Caller supplies an isolated SQLite backup; reinterpretation writes only to that copy.
    string path=Path.GetFullPath(args[1]);
    using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=path}.ToString());db.Open();
    var ips=new List<string>();var runs=new List<RouteRun>();var snapshots=new Dictionary<string,string>();
    using(var cmd=db.CreateCommand())
    {
        cmd.CommandText="SELECT address FROM node_metadata";using(var r=cmd.ExecuteReader())while(r.Read())ips.Add(r.GetString(0));
        cmd.CommandText="SELECT json FROM route_run";using(var r=cmd.ExecuteReader())while(r.Read())runs.Add(JsonSerializer.Deserialize<RouteRun>(r.GetString(0))!);
        cmd.CommandText="SELECT id,json FROM route_annotation";using(var r=cmd.ExecuteReader())while(r.Read())snapshots.Add(r.GetString(0),r.GetString(1));
    }
    await using var legacyHistory=new History(path);legacyHistory.Initialize();var legacyNow=DateTimeOffset.UtcNow;
    foreach(var ip in ips){var m=legacyHistory.LoadNodeMetadata(ip)!;_ = NodeClassifier.Locate(m,legacyNow);_ = m.Description;legacyHistory.RefreshCombinedMetadata(ip);}
    foreach(var run in runs)_ = legacyHistory.CurrentRouteAnnotation(run,legacyNow);
    using(var cmd=db.CreateCommand())
    {
        cmd.CommandText="SELECT json FROM route_annotation WHERE id=$id";var id=cmd.Parameters.Add("$id",Microsoft.Data.Sqlite.SqliteType.Text);
        foreach(var pair in snapshots){id.Value=pair.Key;if(cmd.ExecuteScalar() as string!=pair.Value)throw new InvalidOperationException("Original annotation changed");}
    }
    Console.WriteLine($"PASS isolated legacy database: {ips.Count} metadata records and {runs.Count} routes read/reinterpreted; all {snapshots.Count} original annotations retained byte-for-byte; no network calls.");return;
}
if(args.Length>1&&args[0]=="--multi-benchmark"){await MultiBenchmark.Run(Path.GetFullPath(args[1]));return;}
if(args.Length>0&&args[0]=="--geo")
{
    int checks=0;await GeoEvidenceChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} GEO CHECKS PASSED.");return;
}

if(args.Length>0&&args[0]=="--multi")
{
    int checks=0;await MultiTargetChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} MULTI-TARGET CHECKS PASSED.");return;
}

if(args.Length>0&&args[0]=="--routes")
{
    int checks=0;await RouteProbeChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} ROUTE CHECKS PASSED.");return;
}

if(args.Length>0&&args[0]=="--table")
{
    int checks=0;RouteTableChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} TABLE CHECKS PASSED.");return;
}

if(args.Length>0&&args[0]=="--locations")
{
    int checks=0;await LocationChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} LOCATION CHECKS PASSED.");return;
}

if(args.Length==1&&args[0]=="--metadata-checks")
{
    int checks=0;await MetadataChecks.Run((condition,label)=>{if(!condition)throw new InvalidOperationException("FAIL: "+label);checks++;Console.WriteLine("PASS: "+label);});
    Console.WriteLine($"ALL {checks} OFFLINE METADATA CHECKS PASSED.");return;
}

if(args.Length>0&&args[0]=="--metadata")
{
    var dir=Path.GetFullPath(args[2]);Directory.CreateDirectory(dir);var lookupHistory=new History(Path.Combine(dir,"history.db"));lookupHistory.Initialize();
    using var client=new NodeMetadataClient(lookupHistory);var result=await client.GetAsync(args[1],true,CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result));return;
}

if(args.Length>0&&args[0]=="--native")
{
    var ip=IPAddress.Parse(args.Length>1?args[1]:"127.0.0.1");
    Console.WriteLine(NetworkContext.Read(ip.ToString()));
    var endpoint=Target.Parse(ip.ToString(),0,ProbeProtocol.Icmp,1000);
    Console.WriteLine(await new IcmpProbe().RunAsync(endpoint,TimeSpan.FromSeconds(1),CancellationToken.None));
    var run=await new RouteProbe(new WindowsHopProbe()).RunAsync(endpoint,"native-test","test",new RouteOptions(5,3,1000,15),CancellationToken.None);
    foreach(var p in run.Probes)Console.WriteLine(p);Console.WriteLine(run.Outcome);return;
}
if(args.Length>0&&args[0]=="--soak")
{
    var dir=Path.GetFullPath(args[1]);Directory.CreateDirectory(dir);var h=new History(Path.Combine(dir,"history.db"));h.Initialize();
    var t=Target.Parse("127.0.0.1",0,ProbeProtocol.Icmp,1000);var monitor=new MonitorCoordinator(h,t,new MonitorOptions(1,1000,1,true,new RouteOptions(5,3,300,5)));
    int n=0;monitor.SampleSaved+=s=>{n++;if(n%60==0){var p=System.Diagnostics.Process.GetCurrentProcess();Console.WriteLine($"{DateTimeOffset.UtcNow:O} samples={n} workingSet={p.WorkingSet64} handles={p.HandleCount}");}};
    using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(args[2])));await monitor.RunAsync(cts.Token);
    Console.WriteLine($"SOAK COMPLETE samples={n} routes={h.LoadRoutes(t).Count} events={h.LoadEvents(t).Count}");return;
}

if(args.Length>0 && args[0] is "--seed" or "--seed-week")
{
    var directory=Path.GetFullPath(args[1]);Directory.CreateDirectory(directory);
    var fixtureTarget=Target.Parse("127.0.0.1",int.Parse(args[2]),args.Length>3&&args[3]=="tcp"?ProbeProtocol.Tcp:ProbeProtocol.Icmp,500);var db=new History(Path.Combine(directory,"history.db"));db.Initialize();
    var fixtureNow=DateTimeOffset.UtcNow;var rng=new Random(8);
    for(int i=0;i<(args[0]=="--seed-week"?7:1)*24*60;i++)
    {
        if(i is >30 and <40)continue;
        var time=fixtureNow.AddMinutes(-i);bool peak=time.ToLocalTime().Hour is >=19 and <=22;
        var status=i%97<3||peak&&i%17<3?ProbeStatus.Timeout:ProbeStatus.Success;
        var latency=35+Math.Sin(i*.12)*9+rng.NextDouble()*10+(peak?50+Math.Sin(i*.04)*30:0);
        db.Add(fixtureTarget,new Sample(fixtureNow.AddMinutes(-i),status,status==ProbeStatus.Success?latency:null,status==ProbeStatus.Success?latency:3000,"UI 测试样本"));
    }
    File.WriteAllText(Path.Combine(directory,"settings.json"),JsonSerializer.Serialize(new {Version=2,Mode=fixtureTarget.Protocol==ProbeProtocol.Icmp?"Icmp":"Tcp",Address=fixtureTarget.Address,Port=fixtureTarget.Port==0?443:fixtureTarget.Port,IntervalSeconds=1,TimeoutMilliseconds=500,ResumeOnLaunch=false,EnableRoutes=true,RouteMinutes=10}));
    Console.WriteLine("Seeded isolated UI fixture: "+directory);return;
}
if(args.Length>0 && args[0]=="--server")
{
    var server=new TcpListener(IPAddress.Loopback,0);server.Start();Console.WriteLine("PORT="+((IPEndPoint)server.LocalEndpoint).Port);
    while(true){using var client=await server.AcceptTcpClientAsync();}
}

int passed=0;
void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
void Invalid(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new Exception("Expected invalid: "+name);}
var target=Target.Parse("127.0.0.1",443);
Check(Target.Parse("2001:0db8::1",443).Address=="2001:db8::1","canonical IPv6");
Check(Target.Parse("::ffff:127.0.0.1",443).Key==target.Key,"IPv4-mapped identity");
Invalid(()=>Target.Parse("https://example.com",443),"reject URL");
Invalid(()=>Target.Parse("127.1",443),"reject shorthand IPv4");
Invalid(()=>Target.Parse("127.0.0.1",double.NaN),"reject invalid number");
Invalid(()=>Target.Parse("127.0.0.1",443.5),"reject fractional port");
Invalid(()=>Target.Parse("0.0.0.0",443),"reject unspecified address");
Invalid(()=>Target.Parse("::ffff:0.0.0.0",443),"reject IPv4-mapped unspecified address");
Check(TcpProbe.MapError(SocketError.ConnectionRefused)==ProbeStatus.Refused,"classify refusal");
Check(TcpProbe.MapError(SocketError.TimedOut)==ProbeStatus.Timeout,"classify timeout");
Check(TcpProbe.MapError(SocketError.AccessDenied)==ProbeStatus.LocalError,"classify local permission error");
Check(TcpProbe.MapError(SocketError.ConnectionReset)==ProbeStatus.Failed,"connection reset counts as attempt");

var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
var port=((IPEndPoint)listener.LocalEndpoint).Port;var loopTarget=Target.Parse("127.0.0.1",port);
var probe=new TcpProbe();
var accepting=listener.AcceptSocketAsync();
var success=await probe.RunAsync(loopTarget,TimeSpan.FromSeconds(2),CancellationToken.None);
using(var socket=await accepting){Check(success.Status==ProbeStatus.Success&&success.LatencyMs>=0,"real loopback TCP success");}
listener.Stop();
var refusal=await probe.RunAsync(loopTarget,TimeSpan.FromSeconds(5),CancellationToken.None);
Console.WriteLine($"Closed loopback observation: {refusal.Status} / {refusal.Detail}");
Check((refusal.Status==ProbeStatus.Refused||refusal.Status==ProbeStatus.Timeout)&&refusal.LatencyMs is null,"real closed port records refusal or filtered timeout without latency");
using(var cancel=new CancellationTokenSource())
{
    cancel.Cancel();bool canceled=false;
    try{await probe.RunAsync(loopTarget,TimeSpan.FromSeconds(2),cancel.Token);}catch(OperationCanceledException){canceled=true;}
    Check(canceled,"user cancellation does not create failed sample");
}
var fixture=Path.Combine(Path.GetTempPath(),"TcpLatencyMonitor-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var history=new History(Path.Combine(fixture,"history.db"));history.Initialize();
var now=new DateTimeOffset(2026,9,9,1,23,0,TimeSpan.Zero);
Sample S(int seconds,ProbeStatus state,double? ms)=>new(now.AddSeconds(seconds),state,ms,ms??3000,"test, \"quoted\"");
var empty=history.Load(target,now);
Check(empty.Day.Availability is null&&empty.P95Hour is null&&empty.Minutes.Count==60,"empty history has no fabricated zero");
history.Add(target,S(0,ProbeStatus.Success,100));history.Add(target,S(-20,ProbeStatus.Success,200));
history.Add(target,S(-30,ProbeStatus.Refused,null));history.Add(target,S(-40,ProbeStatus.LocalError,null));
history.Add(Target.Parse("127.0.0.1",444),S(-5,ProbeStatus.Success,999));
history.Add(Target.Parse("127.0.0.2",443),S(-5,ProbeStatus.Success,999));
history.Add(target,S(5,ProbeStatus.Success,999));
var d=history.Load(target,now);
Check(d.Minute.Attempts==3&&d.Minute.Successes==2&&d.Minute.LocalErrors==1,"exclude local errors, other targets and future samples");
Check(d.Minute.Average==150&&Math.Abs(d.Minute.Availability!.Value-66.666666)<.001,"failures do not dilute successful latency");
Check(d.P95Hour==200,"nearest-rank P95");
Check(d.Minutes[^1].Successes==1&&d.Minutes[^1].Average==100,"exact minute boundary includes newest sample");
Check(d.Recent.Count==4&&d.Recent[0].Time==now,"history newest first");
Check(new History(Path.Combine(fixture,"history.db")).Load(target,now).Day.Attempts==3,"history survives restart");
var csv=Path.Combine(fixture,"export.csv");var exported=history.Export(target,csv,now);var csvText=File.ReadAllText(csv);
Check(exported==4&&csvText.Contains("\"test, \"\"quoted\"\"\""),"CSV export preserves errors, nulls and quoting");
history.Add(target,new Sample(now.AddDays(-32),ProbeStatus.Success,500,500,"old"));history.Prune(now);
Check(history.Export(target,csv,now)==4,"retention cleanup preserves recent history");
using(var cancel=new CancellationTokenSource())
{
    var fake=new SlowProbe();int samples=0;
    try { await new MonitorLoop(fake).RunAsync(target,TimeSpan.FromMilliseconds(10),TimeSpan.FromSeconds(1),s=> {if(++samples==3)cancel.Cancel();return Task.CompletedTask;},cancel.Token); }
    catch(OperationCanceledException) { }
    Check(samples==3&&fake.MaximumConcurrent==1,"slow probes never overlap or produce catch-up burst");
}
using(var cancel=new CancellationTokenSource(50))
{
    int samples=0;
    try {await new MonitorLoop(new SlowProbe()).RunAsync(target,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2),s=> {samples++;return Task.CompletedTask;},cancel.Token);}
    catch(OperationCanceledException){}
    Check(samples==0,"stopping in-flight probe does not record failure");
}
using(var cancel=new CancellationTokenSource())
{
    var fake=new StaleProbe();int samples=0;
    try{await new MonitorLoop(fake).RunAsync(target,TimeSpan.FromMilliseconds(1),TimeSpan.FromSeconds(1),s=>{samples++;cancel.Cancel();return Task.CompletedTask;},cancel.Token);}
    catch(OperationCanceledException){}
    Check(samples==1&&fake.Calls==2,"stale in-flight result after suspension is not recorded");
}
await ChecksV2.Run(Check);
await RouteProbeChecks.Run(Check);
await MultiTargetChecks.Run(Check);
await MetadataChecks.Run(Check);
await FeatureChecks.Run(Check);
await LocationChecks.Run(Check);
await GeoEvidenceChecks.Run(Check);
await NextTraceChecks.Run(Check);
RouteTableChecks.Run(Check);
await RouteHistoryChecks.Run(Check);
Console.WriteLine($"ALL {passed} CHECKS PASSED. Test data: {fixture}");

sealed class SlowProbe:IProbe
{
    private int _active;
    public int MaximumConcurrent {get;private set;}
    public async Task<Sample> RunAsync(Target target,TimeSpan timeout,CancellationToken token)
    {
        MaximumConcurrent=Math.Max(MaximumConcurrent,Interlocked.Increment(ref _active));
        try {await Task.Delay(130,token);return new Sample(DateTimeOffset.UtcNow,ProbeStatus.Success,130,130,"fake");}
        finally{Interlocked.Decrement(ref _active);}
    }
}

sealed class StaleProbe:IProbe
{
    public int Calls{get;private set;}
    public Task<Sample> RunAsync(Target target,TimeSpan timeout,CancellationToken token)
    {
        Calls++;return Task.FromResult(Calls==1?new Sample(DateTimeOffset.UtcNow.AddMinutes(-10),ProbeStatus.Timeout,null,600000,"stale"):
            new Sample(DateTimeOffset.UtcNow,ProbeStatus.Success,1,1,"fresh"));
    }
}

