using System.Net;
using System.Text.Json;
using TcpLatencyMonitor.Core;

internal static class NextTraceChecks
{
    internal const string Geo="""{"ip":"202.97.63.30","asnumber":"4134","country":"美国","prov":"加利福尼亚州","city":"圣何塞","whois":"CHINANET-BB","owner":"China Telecom"}""";
    private sealed class Http(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return Task.FromResult(response(request));}
    }
    private sealed class Pow:INextTraceLookup
    {
        public int Calls;public bool Fail;
        public Task<string> LookupAsync(string address,CancellationToken token){Calls++;token.ThrowIfCancellationRequested();return Fail?Task.FromException<string>(new IOException("offline fixture")):Task.FromResult(Geo.Replace("202.97.63.30",address));}
        public void Dispose(){}
    }
    public static async Task Run(Action<bool,string> check)
    {
        string folder=Path.Combine(Path.GetTempPath(),"IpQuality-nexttrace-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var histories=new List<History>();History New(string name){var h=new History(Path.Combine(folder,name+".db"));h.Initialize();histories.Add(h);return h;}
        var now=DateTimeOffset.UtcNow;const string ip="202.97.63.30";
        try
        {
            var db=New("cache");var pow=new Pow();
            var handler=new Http(request=>
            {
                check(request.RequestUri!.AbsolutePath=="/v4/ipGeo"&&request.Headers.GetValues("X-NextTrace-Token").Single()=="fixture-secret"&&!request.RequestUri.ToString().Contains("fixture-secret"),"v4 uses the official endpoint and header, not a credential URL");
                var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Geo)};
                response.Headers.Add("X-NextTrace-Quota-Expires-At",now.AddDays(3).ToString("O"));return response;
            });
            using(var client=new NodeMetadataClient(db,handler,pow))
            {
                client.Configure(new("nexttrace",NextTraceToken:"fixture-secret"));
                var results=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>client.GetAsync(ip,true,CancellationToken.None)));
                check(handler.Calls==1&&pow.Calls==0&&results.All(r=>r.Source=="nexttrace"&&r.City=="圣何塞"),"concurrent targets share one v4 result and never start PoW on v4 success");
                check(results.All(r=>r.Expires-r.Queried==TimeSpan.FromDays(1)),"NextTrace successful results default to a 24-hour database cache");
                check(client.DescribeNextTraceToken().Contains("服务端有效期"),"actual token expiry is learned from the service header");
            }
            var secondPow=new Pow();var secondHttp=new Http(_=>throw new Exception("cache should avoid HTTP"));
            using(var client=new NodeMetadataClient(db,secondHttp,secondPow))
            {
                client.Configure(new("nexttrace"));await client.GetAsync(ip,true,CancellationToken.None);
                check(secondHttp.Calls==0&&secondPow.Calls==0,"changing transport or restarting the client reuses the shared database cache");
            }
            foreach(var status in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.ServiceUnavailable})
            {
                var h=New("fallback-"+(int)status);var fake=new Pow();var http=new Http(_=>new(status));
                using var client=new NodeMetadataClient(h,http,fake);client.Configure(new("nexttrace",NextTraceToken:"rejected-fixture"));
                var first=await client.GetAsync(ip,true,CancellationToken.None);await client.GetAsync("218.30.53.210",true,CancellationToken.None);
                check(first.Success&&fake.Calls==2&&http.Calls==1,$"HTTP {(int)status} falls back to PoW and avoids retrying v4 for every IP");
            }
            var limited=New("limited");var limitedPow=new Pow();var limitedHttp=new Http(_=>new(HttpStatusCode.TooManyRequests));
            using(var client=new NodeMetadataClient(limited,limitedHttp,limitedPow))
            {
                client.Configure(new("nexttrace",NextTraceToken:"rate-fixture"));var result=await client.GetAsync(ip,true,CancellationToken.None);
                check(!result.Success&&limitedPow.Calls==0&&limited.MetadataCooldown("nexttrace")>now,"429 applies a service cooldown instead of evading it through PoW");
            }
            var expired=New("expired-token");var expiredPow=new Pow();var expiredHttp=new Http(_=>
            {
                var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Geo)};response.Headers.Add("X-NextTrace-Quota-Expires-At",now.AddSeconds(-1).ToString("O"));return response;
            });
            using(var client=new NodeMetadataClient(expired,expiredHttp,expiredPow))
            {
                client.Configure(new("nexttrace",NextTraceToken:"expired-fixture"));await client.GetAsync(ip,true,CancellationToken.None);await client.GetAsync("218.30.53.210",true,CancellationToken.None);
                check(expiredHttp.Calls==1&&expiredPow.Calls==1&&client.DescribeNextTraceToken().Contains("过期"),"known expired token skips v4 for new nodes");
            }
            var active=New("activity");
            RouteRun Route(string address,DateTimeOffset when)=>new(Guid.NewGuid().ToString("N"),"fixture-target",address,"fixture",when,when,"fixture","partial",false,1000,32,[new(1,1,address,11013,1),new(2,1,"10.0.0.1",11013,2)]);
            var old=NodeMetadataClient.ParseNextTrace(ip,Geo,now.AddHours(-25));
            active.SaveNodeMetadata(old);active.SaveRoute(Route(ip,now.AddHours(-1)));
            active.SaveNodeMetadata(old with{Address="218.30.53.210"});active.SaveRoute(Route("218.30.53.210",now.AddHours(-25)));
            active.SaveNodeMetadata(old with{Address="186.241.113.4",Queried=now.AddHours(-2),Expires=now.AddHours(22)});active.SaveRoute(Route("186.241.113.4",now));
            active.SaveRoute(Route("59.43.246.178",now));
            var due=active.DueRouteNodes(now.AddSeconds(1),new("nexttrace"));
            check(due.Count==2&&due.Contains(ip)&&due.Contains("59.43.246.178"),"only new or expired nodes seen within 24 hours are due; dormant, fresh and private nodes are excluded");
            var fakeActive=new Pow();using(var client=new NodeMetadataClient(active,new Http(_=>throw new Exception("no v4 token")),fakeActive))
            {
                client.Configure(new("nexttrace"));await client.RefreshRecentAsync(CancellationToken.None);await client.RefreshRecentAsync(CancellationToken.None);
                check(fakeActive.Calls==2&&active.DueRouteNodes(DateTimeOffset.UtcNow,new("nexttrace")).Count==0,"daily background refresh is idempotent and does not requery fresh active IPs");
            }
            var failing=New("failed-refresh");failing.SaveNodeMetadata(old);var failedPow=new Pow{Fail=true};
            using(var client=new NodeMetadataClient(failing,new Http(_=>throw new Exception("no token")),failedPow))
            {
                client.Configure(new("nexttrace"));var result=await client.GetAsync(ip,true,CancellationToken.None);
                check(result.Success&&result.City=="圣何塞"&&result.Expires<now&&result.QueryState.Contains("失败"),"failed refresh preserves the previous success and marks its stale/error state");
            }
            var preferred=GeoResolver.Combine(ip,[NodeMetadataClient.ParseNextTrace(ip,Geo,now),old with{ProviderId="ipwho.is",City="Washington",Region="District of Columbia",Queried=now,Expires=now.AddDays(1)}],null,"nexttrace",now)!;
            check(NodeClassifier.Locate(preferred,now).Label=="圣何塞"&&preferred.Sources.Count==2,"NextTrace provides the displayed city while alternate source evidence remains available");
            var hk=NodeMetadataClient.ParseNextTrace("59.43.246.178","""{"ip":"59.43.246.178","country":"中国","prov":"香港","city":"","asnumber":"","whois":"CN2-Global"}""",now);
            check(hk.Asn is null&&hk.City==""&&NodeClassifier.Locate(hk,now).Label.Contains("香港"),"Hong Kong region and missing ASN are retained without inventing a city or GIA classification");
            bool rejected=false;try{NodeMetadataClient.ParseNextTrace(ip,Geo.Replace(ip,"8.8.8.8"),now);}catch(JsonException){rejected=true;}
            check(rejected,"v4 rejects a response for a different IP");
        }
        finally{foreach(var h in histories)await h.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
    public static async Task Live(string helperPath)
    {
        string path=Path.Combine(Path.GetTempPath(),"IpQuality-nexttrace-live-"+Guid.NewGuid().ToString("N")+".db");
        await using var history=new History(path);history.Initialize();
        using var helper=new NextTraceProcessClient(helperPath);using var client=new NodeMetadataClient(history,nextTrace:helper);client.Configure(new("nexttrace"));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));var result=await client.GetAsync("202.97.63.30",true,timeout.Token);
        if(!result.Success||result.Source!="nexttrace"||result.City.Length==0)throw new Exception("Production helper live lookup failed");
        var repeat=await client.GetAsync(result.Address,true,timeout.Token);
        if(repeat.Queried!=result.Queried)throw new Exception("Production cache was not reused");
        Console.WriteLine($"LIVE PASS: {result.Address} {result.Country}/{result.Region}/{result.City} AS{result.Asn}; {result.QueryState}; expires {result.Expires:O}; repeated query reused database cache.");
    }
    private sealed class V4Only:INextTraceLookup,INextTraceV4Lookup
    {
        private readonly NextTraceProcessClient helper=new(Environment.GetEnvironmentVariable("IPQUALITY_TEST_HELPER")??throw new InvalidOperationException("Missing helper path"));
        public int Calls;
        public Task<string> LookupAsync(string address,CancellationToken token){Calls++;throw new IOException("PoW disabled for v4 verification");}
        public async Task<HttpResponseMessage> LookupV4Async(string address,string credential,CancellationToken token)
        {
            var response=await helper.LookupV4Async(address,credential,token);Console.WriteLine($"V4 HTTP status={(int)response.StatusCode}");return response;
        }
        public void Dispose()=>helper.Dispose();
    }
    public static async Task LiveV4()
    {
        string secret=await Console.In.ReadLineAsync()??"";
        if(secret.Length==0)throw new InvalidOperationException("No token supplied through stdin");
        string path=Path.Combine(Path.GetTempPath(),"IpQuality-v4-live-"+Guid.NewGuid().ToString("N")+".db");
        await using var history=new History(path);history.Initialize();var pow=new V4Only();
        using var client=new NodeMetadataClient(history,nextTrace:pow);client.Configure(new("nexttrace",NextTraceToken:secret));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result=await client.GetAsync("202.97.63.30",true,timeout.Token);
        Console.WriteLine($"V4 diagnostic: success={result.Success}; state={result.QueryState}; message={result.Message}; fallbackCalls={pow.Calls}; tokenState={client.DescribeNextTraceToken()}");
        if(!result.Success||result.QueryState!="NextTrace v4"||pow.Calls!=0)throw new InvalidOperationException("Real v4 verification failed; no credential or response body logged");
        Console.WriteLine($"V4 LIVE PASS: {result.Address} {result.Country}/{result.Region}/{result.City} AS{result.Asn}; {result.QueryState}; {client.DescribeNextTraceToken()}; no PoW fallback.");
        var cached=await client.GetAsync(result.Address,true,timeout.Token);
        if(cached.Queried!=result.Queried)throw new InvalidOperationException("V4 cache reuse failed");
        Console.WriteLine("V4 CACHE PASS: repeated query used the database cache.");
    }
}
