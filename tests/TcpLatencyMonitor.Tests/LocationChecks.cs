using System.Net;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class LocationChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;const string first="59.43.250.50",second="59.43.246.178";
        NodeMetadata Meta(string ip,long? asn=null,string city="北京",string region="北京市",string country="中国")=>new(ip,true,asn,"fixture","fixture",country,region,city,now,now.AddDays(7),"");
        RouteRun Route(params HopProbe[] probes)=>new(Guid.NewGuid().ToString("N"),"fixture","8.8.4.4","fixture",now,now,"fixture","partial",false,500,32,probes);
        RouteAnnotation Classify(RouteRun run,params NodeMetadata[] data)=>RouteClassifier.Classify(run,data.ToDictionary(m=>m.Address),now,"fixture");
        var prefix=BackboneCatalog.Prefix(first)!;
        check(prefix.Cidr=="59.43.0.0/16"&&prefix.RegistryName=="CN2-BB"&&prefix.Source.StartsWith("https://rdap.apnic.net/"),"CN2 prefix records its registry source and range");
        check(BackboneCatalog.Prefix(second)==prefix&&BackboneCatalog.Prefix("59.43.0.0")==prefix&&BackboneCatalog.Prefix("59.43.255.255")==prefix&&BackboneCatalog.Prefix("59.42.255.255") is null&&BackboneCatalog.Prefix("59.44.0.0") is null,"CN2 CIDR includes both supplied IPs and respects boundaries");
        check(BackboneCatalog.Prefix("::ffff:59.43.250.50")==prefix&&BackboneCatalog.Prefix("2001:db8::1") is null,"mapped IPv4 matches while IPv6 cannot match an IPv4 prefix");
        var narrow=prefix with{Id="narrow",Cidr="59.43.250.0/24",Name="fixture-specific"};
        check(new PrefixCatalog(new[]{prefix,narrow}).Match(first)==narrow,"longest prefix wins independently of file order");
        bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        check(Reject(()=>CidrBlock.Parse("59.43.1.1/16"))&&Reject(()=>CidrBlock.Parse("59.43.0.0/33"))&&Reject(()=>new PrefixCatalog(new[]{prefix,prefix with{Id="duplicate"}})),"invalid masks, host bits and duplicate prefix evidence are rejected");
        var ipv6=CidrBlock.Parse("2001:db8:1230::/44");
        check(ipv6.Contains("2001:db8:123f:ffff::1")&&!ipv6.Contains("2001:db8:1240::1")&&!ipv6.Contains(first),"IPv6 CIDR handles partial-byte masks and address families");

        var run=Route(new(1,1,first,11013,12),new(2,1,second,11013,161));
        var offline=Classify(run);
        check(offline.Nodes.All(n=>n.Name.Contains("CN2")&&n.Metadata is null&&n.Identity!.Kind=="prefix")&&offline.MissingMetadata==0&&offline.MissingAsn==2,"no metadata still yields CN2 without inventing ASN");
        var cached=Classify(run,Meta(first),Meta(second));
        check(cached.Nodes.All(n=>n.Metadata!.Asn is null)&&cached.Summary.Contains("CN2（地址段归属）")&&cached.PendingQueries==0,"valid cache with missing ASN supports overview and does not need a repeated query");
        check(cached.LocationSequence.Contains("北京")&&cached.LocatedNodes==2&&cached.CityNodes==2&&cached.LocationSequence.Split(" → ").Length==1,"adjacent equal city/network nodes collapse but retain node evidence");
        var changedCity=Classify(run,Meta(first),Meta(second,null,"上海","上海市"));
        check(changedCity.LocationSequence.Split(" → ").Length==2&&changedCity.LocationSequence.Contains("上海"),"city changes remain visible inside the same network");
        var changedNetwork=Classify(Route(new(1,1,"1.1.1.1",11013,1),new(2,1,"8.8.8.8",11013,2)),Meta("1.1.1.1",4837),Meta("8.8.8.8",4809));
        check(changedNetwork.LocationSequence.Split(" → ").Length==2&&changedNetwork.LocationSequence.Contains("4837"),"network changes remain visible inside the same city");
        var repeated=Classify(Route(new(1,1,first,11013,1),new(2,1,second,11013,2),new(3,1,first,11013,3)),Meta(first),Meta(second,null,"上海","上海市"));
        check(repeated.LocationSequence.Split(" → ").Length==3,"returning to a previous city is not globally deduplicated");
        var ambiguous=Classify(Route(new(1,1,first,11013,1),new(1,2,second,11013,2)),Meta(first),Meta(second,null,"上海","上海市"));
        check(ambiguous.LocationSequence.StartsWith("{")&&ambiguous.LocationSequence.Contains(" / ")&&!ambiguous.LocationSequence.Contains(" → "),"multiple cities at one TTL remain alternatives");
        var samePlace=Classify(Route(new(1,1,first,11013,1),new(1,2,second,11013,2)),Meta(first),Meta(second));
        check(samePlace.LocationSequence.Contains("2 个响应 IP"),"same-label multiple responses retain their ambiguity");
        var missing=Classify(Route(new(1,1,first,11013,1),new(2,1,null,11010,null),new(3,1,null,11010,null),new(4,1,second,11013,2)),Meta(first),Meta(second));
        check(missing.LocationSequence.Contains("［2 跳未回应］")&&missing.LocationSequence.Split(" → ").Length==3,"unresponsive TTLs retain their position and break city merging");
        var unrecorded=Classify(Route(new(1,1,first,11013,1),new(3,1,second,11013,2)),Meta(first),Meta(second));
        check(unrecorded.LocationSequence.Contains("［1 跳无记录］")&&unrecorded.UnknownHops==0,"unrecorded TTLs remain distinct from probes with no reply");
        check(offline.LocationSequence.Split(" → ").Length==2&&offline.LocationSequence.Contains("地区未知"),"missing location is not inherited or merged through");
        var noAsn=Classify(Route(new HopProbe(1,1,"1.1.1.1",11013,1)),Meta("1.1.1.1"));
        check(noAsn.LocationSequence.Contains("北京")&&noAsn.LocationSequence.Contains("线路未识别"),"city display is independent of ASN and network recognition");
        check(NodeClassifier.Locate(Meta(first,null,"","广东省"),now).Label=="广东省（省区）"&&NodeClassifier.Locate(Meta(first,null,"","","日本"),now).Label=="日本（国家）","province and country fallbacks explicitly retain their granularity");
        var homonyms=Classify(run,Meta(first,null,"同名城","甲省","甲国"),Meta(second,null,"同名城","乙省","乙国"));
        check(homonyms.LocationSequence.Contains("甲国")&&homonyms.LocationSequence.Contains("乙国")&&homonyms.LocationSequence.Split(" → ").Length==2,"homonymous cities in different regions are disambiguated");
        var stale=Classify(run,Meta(first) with{Expires=now.AddMinutes(-1)});
        check(stale.LocationSequence.Contains("旧缓存")&&stale.PendingQueries==2&&stale.Nodes[0].Metadata!.Expires<now,"expired location is marked without modifying original query timestamps");
        var conflict=Classify(run,Meta(first,4837));
        check(conflict.Nodes[0].Name=="归属待核对"&&conflict.Nodes[0].Metadata!.Asn==4837&&conflict.Conflicts==1&&conflict.Summary.Contains("待核对"),"conflicting known ASN and prefix evidence stays explicit and unchanged");
        var onlyConflict=Classify(Route(new HopProbe(1,1,first,11013,1)),Meta(first,4837));
        check(!onlyConflict.Summary.StartsWith("可见中间节点："),"conflict alone cannot assert a confirmed transit backbone");
        var agreed=Classify(run,Meta(first,4809));
        check(agreed.Nodes[0].Identity!.Kind=="asn+prefix"&&agreed.Nodes[0].Metadata!.Asn==4809,"matching ASN and prefix retain both independent evidence sources");
        var unfamiliar=Classify(run,Meta(first,64500));
        check(unfamiliar.Nodes[0].Name.Contains("AS64500")&&unfamiliar.Nodes[0].Name.Contains("CN2")&&!unfamiliar.Nodes[0].Identity!.Conflict,"unknown catalog ASN is retained alongside registry attribution");
        var endpoint=Classify(run with{Address=first,Probes=new HopProbe[]{new(1,1,first,0,12)}},Meta(first));
        check(endpoint.Summary.Contains("目标归属")&&!endpoint.Summary.StartsWith("可见中间节点：")&&endpoint.LocationSequence.Contains("（目标）"),"prefix-only endpoint is separate from transit evidence");
        var notReached=Classify(run with{Address="8.8.4.4"},Meta(first),Meta(second),Meta("8.8.4.4",15169,"东京","东京","日本"));
        check(!notReached.LocationSequence.Contains("东京")&&!notReached.LocationSequence.Contains("（目标）"),"unobserved destination geography is not appended to the measured sequence");
        check(!cached.Summary.Contains("GIA")&&!cached.Summary.Contains("GT"),"registry attribution never certifies GIA or GT");

        var dir=Path.Combine(Path.GetTempPath(),"IpQuality-locations-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var history=new History(Path.Combine(dir,"history.db"));history.Initialize();history.SaveRoute(run);
        var oldJson=JsonNode.Parse(JsonSerializer.Serialize(cached))!.AsObject();
        foreach(var key in new[]{"InterpretationVersion","EvidenceKey","LocationSequence","PublicNodes","LocatedNodes","CityNodes","MissingAsn","Conflicts","PendingQueries"})oldJson.Remove(key);
        oldJson["RuleVersion"]="2026-09-09.1";oldJson["Summary"]="legacy unknown";
        foreach(var node in oldJson["Nodes"]!.AsArray())node!.AsObject().Remove("Identity");
        using(var db=new SqliteConnection("Data Source="+Path.Combine(dir,"history.db")))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO route_annotation VALUES($id,$route,$time,$json)";
            cmd.Parameters.AddWithValue("$id",cached.Id);cmd.Parameters.AddWithValue("$route",run.Id);cmd.Parameters.AddWithValue("$time",now.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$json",oldJson.ToJsonString());cmd.ExecuteNonQuery();
        }
        history.SaveNodeMetadata(Meta(first,9929,"另一个城市"));
        var upgraded=history.CurrentRouteAnnotation(run,now.AddSeconds(1));
        check(upgraded.Summary.Contains("CN2")&&upgraded.LocationSequence.Contains("北京")&&!upgraded.LocationSequence.Contains("另一个城市")&&upgraded.Origin.Contains("重新解释"),"2.1 JSON is reinterpreted offline using frozen historical geography");
        check(history.LoadRouteAnnotation(run.Id,true)!.Summary=="legacy unknown"&&history.LoadRouteAnnotation(run.Id,true)!.IdentityAbsent(),"first annotation remains readable with absent new fields");
        check(history.CurrentRouteAnnotation(run,now.AddSeconds(2)).Id==upgraded.Id&&history.SaveRouteAnnotation(Classify(run,Meta(first),Meta(second))).Id==upgraded.Id,"reopening or recomputing identical evidence does not create another revision");
        var newCity=Classify(run,Meta(first,null,"上海","上海市"),Meta(second)) with{Time=now.AddSeconds(3)};history.SaveRouteAnnotation(newCity);
        check(history.LoadRouteAnnotation(run.Id)!.Id==newCity.Id&&history.LoadRouteAnnotation(run.Id,true)!.Id==cached.Id,"changed city evidence creates a revision while retaining the original");
        var profile=Target.Parse("8.8.4.4",0,ProbeProtocol.Icmp,500);var exportRun=run with{Id="export-location",TargetKey=profile.Key};history.SaveRoute(exportRun);
        history.SaveRouteAnnotation(Classify(exportRun,Meta(first),Meta(second)));
        var exportZip=Path.Combine(dir,"matching-evidence.zip");history.ExportBundle(profile,exportZip,now.AddSeconds(10));
        using(var archive=ZipFile.OpenRead(exportZip))using(var doc=JsonDocument.Parse(archive.GetEntry("route-annotations.json")!.Open()))
            check(doc.RootElement[0].GetProperty("Nodes")[0].GetProperty("Identity").GetProperty("Prefix").GetProperty("RegistryName").GetString()=="CN2-BB"&&doc.RootElement[0].GetProperty("LocationSequence").GetString()!.Contains("北京"),"diagnostic export includes prefix provenance and frozen city sequence");
        var handler=new WaitingHandler();using var client=new NodeMetadataClient(history,handler);
        var live=run with{Id="first-local"};history.SaveRoute(live);
        await using(var service=new RouteAnnotationService(history,client))
        {
            service.Mode=0;var local=await service.Request(live,false);
            check(handler.Calls==0&&local!.Summary.Contains("CN2"),"offline service classifies prefix routes without HTTP");
            var fresh=Route(new HopProbe(1,1,second,11013,161));history.SaveRoute(fresh);
            service.Mode=2;var work=service.Request(fresh,false);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(history.LoadRouteAnnotation(fresh.Id)!.Summary.Contains("CN2")&&!work.IsCompleted,"local prefix result is saved before a slow lookup completes");
            service.CancelRequests();await work.WaitAsync(TimeSpan.FromSeconds(5));
            check(handler.Calls==1,"cancellation ends enrichment without starting another lookup");
        }
    }
    private static bool IdentityAbsent(this RouteAnnotation annotation)=>annotation.Nodes.All(n=>n.Identity is null);
    private sealed class WaitingHandler:HttpMessageHandler
    {
        public int Calls;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Calls++;Started.TrySetResult();await Task.Delay(10000,token);return new(HttpStatusCode.OK);}
    }
}
