using System.Text.Json;
using IpQualityMonitor.Application;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class AnnotationRuntimeChecks
{
    private sealed class NeverLookup:INextTraceLookup
    {
        public int Calls;
        public Task<string> LookupAsync(string address,CancellationToken token){Calls++;throw new InvalidOperationException("Offline fixture cannot query providers");}
        public void Dispose(){}
    }
    private sealed class FakeProbe:IProbe
    {
        public int Calls;
        public Task<Sample> RunAsync(Target target,TimeSpan timeout,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);
            return Task.FromResult(new Sample(DateTimeOffset.UtcNow,ProbeStatus.Success,.2,.2,"local fixture"));
        }
    }
    private static RouteRun Route(string id,DateTimeOffset when)=>new(id,"fixture-target","1.1.1.1","fixture",when,when,"fixture","partial",false,1000,32,
        [new(1,1,"202.97.63.30",11013,1),new(2,1,"10.0.0.1",11013,2)]);
    public static async Task RunAsync(Action<bool,string> check,Action<Action,string> reject)
    {
        string root=Path.Combine(Path.GetTempPath(),"iqm-annotation-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var missing=new AnnotationEnvironment(Path.Combine(root,"missing-helper"),Path.Combine(root,"missing-token"));
            check(missing.ReadToken("offline")==""&&missing.ReadToken("v3")=="","offline/v3 never open or require the deployment token file");
            reject(()=>missing.ReadToken("v4"),"v4 missing token file fails closed");
            var unreadable=new AnnotationEnvironment(tokenFile:root);
            reject(()=>unreadable.ReadToken("v4"),"v4 unreadable token source fails closed");
            string secretPath=Path.Combine(root,"token.txt");const string secret="synthetic-nexttrace-token-no-live-use";
            File.WriteAllText(secretPath,secret+"\r\n");var environment=new AnnotationEnvironment(Path.Combine(root,"missing-helper"),secretPath);
            check(environment.ReadToken("v4")==secret,"v4 reads the deployment file and removes only its final newline");
            check(!JsonSerializer.Serialize(environment).Contains(secret,StringComparison.Ordinal)&&!JsonSerializer.Serialize(new AnnotationSettings("v4")).Contains("Token",StringComparison.OrdinalIgnoreCase),"deployment secrets and paths are absent from serializable configuration DTOs");
            File.WriteAllText(secretPath,"invalid\0token");reject(()=>environment.ReadToken("v4"),"v4 rejects control characters before configuring a provider");
            File.WriteAllText(secretPath,new string('x',4099));reject(()=>environment.ReadToken("v4"),"v4 rejects oversized token files");
            File.WriteAllText(secretPath,secret);

            string database=Path.Combine(root,"views.db");
            await using(var history=new History(database))
            {
                history.Initialize();var now=DateTimeOffset.UtcNow;
                var route=Route("snapshot-route",now.AddHours(-2));history.SaveRoute(route);
                var emptyRoute=Route("unannotated-route",now.AddHours(-1));history.SaveRoute(emptyRoute);
                var old=new NodeMetadata("202.97.63.30",true,4134,"Fixture ISP","Fixture org","US","California","Old city",now.AddHours(-50),now.AddHours(-26),""){ProviderId="nexttrace"};
                var first=RouteClassifier.Classify(route,new Dictionary<string,NodeMetadata>{{old.Address,old}},now.AddHours(-2),"fixture first");
                history.SaveRouteAnnotation(first);
                var newest=RouteClassifier.Classify(route,new Dictionary<string,NodeMetadata>{{old.Address,old with{City="Frozen city"}}},now.AddHours(-1),"fixture last");
                history.SaveRouteAnnotation(newest);
                history.SaveNodeMetadata(old with{City="New cache city",Queried=now,Expires=now.AddDays(1)});
                long Count()
                {
                    using var connection=new SqliteConnection("Data Source="+database);connection.Open();
                    using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM route_annotation";return (long)command.ExecuteScalar()!;
                }
                string RawJson()
                {
                    using var connection=new SqliteConnection("Data Source="+database);connection.Open();
                    using var command=connection.CreateCommand();command.CommandText="SELECT json FROM route_run WHERE id=$id";command.Parameters.AddWithValue("$id",route.Id);return (string)command.ExecuteScalar()!;
                }
                string rawBefore=RawJson();
                long count=Count();var view=AnnotationViews.Read(history,route,"v4");var repeated=AnnotationViews.Read(history,route,"offline");
                var missingView=AnnotationViews.Read(history,emptyRoute,"offline");
                check(Count()==count&&history.LoadRouteAnnotation(emptyRoute.Id) is null&&missingView.AnnotatedAt is null,"reading existing/missing annotation views does not create revisions or trigger work");
                var node=view.Nodes.Single(value=>value.Address==old.Address);
                check(view.MeasuredAt==route.Finished&&view.AnnotatedAt==newest.Time&&view.FirstAnnotatedAt==first.Time&&node.QueriedAt==old.Queried&&node.ExpiresAt==old.Expires,"annotation GET preserves raw, first/latest annotation, queried and expiry timestamps independently");
                check(view.State=="stale"&&node.State=="stale"&&node.Region.Contains("Frozen city",StringComparison.Ordinal)&&!node.Region.Contains("New cache city",StringComparison.Ordinal),"annotation views use frozen historical evidence and expose stale state instead of silently reinterpreting current cache");
                check(repeated.AnnotatedAt==view.AnnotatedAt&&RawJson()==rawBefore,"repeated offline reads leave raw route bytes and annotation time unchanged");
                var withAttempt=AnnotationViews.Read(history,route,"v4",address=>new("timeout",true,now.AddMinutes(-5),old.Queried,now.AddMinutes(1)));
                var attemptedNode=withAttempt.Nodes.Single(value=>value.Address==old.Address);
                check(attemptedNode.Stale&&attemptedNode.AttemptState=="timeout"&&attemptedNode.LastAttemptAt==now.AddMinutes(-5)&&attemptedNode.QueriedAt==old.Queried&&withAttempt.AnnotatedAt==newest.Time,"current query status is separate from frozen stale evidence and never overwrites observation times");
                check(node.Source=="nexttrace"&&node.Asn==4134&&node.Organization=="Fixture org"&&view.Nodes.Single(value=>value.Address=="10.0.0.1").State=="local","annotation DTO includes provider/ASN/organization and keeps private hops local");
                var offlineRoute=Route("offline-route",now);history.SaveRoute(offlineRoute);var lookup=new NeverLookup();
                using var client=new NodeMetadataClient(history,nextTrace:lookup);client.Configure(new("nexttrace"),new("offline"));
                await using var service=new RouteAnnotationService(history,client);
                var annotation=await service.Request(offlineRoute,false).WaitAsync(TimeSpan.FromSeconds(3));
                check(annotation is not null&&annotation.Nodes.Any(value=>value.Identity?.Network?.Contains("China",StringComparison.OrdinalIgnoreCase)==true||value.Name.Length>0)&&lookup.Calls==0,"offline annotation service still produces catalog/cache evidence without invoking a helper");
            }
            await Runtime(root,missing,environment,secret,check);
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
    private static async Task Runtime(string root,AnnotationEnvironment missing,AnnotationEnvironment valid,string secret,Action<bool,string> check)
    {
        using(var storage=new ServerStorage(Path.Combine(root,"mode-change"),"fixture"))
        {
            var probe=new FakeProbe();var resources=new MonitorResources(probes:_=>probe,contexts:_=>new("fixture","fixture"));
            await using var runtime=new MonitorRuntime(storage,resources,missing);await runtime.StartAsync();
            bool rejected=false;try{await runtime.SetAnnotationsAsync(new("v4"),runtime.Configuration.Revision);}catch(InvalidOperationException){rejected=true;}
            check(rejected&&runtime.Configuration.Revision==0&&runtime.Configuration.Annotations.Mode=="offline"&&runtime.AnnotationProvider?.Mode=="offline","missing v4 file rejects settings atomically without changing saved or active mode");
            await runtime.SetAnnotationsAsync(new("v3",12),runtime.Configuration.Revision);
            check(runtime.Configuration.Annotations.Mode=="v3"&&runtime.AnnotationProvider?.Mode=="v3","explicit v3 settings do not require any token file");
            await runtime.SetAnnotationsAsync(new("offline"),runtime.Configuration.Revision);
            check(runtime.Ready&&runtime.AnnotationProvider?.Mode=="offline","switching back offline keeps the raw monitor ready");
        }
        using(var storage=new ServerStorage(Path.Combine(root,"redaction"),"fixture"))
        {
            var resources=new MonitorResources(probes:_=>new FakeProbe(),contexts:_=>new("fixture","fixture"));
            await using var runtime=new MonitorRuntime(storage,resources,valid);await runtime.StartAsync();
            await runtime.SetAnnotationsAsync(new("v4"),runtime.Configuration.Revision);
            string serialized=JsonSerializer.Serialize(new{config=runtime.Configuration,annotationProvider=runtime.AnnotationProvider,annotationError=runtime.AnnotationError});
            check(!serialized.Contains(secret,StringComparison.Ordinal)&&!File.ReadAllText(storage.ConfigurationPath).Contains(secret,StringComparison.Ordinal),"v4 token never enters overview/config serialization or persisted settings");
            long revision=runtime.Configuration.Revision;string backup=storage.ConfigurationPath+".fixture-backup";
            File.Move(storage.ConfigurationPath,backup);Directory.CreateDirectory(storage.ConfigurationPath);
            bool saveRejected=false;
            try{await runtime.SetAnnotationsAsync(new("v3"),revision);}catch(IOException){saveRejected=true;}
            finally{Directory.Delete(storage.ConfigurationPath);File.Move(backup,storage.ConfigurationPath);}
            check(saveRejected&&runtime.Configuration.Revision==revision&&runtime.Configuration.Annotations.Mode=="v4"&&runtime.AnnotationProvider?.Mode=="v4","failed atomic settings commit restores the prior live mode and preserves saved intent");
        }
        using(var storage=new ServerStorage(Path.Combine(root,"startup-fail-closed"),"fixture"))
        {
            storage.Save(ServerConfiguration.Empty with{Annotations=new("v4")});var probe=new FakeProbe();
            var resources=new MonitorResources(probes:_=>probe,contexts:_=>new("fixture","fixture"));
            await using var runtime=new MonitorRuntime(storage,resources,missing);await runtime.StartAsync();
            check(runtime.Ready&&runtime.AnnotationError is not null&&runtime.AnnotationProvider?.Mode=="offline","restart with missing v4 secret disables only enrichment and keeps raw runtime ready");
            await runtime.SetPolicyAsync(new GlobalMonitorSettings(EnableRoutes:false),runtime.Configuration.Revision);
            string id=await runtime.AddAsync(new("fixture","127.0.0.1","Tcp",80),runtime.Configuration.Revision);
            await runtime.SetRunningAsync(id,true,runtime.Configuration.Revision);
            for(int count=0;count<100&&Volatile.Read(ref probe.Calls)==0;count++)await Task.Delay(10);
            check(probe.Calls>0&&runtime.States().Single().Running,"raw sampling remains functional when optional v4 credentials are unavailable");
            await runtime.SetRunningAsync(id,false,runtime.Configuration.Revision);
        }
    }
}
