using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class FeatureChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var dir=Path.Combine(Path.GetTempPath(),"IpQuality-features-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var file=Path.Combine(dir,"history.db");var history=new History(file);history.Initialize();
        var target=Target.Parse("127.0.0.1",0,ProbeProtocol.Icmp,500);var now=DateTimeOffset.UtcNow;
        var minute=DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()/60000*60000);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file}.ToString()))
        {
            db.Open();using var cmd=db.CreateCommand();
            cmd.CommandText="""
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<=10081)
                INSERT INTO sample(target,time_ms,status,latency,elapsed,detail) SELECT $t,$now-x*60000,
                  CASE WHEN x%97=0 THEN 4 WHEN x%31=0 THEN 2 ELSE 0 END,
                  CASE WHEN x%97=0 OR x%31=0 THEN NULL ELSE 20+x%120 END,50,'fixture' FROM n;
                """;
            cmd.Parameters.AddWithValue("$t",target.Key);cmd.Parameters.AddWithValue("$now",minute.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();
        }
        history.Add(target,new(now,ProbeStatus.Success,900,900,"current"));
        history.Add(target,new(now.AddSeconds(20),ProbeStatus.Success,90000,90000,"future"));
        history.Add(Target.Parse("127.0.0.2",0,ProbeProtocol.Icmp,500),new(now,ProbeStatus.Success,99000,99000,"other target"));
        foreach(int days in new[]{1,7})
        {
            var timeline=history.LoadTimeline(target,now,days);
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file}.ToString());db.Open();using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT COUNT(CASE WHEN status<>4 THEN 1 END),AVG(CASE WHEN status=0 THEN latency END),MAX(CASE WHEN status=0 THEN latency END) FROM sample WHERE target=$t AND time_ms >= $from AND time_ms <= $until";
            cmd.Parameters.AddWithValue("$t",target.Key);cmd.Parameters.AddWithValue("$from",timeline.From.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$until",now.ToUnixTimeMilliseconds());using var r=cmd.ExecuteReader();r.Read();
            check(timeline.Total.Attempts==r.GetInt64(0)&&Math.Abs(timeline.Total.Average!.Value-r.GetDouble(1))<.0001&&timeline.Total.Maximum==900,$"{days}-day timeline agrees with raw samples and excludes future/other-target data");
            check(timeline.Points.Sum(b=>b.Attempts)==timeline.Hours.Sum(b=>b.Attempts)&&timeline.Points.Sum(b=>b.Attempts)==timeline.Total.Attempts&&timeline.Points.Sum(b=>b.LocalErrors)==timeline.Hours.Sum(b=>b.LocalErrors),$"{days}-day chart and hour grid share exact counts and boundaries");
            check(timeline.Points.Count==(days==1?289:337)&&timeline.StepMinutes==(days==1?5:30),$"{days}-day timeline uses bounded display resolution");
        }
        var zone=TimeZoneInfo.CreateCustomTimeZone("QuarterHour",TimeSpan.FromMinutes(345),"QuarterHour","QuarterHour");
        var shifted=history.LoadTimeline(target,now,7,zone);
        check(shifted.Hours.All(b=>TimeZoneInfo.ConvertTime(b.Start,zone).Minute==0),"hour grid aligns to local clock hours including fractional UTC offsets");
        var empty=history.LoadTimeline(Target.Parse("127.0.0.3",0,ProbeProtocol.Icmp,500),now,1);
        check(empty.Points.All(b=>b.Average is null&&b.Attempts==0)&&empty.Hours.All(b=>b.Average is null),"unobserved periods remain empty rather than zero latency");
        check(BackboneCatalog.Name(4837)=="联通 4837"&&BackboneCatalog.Name(9929)=="联通 9929"&&BackboneCatalog.Name(4809)=="电信 CN2","embedded catalog maps requested network names");
        check(BackboneCatalog.Name(9808)!="移动 CMI"&&!BackboneCatalog.Name(23764)!.Contains("GIA")&&BackboneCatalog.Name(13335) is null,"catalog avoids unsupported CMI/GIA and unknown-ASN labels");
        NodeMetadata Meta(string ip,long asn)=>new(ip,true,asn,"fixture","fixture","fixture","","",now,now.AddDays(7),"");
        var metadata=new Dictionary<string,NodeMetadata>{{"1.1.1.1",Meta("1.1.1.1",4809)},{"8.8.8.8",Meta("8.8.8.8",4837)}};
        var route=new RouteRun("classification",target.Key,target.Address,"test",now,now,"fixture","partial",false,500,32,
            [new(1,1,"1.1.1.1",11013,20),new(1,2,"8.8.8.8",11013,20),new(2,1,null,11010,null),new(3,1,"1.1.1.1",11013,20)]);
        var annotation=RouteClassifier.Classify(route,metadata,now,"fixture");
        check(annotation.Summary.Contains("CN2")&&annotation.Summary.Contains("4837")&&annotation.Sequence.Contains("{")&&annotation.Sequence.Contains("?")&&annotation.UnknownHops==1,"multi-response TTLs and unknown hops preserve ambiguity in route summary");
        check(!annotation.Summary.Contains("GIA")&&!annotation.Summary.Contains("GT"),"CN2 observations do not certify a GIA/GT product");
        var endpoint=route with{Address="1.1.1.1",Probes=new HopProbe[]{new(1,1,"1.1.1.1",0,10)}};
        var endpointAnnotation=RouteClassifier.Classify(endpoint,metadata,now,"fixture");
        check(endpointAnnotation.Summary.Contains("目标归属：电信 CN2")&&!endpointAnnotation.Summary.StartsWith("可见中间节点："),"target ASN alone is not presented as transit-network evidence");
        history.SaveRoute(route);history.SaveRouteAnnotation(annotation);
        history.SaveNodeMetadata(Meta("1.1.1.1",9929));
        check(history.LoadRouteAnnotation(route.Id)!.Nodes[0].Metadata!.Asn==4809,"new cache values do not rewrite historical annotation evidence");
        var revision=RouteClassifier.Classify(route,new Dictionary<string,NodeMetadata>{{"1.1.1.1",Meta("1.1.1.1",9929)}},now.AddSeconds(1),"查看时补全");
        history.SaveRouteAnnotation(revision);
        check(history.LoadRouteAnnotation(route.Id,true)!.Id==annotation.Id&&history.LoadRouteAnnotation(route.Id)!.Id==revision.Id,"original and supplementary route interpretations remain independently retrievable");
        var handler=new CounterHandler();using var client=new NodeMetadataClient(history,handler);
        await using(var service=new RouteAnnotationService(history,client))
        {
            var first=await service.Request(route,false);
            check(first is not null&&handler.Calls==0,"automatic offline annotations never perform HTTP");
            service.Mode=1;await service.Request(route,false);
            check(handler.Calls==0,"view-only setting never queries in background");
            service.Mode=2;var a=service.Request(route,false);var b=service.Request(route,false);await Task.WhenAll(a,b);
            check(handler.Calls==1&&a.Result!.Nodes.Any(n=>n.Metadata?.Asn==4837),"background enrichment shares a client, cache and duplicate jobs");
        }
        var zipPath=Path.Combine(dir,"snapshot.zip");history.ExportBundle(target,zipPath,DateTimeOffset.UtcNow.AddSeconds(3));
        using(var zip=ZipFile.OpenRead(zipPath))using(var doc=JsonDocument.Parse(zip.GetEntry("route-annotations.json")!.Open()))
            check(doc.RootElement.GetArrayLength()>=2&&doc.RootElement[0].GetProperty("RuleVersion").GetString()==BackboneCatalog.Version,"diagnostic export preserves annotation revisions and rule provenance");
        history.Prune(now.AddDays(33));check(history.LoadRouteAnnotation(route.Id) is null,"annotation retention follows its original route");
        var stopping=new History(Path.Combine(dir,"stopping.db"));stopping.Initialize();stopping.SaveRoute(route);
        var next=route with{Id="queued-after-stop"};stopping.SaveRoute(next);
        var slow=new SlowHandler();using var slowClient=new NodeMetadataClient(stopping,slow);
        await using(var service=new RouteAnnotationService(stopping,slowClient))
        {
            service.Mode=2;var active=service.Request(route,false);await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued=service.Request(next,false);service.CancelRequests();
            await Task.WhenAll(active,queued).WaitAsync(TimeSpan.FromSeconds(5));
            check(slow.Calls==1,"stop cancels active enrichment and queued jobs cannot restart online queries");
        }
        var migrationPath=Path.Combine(dir,"version3.db");var migration=new History(migrationPath);migration.Initialize();migration.SaveRoute(route);migration.Add(target,new(now,ProbeStatus.Success,12,12,"pre-upgrade"));
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=migrationPath}.ToString()))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE route_annotation; DROP TABLE geo_observation; DROP TABLE geo_request; DROP TABLE node_calibration; DROP TABLE calibration_audit; DROP TABLE nexttrace_import; PRAGMA user_version=3";cmd.ExecuteNonQuery();
        }
        migration.Initialize();migration.Initialize();
        check(migration.Load(target,now).Day.Attempts==1&&migration.LoadRoute(target,route.Id) is not null&&migration.LoadRouteAnnotation(route.Id) is null,"v3 upgrade preserves samples and routes and remains repeatable");
    }
    private sealed class CounterHandler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"ip":"8.8.8.8","success":true,"connection":{"asn":4837}}""")});
        }
    }
    private sealed class SlowHandler:HttpMessageHandler
    {
        public int Calls;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;Started.TrySetResult();await Task.Delay(10000,token);return new(HttpStatusCode.OK);
        }
    }
}
