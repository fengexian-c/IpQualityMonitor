using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class GeoEvidenceChecks
{
    public const string Fixture="""
        {"Hops":[[{"TTL":1,"Success":true,"Address":{"IP":"172.16.10.1","Zone":""},"RTT":220000,"Geo":null}],
        [{"TTL":2,"Success":false,"Address":null,"RTT":0}],
        [{"TTL":10,"Success":true,"Address":{"IP":"202.97.63.30"},"RTT":149600000,"Geo":{"asnumber":"4134","country":"美国","prov":"加利福尼亚州","city":"圣何塞","whois":"CHINANET-BB"}}],
        [{"TTL":15,"Success":true,"Address":"186.241.113.4","RTT":143620000,"Geo":{"asnumber":"402169","country":"美国","prov":"加利福尼亚州","city":"圣何塞","owner":"Uscloud Inc"}}]],"StopReason":{"reason":"destination_reached","hop":15}}
        """;
    public static async Task Run(Action<bool,string> check)
    {
        var now=DateTimeOffset.UtcNow;const string ip="202.97.63.30";
        NodeMetadata Meta(string source="ipwho.is",string region="California",string city="San Jose",string country="United States",long? asn=4134)=>
            new(ip,true,asn,"fixture","fixture",country,region,city,now,now.AddDays(7),""){ProviderId=source};
        NodeMetadata Combine(params NodeMetadata[] items)=>GeoResolver.Combine(ip,items,null,"ipwho.is",now)!;
        var san=Meta();var washington=Meta(city:"Washington",region:"District of Columbia");var info=Meta("ipinfo-core");
        var conflict=NodeClassifier.Locate(Combine(washington,info),now);
        check(conflict.Conflict&&conflict.Level=="country"&&conflict.Label=="美国（地区待核对）"&&conflict.Details.Contains("Washington")&&conflict.Details.Contains("San Jose"),"DC versus California degrades to common country with both sources preserved");
        var cityConflict=NodeClassifier.Locate(Combine(san,Meta("ipinfo-core",city:"Fremont")),now);
        check(cityConflict.Conflict&&cityConflict.Level=="region"&&cityConflict.Label.Contains("加州")&&cityConflict.Label.Contains("城市待核对"),"San Jose and Fremont remain distinct cities within common California");
        var agreed=NodeClassifier.Locate(Combine(san,Meta("ipinfo-core","加利福尼亚州","聖何西","美国")),now);
        check(!agreed.Conflict&&agreed.State=="consistent"&&agreed.Label=="圣何塞","English and Chinese aliases normalize to one city identity");
        check(NodeClassifier.Locate(Combine(san,Meta("ipinfo-core",city:"",region:"")),now).State=="estimated","country-only data does not count as a second city confirmation");
        check(NodeClassifier.Locate(Combine(san,Meta("ipinfo-core",city:"Washington",region:"")),now) is {Conflict:true,Level:"country"},"different city names remain a conflict even when one province field is missing");
        check(NodeClassifier.Locate(Meta(country:"CN",region:"GD",city:"Guangzhou"),now).Key==NodeClassifier.Locate(Meta(country:"中国",region:"广东省",city:"广州"),now).Key,"Chinese province codes and English city aliases normalize consistently");
        check(NodeClassifier.Locate(Combine(san,info with{Expires=now.AddSeconds(-1),City="Fremont"}),now).Conflict==false,"expired secondary data cannot overrule a fresh city");
        check(NodeClassifier.Locate(Combine(san with{Expires=now.AddSeconds(-1)}),now).Stale,"last successful expired location remains visibly stale");
        check(NodeClassifier.Locate(Combine(san,Meta("ipinfo-core",country:"Costa Rica")),now).Level=="unknown","different countries never merge same-named cities");
        var network=NodeClassifier.Classify(10,ip,false,Combine(washington,info),now);
        check(network.Name=="电信 163"&&!network.Identity!.Conflict&&network.Identity.Location.Conflict,"city conflict does not invalidate a consistent ASN");
        var asnConflict=NodeClassifier.Classify(10,ip,false,Combine(san,Meta("ipinfo-core",asn:4809)),now);
        check(asnConflict.Name=="归属待核对"&&asnConflict.Identity!.Conflict,"different current ASNs retain an explicit network conflict");
        var manual=new NodeCalibration(ip,"美国","加州","圣何塞","用户提供 NextTrace；待复核",now,now.AddDays(7));
        var corrected=GeoResolver.Combine(ip,new[]{washington,info},manual,"ipwho.is",now)!;
        check(NodeClassifier.Locate(corrected,now) is {State:"manual",Conflict:false,Label:"圣何塞"}&&corrected.Sources.Count==2,"manual city selection preserves conflicting source evidence");
        corrected=corrected with{Calibration=manual with{Expires=now.AddSeconds(-1)}};
        check(NodeClassifier.Locate(corrected,now) is {State:"conflict",Conflict:true},"expired calibration falls back to unresolved source disagreement");
        var parsed=NodeMetadataClient.ParseIpinfo(ip,"""{"ip":"202.97.63.30","geo":{"city":"San Jose","region":"California","country":"United States","country_code":"US","region_code":"CA"},"as":{"asn":"AS4134","name":"CHINANET"}}""",now);
        check(parsed.Source=="ipinfo-core"&&parsed.Asn==4134&&parsed.CountryCode=="US"&&parsed.City=="San Jose","IPinfo Core city schema and string ASN parsed independently");
        check(!NodeMetadataClient.ParseIpinfo(ip,"""{"ip":"202.97.63.30","asn":"AS4134","country":"United States"}""",now).Success,"IPinfo Lite cannot masquerade as the city provider");
        bool Reject(Action action){try{action();return false;}catch(Exception e) when(e is InvalidDataException or JsonException or ArgumentException){return true;}}
        check(Reject(()=>NodeMetadataClient.ParseIpinfo(ip,"""{"ip":"8.8.8.8","geo":{}}""",now)),"IPinfo rejects another IP's response");
        var import=NextTraceImporter.Parse(Fixture,"nexttrace.json",now);
        check(import.Hops.Count==4&&import.Hops[2].Ttl==10&&import.Hops[2].Address==ip&&import.Hops[2].RttMs==149.6,"NextTrace address objects and nanosecond RTT imported correctly");
        check(import.Hops[0].Metadata is null&&import.Hops[1].RttMs is null&&import.Candidates.Count==2,"private nodes and timeout probes remain outside city adoption");
        check(import.MeasuredAt is null&&import.StopReason=="destination_reached"&&import.Sha256.Length==64,"import does not invent measurement time and preserves provenance");
        check(Reject(()=>NextTraceImporter.Parse("{\"Hops\":[{}]}","bad.json",now))&&Reject(()=>NextTraceImporter.Parse("[]","bad.json",now)),"malformed and unsupported JSON shapes fail before persistence");
        check(Reject(()=>NextTraceImporter.Parse(new string('x',NextTraceImporter.MaxBytes+1),"large.json",now)),"import is bounded at two megabytes");
        check(Reject(()=>NextTraceImporter.Parse(Fixture.Replace("\"whois\":\"CHINANET-BB\"","\"ip\":\"8.8.8.8\""),"mismatch.json",now)),"import rejects a Geo IP inconsistent with the hop responder");
        string directory=Path.Combine(Path.GetTempPath(),"IpQuality-geo-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var histories=new List<History>();History New(string name){var h=new History(Path.Combine(directory,name+".db"));h.Initialize();histories.Add(h);return h;}
        try
        {
            var legacy=New("legacy-json");
            string legacyJson="""{"Address":"202.97.63.30","Success":true,"Asn":4134,"Isp":"CHINANET","Organization":"CHINANET","Country":"United States","Region":"California","City":"San Jose","Queried":"2026-09-09T00:00:00+00:00","Expires":"2026-09-16T00:00:00+00:00","Message":"","Source":"ipwho.is"}""";
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"legacy-json.db")}.ToString()))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO node_metadata VALUES($ip,$expiry,$json)";
                cmd.Parameters.AddWithValue("$ip",ip);cmd.Parameters.AddWithValue("$expiry",now.AddDays(7).ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$json",legacyJson);cmd.ExecuteNonQuery();
            }
            var legacyData=legacy.LoadNodeMetadata(ip)!;
            check(legacyData.Source=="ipwho.is"&&legacyData.Sources.Count==0&&legacyData.CountryCode==""&&legacyData.RegionCode==""&&legacyData.NetworkName==""&&legacyData.QueryState==""&&legacyData.AsnSource=="","old JSON missing every new field restores safe defaults through production deserialization");
            check(NodeClassifier.Locate(legacyData,now).Label.StartsWith("圣何塞")&&legacyData.Description.Contains("CHINANET"),"legacy JSON renders location and metadata without a null reference");
            legacy.SaveNodeMetadata(legacyData);
            check(legacy.LoadProviderMetadata(ip,"ipwho.is")?.City=="San Jose","legacy metadata can be promoted into the provider cache");
            var h=New("evidence");h.SaveNodeMetadata(washington);h.SaveNodeMetadata(info);
            check(h.LoadProviderMetadata(ip,"ipwho.is")!.City=="Washington"&&h.LoadProviderMetadata(ip,"ipinfo-core")!.City=="San Jose","each provider owns an independent successful observation");
            check(NodeClassifier.Locate(h.LoadNodeMetadata(ip),now).Conflict,"combined cache preserves multiple provider evidence");
            h.SaveNodeMetadata(info with{Success=false,Message="rate limited",Expires=now.AddMinutes(10)});
            check(h.LoadProviderMetadata(ip,"ipinfo-core")!.Success&&h.LoadProviderMetadata(ip,"ipinfo-core",true)!.Success==false&&h.LoadNodeMetadata(ip)!.Sources.Any(s=>s.QueryState.Contains("rate limited")),"failed attempt preserves last success and records separate error state");
            h.SetMetadataCooldown(now.AddHours(1),"ipinfo-core");check(h.MetadataCooldown("ipwho.is")<=now&&h.MetadataCooldown("ipinfo-core")>now,"provider cooldowns are isolated");
            h.ResetProviderFailures("ipinfo-core");check(h.MetadataCooldown("ipinfo-core")<=now&&h.LoadProviderMetadata(ip,"ipinfo-core",true)!.Success,"saving credentials can reset failed authentication without erasing successful data");
            h.SetCalibration(manual);check(NodeClassifier.Locate(h.LoadNodeMetadata(ip),now).State=="manual"&&h.LoadCalibration(ip)!.Note==manual.Note,"single-IP calibration is persistent and reversible");
            check(h.LoadCalibration("202.97.63.31") is null,"calibration does not expand into neighbouring IPs");
            h.RemoveCalibration(ip);check(h.LoadCalibration(ip) is null&&NodeClassifier.Locate(h.LoadNodeMetadata(ip),now).Conflict,"undo restores provider evidence instead of deleting annotations");
            check(Reject(()=>h.SetCalibration(manual with{Address="10.0.0.1"}))&&Reject(()=>h.SetCalibration(manual with{Note=""})),"invalid calibration is rejected before entering the writer");
            var endpoint=Target.Parse("186.241.113.4",0,ProbeProtocol.Icmp,1000);
            var run=new RouteRun(Guid.NewGuid().ToString("N"),endpoint.Key,endpoint.Address,"fixture",now,now,"fixture","complete",true,1000,32,[new(10,1,ip,11013,149.6),new(11,1,"218.30.53.210",11013,152)]);
            h.SaveRoute(run);var original=h.ReinterpretWithCurrentEvidence(run);h.SetCalibration(manual);
            check(h.CurrentRouteAnnotation(run,now).Id==original.Id,"editing global metadata leaves the original route snapshot frozen until explicit reinterpretation");
            var revised=h.ReinterpretWithCurrentEvidence(run);
            check(revised.Id!=original.Id&&h.LoadRouteAnnotation(run.Id,true)!.Id==original.Id,"explicit reinterpretation adds a revision and retains earliest evidence");
            await h.RecordAsync(()=>h.SaveNextTraceImport(import));
            check(h.LoadNextTraceImports().Single().Hops[2].RttMs==149.6&&h.Load(endpoint,now).Day.Attempts==0&&h.LoadRoutes(endpoint).Count==1,"import history never creates monitor samples or replaces route runs");
            var secondMeta=Combine(san,Meta("ipinfo-core",city:"Fremont")) with{Address="218.30.53.210"};
            var routeAnno=RouteClassifier.Classify(run,new Dictionary<string,NodeMetadata>{{ip,Combine(san,Meta("ipinfo-core",city:"Fremont"))},{"218.30.53.210",secondMeta}},now,"fixture");
            check(RouteTable.Build(run,routeAnno).Rows.Count(r=>r.Addresses.Length>0)==2&&RouteTable.Build(run,routeAnno).Rows.All(r=>!r.Merged),"adjacent conflicted locations never collapse into one overview row");
            var old=New("expired");old.SaveNodeMetadata(san with{Queried=now.AddDays(-8),Expires=now.AddDays(-1)});
            using(var client=new NodeMetadataClient(old,new Handler(_=>new(HttpStatusCode.ServiceUnavailable))))
                check((await client.GetAsync(ip,true,CancellationToken.None)).Success&&NodeClassifier.Locate(old.LoadNodeMetadata(ip),now).Stale,"network outage retains an expired successful city");
            var both=New("both");var calls=new List<string>();
            using(var client=new NodeMetadataClient(both,new Handler(request=>
            {
                calls.Add(request.RequestUri!.Host);
                if(request.RequestUri.Host=="api.ipinfo.io")
                {
                    check(request.Headers.Authorization?.Parameter=="fixture-token"&&!request.RequestUri.ToString().Contains("fixture-token"),"IPinfo credential travels in auth header only");
                    return new(HttpStatusCode.OK){Content=new StringContent("""{"ip":"202.97.63.30","geo":{"city":"San Jose","region":"California","country":"United States"},"as":{"asn":"AS4134"}}""")};
                }
                return new(HttpStatusCode.OK){Content=new StringContent("""{"ip":"202.97.63.30","success":true,"country":"美国","region":"District of Columbia","city":"Washington","connection":{"asn":4134}}""")};
            })))
            {
                client.Configure(new("ipinfo-core",true,"fixture-token"));
                var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>client.GetAsync(ip,true,CancellationToken.None)));
                check(calls.Count==2&&results.All(r=>r.Sources.Count==2),"multiple targets share exactly one request per provider for a new node");
                check(results.All(r=>NodeClassifier.Locate(r,now).Conflict),"double-source lookup reaches conflict resolution");
                await client.GetAsync(ip,false,CancellationToken.None);check(calls.Count==2,"offline view never refreshes either provider");
            }
            var longLife=New("long-calibration");longLife.SetCalibration(manual with{Expires=now.AddDays(90)});longLife.Prune(now.AddDays(40));
            check(longLife.LoadNodeMetadata(ip)?.Calibration is not null,"retention preserves still-valid long-lived manual calibration");
            check(!NodeClassifier.Locate(longLife.LoadNodeMetadata(ip),now).Details.Contains("ipwho.is")&&longLife.LoadNodeMetadata(ip)!.Source=="local-calibration","manual-only evidence never invents an online provider lookup");
            var migration=New("migration");migration.SaveNodeMetadata(san);
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"migration.db")}.ToString()))
            {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE geo_observation; DROP TABLE geo_request; DROP TABLE node_calibration; DROP TABLE calibration_audit; DROP TABLE nexttrace_import; PRAGMA user_version=4";cmd.ExecuteNonQuery();}
            migration.Initialize();migration.Initialize();
            check(migration.LoadProviderMetadata(ip,"ipwho.is")?.City=="San Jose","v4 migration imports existing observations once and preserves values");
        }
        finally{foreach(var h in histories)await h.DisposeAsync();}
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request));}
}
