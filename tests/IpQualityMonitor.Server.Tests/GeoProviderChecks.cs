using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TcpLatencyMonitor.Core;

internal static class GeoProviderChecks
{
    private const string Ip="8.8.8.8";
    private const string OtherIp="1.1.1.1";
    private static string Geo(string address)=>JsonSerializer.Serialize(new {ip=address,asnumber="15169",country="US",prov="California",city="Fixture city",owner="Fixture owner"});
    private sealed class MockLookup:INextTraceLookup,INextTraceV4Lookup
    {
        public int V3Calls,V4Calls;
        public Func<string,CancellationToken,Task<string>> V3 {get;set;}=(address,_)=>Task.FromResult(Geo(address));
        public Func<string,string,CancellationToken,Task<HttpResponseMessage>> V4 {get;set;}=(address,_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Geo(address))});
        public Task<string> LookupAsync(string address,CancellationToken token){Interlocked.Increment(ref V3Calls);return V3(address,token);}
        public Task<HttpResponseMessage> LookupV4Async(string address,string credential,CancellationToken token){Interlocked.Increment(ref V4Calls);return V4(address,credential,token);}
        public void Dispose(){}
    }
    private sealed class NoHttp:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>throw new InvalidOperationException("Unexpected real HTTP path in fixture");
    }
    private static NodeMetadataClient Client(History history,MockLookup lookup,string mode="v4",string credential="fixture-token",TimeSpan? timeout=null)
    {
        var client=new NodeMetadataClient(history,new NoHttp(),lookup);
        client.Configure(new("nexttrace",NextTraceToken:mode=="v4"?credential:""),new(mode,timeout));return client;
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        string folder=Path.Combine(Path.GetTempPath(),"iqm-geo-provider-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var histories=new List<History>();
        History New(string name){var history=new History(Path.Combine(folder,name+".db"));history.Initialize();histories.Add(history);return history;}
        try
        {
            var history=New("dedup");var lookup=new MockLookup();
            using(var client=Client(history,lookup))
            {
                var results=await Task.WhenAll(client.GetAsync(Ip,true,CancellationToken.None),client.GetAsync("::ffff:8.8.8.8",true,CancellationToken.None));
                await client.GetAsync("192.168.1.1",true,CancellationToken.None);await client.GetAsync("2001:db8::1",true,CancellationToken.None);
                check(lookup.V4Calls==1&&lookup.V3Calls==0&&results.All(value=>value.Success),"strict v4 canonicalizes mapped IPs, deduplicates, and never sends private/reserved IPs");
                check(client.GetNextTraceState().LastSuccess is not null&&client.GetNextTraceAddressState(Ip).Status=="cached","safe state exposes successful cache and last-success timestamps");
                check(!JsonSerializer.Serialize(client.GetNextTraceState()).Contains("fixture-token",StringComparison.Ordinal),"public provider state never includes the token");
                bool rejected=false;try{client.Configure(new("nexttrace",NextTraceToken:"not-for-v3"),new("v3"));}catch(ArgumentException){rejected=true;}
                check(rejected&&client.NextTracePolicy.Mode=="v4","v3 rejects credential configuration without partially changing active policy");
            }
            foreach(int code in new[]{401,403,503})
            {
                var failed=New("strict-"+code);var mock=new MockLookup{V4=(_,_,_)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)code))};
                using var client=Client(failed,mock);var value=await client.GetAsync(Ip,true,CancellationToken.None);
                check(!value.Success&&mock.V4Calls==1&&mock.V3Calls==0,$"strict HTTP {code} never falls back to v3 or another provider");
                if(code!=503)check(client.GetNextTraceState().AuthenticationBlocked,"401/403 blocks the selected credential");
                else check(client.GetNextTraceState().CooldownUntil is {} until&&until>DateTimeOffset.UtcNow.AddSeconds(20)&&until<DateTimeOffset.UtcNow.AddSeconds(65),"5xx uses bounded short jittered backoff");
            }
            var auth=New("auth-restart");var denied=new MockLookup{V4=(_,_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))};
            using(var client=Client(auth,denied)){await client.GetAsync(Ip,true,CancellationToken.None);}
            await auth.DisposeAsync();histories.Remove(auth);auth=New("auth-restart");var recovered=new MockLookup();
            using(var client=Client(auth,recovered))
            {
                await client.GetAsync(OtherIp,true,CancellationToken.None);
                check(recovered.V4Calls==0&&client.GetNextTraceState().AuthenticationBlocked,"auth rejection persists across database/client restart");
                client.Configure(new("nexttrace",NextTraceToken:"changed-fixture-token"),new("v4"));await client.GetAsync(Ip,true,CancellationToken.None);
                check(recovered.V4Calls==1&&!client.GetNextTraceState().AuthenticationBlocked,"credential change unblocks without discarding the old credential rejection");
                client.Configure(new("nexttrace",NextTraceToken:"fixture-token"),new("v4"));
                check(client.GetNextTraceState().AuthenticationBlocked,"returning to rejected credential remains blocked");
                client.ResetNextTraceAuthentication();await client.GetAsync(OtherIp,true,CancellationToken.None);
                check(recovered.V4Calls==2&&!client.GetNextTraceState().AuthenticationBlocked,"explicit authentication reset permits a same-credential retry");
            }
            foreach(string mode in new[]{"v3","v4"})
            {
                var limited=New("limited-"+mode);var mock=new MockLookup
                {
                    V3=(_,_)=>Task.FromException<string>(new NextTraceProviderException(429,"120")),
                    V4=(_,_,_)=>{var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.TryAddWithoutValidation("Retry-After","120");return Task.FromResult(response);}
                };
                using(var client=Client(limited,mock,mode))await client.GetAsync(Ip,true,CancellationToken.None);
                await limited.DisposeAsync();histories.Remove(limited);limited=New("limited-"+mode);var second=new MockLookup();
                using var next=Client(limited,second,mode=="v3"?"v4":"v3");next.ResetNextTraceAuthentication();await next.GetAsync(OtherIp,true,CancellationToken.None);
                check(second.V3Calls==0&&second.V4Calls==0&&next.GetNextTraceState().CooldownUntil>DateTimeOffset.UtcNow.AddSeconds(100),$"{mode} 429 cooldown persists across restart, mode switch, and auth reset");
            }
            var pending=New("cancel");var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow=new MockLookup{V4=async (_,_,token)=>{started.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new InvalidOperationException("Unreachable fixture");}};
            using(var client=Client(pending,slow))
            using(var activeCancellation=new CancellationTokenSource())
            using(var queueCancellation=new CancellationTokenSource())
            {
                var active=client.GetAsync(Ip,true,activeCancellation.Token);await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var queued=client.GetAsync(OtherIp,true,queueCancellation.Token);
                check(client.GetNextTraceAddressState(OtherIp).Status=="queued"&&!client.GetNextTraceAddressState(OtherIp).Attempted,"queued IP is visibly unattempted and has no provider timestamp");
                queueCancellation.Cancel();try{await queued;}catch(OperationCanceledException){}
                activeCancellation.Cancel();try{await active;}catch(OperationCanceledException){}
                check(slow.V4Calls==1&&pending.MetadataCooldown("nexttrace")<=DateTimeOffset.UtcNow&&client.GetNextTraceState().Status=="canceled","generation cancellation is distinct from provider timeout and imposes no failure cooldown");
                check(client.GetNextTraceAddressState(OtherIp).LastAttempt is null&&client.OrderNextTraceCandidates([Ip,OtherIp,"::ffff:8.8.8.8","10.0.0.1"]).SequenceEqual(new[]{OtherIp,Ip}),"fair order prioritizes unattempted canonical public IPs after cancellation");
                slow.V4=(address,_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Geo(address))});
                await client.GetAsync(OtherIp,true,CancellationToken.None);
                check(slow.V4Calls==2,"next generation proceeds after cancellation without waiting for provider cooldown");
            }
            var timeoutHistory=New("timeout");var timeoutMock=new MockLookup{V4=async (_,_,token)=>{await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new InvalidOperationException("Unreachable fixture");}};
            using(var client=Client(timeoutHistory,timeoutMock,timeout:TimeSpan.FromMilliseconds(50)))
            {
                var value=await client.GetAsync(Ip,true,CancellationToken.None);
                check(!value.Success&&client.GetNextTraceAddressState(Ip).Attempted&&timeoutHistory.LoadProviderMetadata(Ip,"nexttrace",true)?.QueryState=="timeout","only an actual timed-out provider request records timeout");
                check(client.GetNextTraceState().CooldownUntil is not null&&client.GetNextTraceState().Status=="timeout"&&client.GetNextTraceAddressState(Ip).Status=="timeout","actual provider timeout receives short failure backoff and a distinct timeout status");
                check(!client.GetNextTraceAddressState(OtherIp).Attempted&&client.GetNextTraceAddressState(OtherIp).Status=="cooling-down","another unattempted IP never inherits the timed-out IP status");
            }
            var stale=New("stale");stale.SaveNodeMetadata(NodeMetadataClient.ParseNextTrace(Ip,Geo(Ip),DateTimeOffset.UtcNow.AddDays(-2)));
            var fail=new MockLookup{V4=(_,_,_)=>Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture failure"))};
            using(var client=Client(stale,fail))
            {
                var value=await client.GetAsync(Ip,true,CancellationToken.None);
                check(value.Success&&value.City=="Fixture city"&&value.Expires<DateTimeOffset.UtcNow&&value.QueryState.Contains("失败",StringComparison.Ordinal),"failed refresh preserves last success and its real stale expiry");
            }
            var rate=New("local-rate");var paced=new MockLookup();
            using(var client=Client(rate,paced,"v3"))
            {
                await client.GetAsync(Ip,true,CancellationToken.None);
            }
            using(var client=Client(rate,paced,"v3"))
            {
                var watch=Stopwatch.StartNew();await client.GetAsync(OtherIp,true,CancellationToken.None);
                check(watch.ElapsedMilliseconds>=200&&paced.V3Calls==2&&paced.V4Calls==0,"300ms local cap survives client replacement and explicit v3 never invokes v4");
            }
            var rejectedWrite=New("rejected-metadata");
            using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(folder,"rejected-metadata.db")))
            {
                connection.Open();using var command=connection.CreateCommand();
                command.CommandText="CREATE TRIGGER reject_derived_metadata BEFORE INSERT ON geo_observation BEGIN SELECT RAISE(ABORT,'fixture constraint'); END";command.ExecuteNonQuery();
            }
            var writeMock=new MockLookup();using(var client=Client(rejectedWrite,writeMock))
            {
                bool rejected=false;try{await client.GetAsync(Ip,true,CancellationToken.None);}catch(InvalidDataException){rejected=true;}
                check(rejected&&rejectedWrite.WriteFailure is null&&rejectedWrite.LoadProviderMetadata(Ip,"nexttrace") is null&&client.GetNextTraceState().Status=="storage-error","rejected derived metadata does not poison raw sampling or report provider success");
                check(!client.NeedsRefresh(OtherIp,DateTimeOffset.UtcNow)&&writeMock.V4Calls==1,"failed optional provider bookkeeping stops further requests until explicit reconfiguration");
            }
            var budget=New("budget");var now=DateTimeOffset.UtcNow;
            for(int index=0;index<900;index++)if(!budget.ReserveMetadataRequest(now,"nexttrace"))throw new InvalidOperationException("Budget fixture failed");
            await budget.DisposeAsync();histories.Remove(budget);budget=New("budget");var budgetMock=new MockLookup();
            using(var client=Client(budget,budgetMock))
            {
                await client.GetAsync(Ip,true,CancellationToken.None);client.Configure(new("nexttrace"),new("v3"));await client.GetAsync(OtherIp,true,CancellationToken.None);
                check(budgetMock.V3Calls==0&&budgetMock.V4Calls==0&&client.GetNextTraceState().Status=="budget-exhausted","900/day provider budget persists across restart and transport changes");
                check(client.GetNextTraceAddressState(Ip).LastAttempt is null,"budget rejection does not mark an unattempted IP as timed out or attempted");
                client.Configure(new("nexttrace"),new("offline"));await client.GetAsync(Ip,true,CancellationToken.None);
                check(client.GetNextTraceState().Status=="offline"&&budgetMock.V3Calls+budgetMock.V4Calls==0,"offline mode cannot query even when a caller requests network");
            }
        }
        finally
        {
            foreach(var history in histories)await history.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);
        }
    }
}
