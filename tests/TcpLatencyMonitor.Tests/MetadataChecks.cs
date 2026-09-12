using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TcpLatencyMonitor.Core;

internal static class MetadataChecks
{
    private const string Reply="""{"ip":"1.1.1.1","success":true,"country":"澳大利亚","region":"Queensland","city":"Brisbane","connection":{"asn":13335,"isp":"Cloudflare, Inc.","org":"APNIC and Cloudflare DNS Resolver project"}}""";
    public static async Task Run(Action<bool,string> check)
    {
        var stamp=DateTimeOffset.UtcNow;
        var parsed=NodeMetadataClient.Parse("1.1.1.1",Reply,stamp);
        check(parsed.Success&&parsed.Asn==13335&&parsed.Isp.Contains("Cloudflare")&&parsed.Country=="澳大利亚"&&parsed.Expires-stamp==TimeSpan.FromDays(1),"metadata ASN/operator/region parsing and one-day TTL");
        var missing=NodeMetadataClient.Parse("1.1.1.1","""{"ip":"1.1.1.1","success":true,"connection":null}""",stamp);
        check(missing.Success&&missing.Asn is null&&missing.Description.Contains("未知"),"missing metadata fields remain unknown");
        bool mismatch=false;try{NodeMetadataClient.Parse("8.8.8.8",Reply,stamp);}catch(JsonException){mismatch=true;}
        check(mismatch,"metadata rejects a response for another IP");
        var privateIps=new[]{"127.0.0.1","::1","10.1.2.3","172.16.1.1","192.168.1.1","100.64.0.1","169.254.1.1","0.0.0.0","224.0.0.1","192.0.2.1","198.18.0.1","198.51.100.1","203.0.113.1","::ffff:10.0.0.1","fe80::1%3","fd00::1","2001:db8::1","2002:a00:1::","64:ff9b::a00:1","https://example.com"};
        check(privateIps.All(p=>NodeMetadataClient.LocalLabel(p) is not null)&&NodeMetadataClient.LocalLabel("1.1.1.1") is null&&NodeMetadataClient.LocalLabel("2606:4700:4700::1111") is null,"nonpublic IPv4/IPv6 nodes never need online lookup");
        string directory=Path.Combine(Path.GetTempPath(),"IpQuality-metadata-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        History NewHistory(string name){var h=new History(Path.Combine(directory,name+".db"));h.Initialize();return h;}
        var history=NewHistory("cache");var handler=new FakeHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Reply)});
        using(var client=new NodeMetadataClient(history,handler))
        {
            await client.GetAsync("1.1.1.1",false,CancellationToken.None);
            foreach(var ip in privateIps)await client.GetAsync(ip,true,CancellationToken.None);
            check(handler.Calls==0,"disabled lookup and private nodes send zero HTTP requests");
            var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>client.GetAsync("1.1.1.1",true,CancellationToken.None)));
            check(handler.Calls==1&&results.All(r=>r.Success),"concurrent duplicate nodes use one online request");
            var offline=await client.GetAsync("1.1.1.1",false,CancellationToken.None);
            check(offline.Success&&handler.Calls==1,"cached annotations remain available with online lookup disabled");
        }
        var reopened=new History(Path.Combine(directory,"cache.db"));reopened.Initialize();
        check(reopened.LoadNodeMetadata("1.1.1.1")?.Asn==13335,"annotation cache and source survive restart and schema initialization");
        history.SaveNodeMetadata(parsed with{Queried=stamp.AddDays(-8),Expires=stamp.AddDays(-1)});
        var renewed=new FakeHandler(_=>new(HttpStatusCode.OK){Content=new StringContent(Reply)});
        using(var client=new NodeMetadataClient(history,renewed))
            check((await client.GetAsync("1.1.1.1",true,CancellationToken.None)).Queried>stamp.AddMinutes(-1)&&renewed.Calls==1,"expired annotations refresh only with network opt-in");
        var failures=NewHistory("failure");var bad=new FakeHandler(_=>new(HttpStatusCode.OK){Content=new StringContent("not JSON")});
        using(var client=new NodeMetadataClient(failures,bad))
        {
            var result=await client.GetAsync("1.1.1.1",true,CancellationToken.None);
            await client.GetAsync("1.1.1.1",true,CancellationToken.None);
            check(!result.Success&&bad.Calls==1&&failures.MetadataCooldown()>stamp,"malformed service response is contained, cached and backed off");
        }
        var limits=NewHistory("limits");var throttled=new FakeHandler(_=>{var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);r.Headers.RetryAfter=new(TimeSpan.FromHours(2));return r;});
        using(var client=new NodeMetadataClient(limits,throttled))await client.GetAsync("1.1.1.1",true,CancellationToken.None);
        var noCalls=new FakeHandler(_=>throw new Exception("must not be called"));
        using(var client=new NodeMetadataClient(limits,noCalls))await client.GetAsync("8.8.8.8",true,CancellationToken.None);
        check(throttled.Calls==1&&noCalls.Calls==0&&limits.MetadataCooldown()>stamp.AddMinutes(119),"HTTP 429 Retry-After persists and blocks other nodes across client restart");
        var quota=NewHistory("quota");for(int i=0;i<900;i++)quota.ReserveMetadataRequest(stamp);
        check(!quota.ReserveMetadataRequest(stamp)&&quota.ReserveMetadataRequest(stamp.AddDays(1).AddSeconds(1)),"persistent rolling 24-hour query budget expires correctly");
        var cancelled=NewHistory("cancel");var slow=new FakeHandler(_=>new(HttpStatusCode.OK),true);
        using(var client=new NodeMetadataClient(cancelled,slow))
        using(var cts=new CancellationTokenSource(30))
        {
            bool stopped=false;try{await client.GetAsync("1.1.1.1",true,cts.Token);}catch(OperationCanceledException){stopped=true;}
            check(stopped&&cancelled.LoadNodeMetadata("1.1.1.1") is null,"cancelled online query does not create a negative cache entry");
        }
        var target=Target.Parse("127.0.0.1",0,ProbeProtocol.Icmp,1000);
        var route=new RouteRun("export",target.Key,target.Address,"test",stamp,stamp,"test","complete",true,1000,32,[new HopProbe(1,1,"1.1.1.1",11013,10)]);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"cache.db")}.ToString()))
        {
            db.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<10001) INSERT INTO route_run SELECT 'route-'||x,$target,$time,json_set($json,'$.Id','route-'||x) FROM n; WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<5001) INSERT INTO monitor_event SELECT 'event-'||x,$target,$time,'Started','test',NULL,NULL FROM n;";
            cmd.Parameters.AddWithValue("$target",target.Key);cmd.Parameters.AddWithValue("$time",stamp.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(route));cmd.ExecuteNonQuery();tx.Commit();
        }
        var path=Path.Combine(directory,"export.zip");history.ExportBundle(target,path,DateTimeOffset.UtcNow);
        using(var zip=ZipFile.OpenRead(path))
        {
            using var routes=JsonDocument.Parse(zip.GetEntry("routes.json")!.Open());using var events=JsonDocument.Parse(zip.GetEntry("events.json")!.Open());
            using var metadata=JsonDocument.Parse(zip.GetEntry("node-metadata.json")!.Open());
            check(routes.RootElement.GetArrayLength()==10001&&events.RootElement.GetArrayLength()==5001,"diagnostic export streams all routes and events beyond former page limits");
            check(metadata.RootElement.GetArrayLength()==1&&metadata.RootElement[0].GetProperty("Source").GetString()=="ipwho.is","diagnostic export contains relevant cached annotations with provenance");
        }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> reply,bool slow=false):HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;if(request.RequestUri?.Scheme!="https"||request.RequestUri.Host!="ipwho.is")throw new Exception("Unexpected provider");
            if(slow)await Task.Delay(10000,token);return reply(request);
        }
    }
}
