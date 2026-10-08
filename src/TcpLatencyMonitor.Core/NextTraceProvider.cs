using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public interface INextTraceLookup:IDisposable
{
    Task<string> LookupAsync(string address,CancellationToken token);
}
public interface INextTraceV4Lookup
{
    Task<HttpResponseMessage> LookupV4Async(string address,string credential,CancellationToken token);
}

/// <summary>Legacy remains the default for the Windows client. Docker explicitly selects offline/v3/v4.</summary>
public sealed record NextTraceProviderPolicy(string Mode="legacy",TimeSpan? RequestTimeout=null);
public sealed record NextTraceProviderState(string Mode,string Status,bool AuthenticationBlocked,
    DateTimeOffset? CooldownUntil,DateTimeOffset? LastAttempt,DateTimeOffset? LastSuccess,int Pending,bool Active,string Message);
public sealed record NextTraceAddressState(string Status,bool Attempted,DateTimeOffset? LastAttempt,
    DateTimeOffset? LastSuccess,DateTimeOffset? RetryAfter);

/// <summary>A sanitized helper failure. Never retain upstream bodies or credentials in exceptions.</summary>
public sealed class NextTraceProviderException(int statusCode,string? retryAfter=null):IOException("NextTrace provider rejected the request")
{
    public int StatusCode {get;}=statusCode;
    public string? RetryAfter {get;}=retryAfter;
}

public sealed partial class NodeMetadataClient
{
    private NextTraceProviderPolicy _nextTracePolicy=new();
    public NextTraceProviderPolicy NextTracePolicy=>Volatile.Read(ref _nextTracePolicy);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,int> _queuedAddresses=new(StringComparer.Ordinal);
    private int _pendingMetadata;
    private int _metadataPersistenceFailed;
    private string? _activeNextTraceAddress;
    private static readonly string[] NextTraceStates=["unattempted","querying","success","authentication-blocked","rate-limited","cooling-down","budget-exhausted","timeout","failed","canceled"];
    private static NextTraceProviderPolicy ValidateNextTracePolicy(NextTraceProviderPolicy policy)
    {
        if(policy.Mode is not ("legacy" or "offline" or "v3" or "v4"))throw new ArgumentException("不支持的 NextTrace 模式。");
        if(policy.RequestTimeout is {} timeout&&(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromMinutes(2)))throw new ArgumentException("定位请求时限须在 0–120 秒内。");
        return policy;
    }
    private static string StrictCredentialKey(MetadataOptions options,NextTraceProviderPolicy policy)=>policy.Mode=="v4"?TokenKey(options.NextTraceToken):"nexttrace:strict:v3";
    private bool AuthenticationBlocked(MetadataOptions options,NextTraceProviderPolicy policy,DateTimeOffset now)
    {
        if(policy.Mode is "offline" or "legacy")return false;
        string key=StrictCredentialKey(options,policy);
        return policy.Mode=="v4"&&options.NextTraceToken.Length==0||_history.MetadataValue(key+":invalid")==1||
            policy.Mode=="v4"&&_history.MetadataValue(key+":expiry") is long expiry&&expiry>0&&expiry<=now.ToUnixTimeMilliseconds();
    }
    private DateTimeOffset? StateTime(string key)=>_history.MetadataValue(key) is long value&&value>0?DateTimeOffset.FromUnixTimeMilliseconds(value):null;
    private string SavedNextTraceState(string key)=>_history.MetadataValue(key) is long code&&code>=0&&code<NextTraceStates.Length?NextTraceStates[(int)code]:"unattempted";
    private void RequireMetadataWrite(bool saved)
    {
        if(saved)return;
        Volatile.Write(ref _metadataPersistenceFailed,1);
        throw new InvalidDataException("可选定位状态未能保存；停止本轮注释。");
    }
    private void SetNextTraceState(string status,string? address=null)
    {
        int code=Array.IndexOf(NextTraceStates,status);if(code<0)return;
        RequireMetadataWrite(_history.SetMetadataValue("nexttrace:state",code));
        if(address is not null)RequireMetadataWrite(_history.SetMetadataValue("nexttrace:state:"+address,code));
        Volatile.Write(ref _nextTraceStatus,status);
    }
    public NextTraceProviderState GetNextTraceState()
    {
        MetadataOptions options;NextTraceProviderPolicy policy;
        lock(_configurationSync){options=_options;policy=_nextTracePolicy;}
        var now=DateTimeOffset.UtcNow;var cooldown=_history.MetadataCooldown("nexttrace");
        bool active=Volatile.Read(ref _activeNextTraceAddress) is not null,blocked=AuthenticationBlocked(options,policy,now);
        string status=SavedNextTraceState("nexttrace:state");
        // A process restart cannot leave an old in-flight operation appearing active.
        if(status=="querying"&&!active)status="canceled";
        if(policy.Mode=="offline")status="offline";
        else if(Volatile.Read(ref _metadataPersistenceFailed)!=0)status="storage-error";
        else if(active)status="querying";
        else if(blocked)status="authentication-blocked";
        else if(cooldown>now)status=status is "rate-limited" or "timeout" or "failed"?status:"cooling-down";
        else if(status is "authentication-blocked" or "rate-limited" or "cooling-down")status="idle";
        int pending=Math.Max(0,Volatile.Read(ref _pendingMetadata)-(active?1:0));
        if((status is "unattempted" or "idle")&&pending>0)status="queued";
        return new(policy.Mode,status,blocked,cooldown>now?cooldown:null,StateTime("nexttrace:last-attempt"),StateTime("nexttrace:last-success"),pending,active,status);
    }
    public NextTraceAddressState GetNextTraceAddressState(string address)
    {
        if(LocalLabel(address) is not null)return new("private-or-invalid",false,null,null,null);
        address=CidrBlock.Normalize(address);var now=DateTimeOffset.UtcNow;
        var last=StateTime("nexttrace:attempt:"+address);var success=_history.LoadProviderMetadata(address,"nexttrace");
        var attempt=_history.LoadProviderMetadata(address,"nexttrace",true);string status=SavedNextTraceState("nexttrace:state:"+address);
        var provider=GetNextTraceState();bool active=Volatile.Read(ref _activeNextTraceAddress)==address;
        if(active)status="querying";
        else if(_queuedAddresses.TryGetValue(address,out int queued)&&queued>0)status="queued";
        else if(status=="querying")status="canceled";
        else if(IsFresh(success,Options,now))status="cached";
        else if(provider.Mode=="offline")status="offline";
        else if(provider.Status=="storage-error")status="storage-error";
        else if(provider.AuthenticationBlocked)status="authentication-blocked";
        else if(provider.CooldownUntil is not null&&status is not ("timeout" or "failed" or "rate-limited"))status=provider.Status=="rate-limited"?"rate-limited":"cooling-down";
        else if(status=="authentication-blocked")status="unattempted";
        return new(status,last is not null,last,success is {Success:true}?success.Queried:null,
            provider.CooldownUntil??(attempt is {Success:false}&&attempt.Expires>now?attempt.Expires:null));
    }
    /// <summary>Clear only the current credential's auth block. Shared 429 cooldown and daily quota remain intact.</summary>
    public void ResetNextTraceAuthentication()
    {
        lock(_configurationSync)
        {
            string key=StrictCredentialKey(_options,_nextTracePolicy);
            RequireMetadataWrite(_history.SetMetadataValue(key+":invalid",0));RequireMetadataWrite(_history.SetMetadataValue(key+":expiry",0));
        }
    }
    public IReadOnlyList<string> OrderNextTraceCandidates(IEnumerable<string> addresses)=>addresses
        .Where(address=>LocalLabel(address) is null).Select(CidrBlock.Normalize).Distinct(StringComparer.Ordinal)
        .Select(address=>(Address:address,Attempt:StateTime("nexttrace:attempt:"+address)??_history.LoadProviderMetadata(address,"nexttrace",true)?.Queried??DateTimeOffset.MinValue))
        .OrderBy(value=>value.Attempt).ThenBy(value=>value.Address,StringComparer.Ordinal).Select(value=>value.Address).ToArray();
    private static DateTimeOffset RetryUntil(string? retryAfter,DateTimeOffset now)
    {
        var until=now.AddHours(1);
        if(long.TryParse(retryAfter,NumberStyles.None,CultureInfo.InvariantCulture,out long seconds)&&seconds>=0)
            until=now.AddSeconds(Math.Min(seconds,7*24*60*60));
        else if(DateTimeOffset.TryParse(retryAfter,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date))until=date;
        return until<now.AddMinutes(1)?now.AddMinutes(1):until>now.AddDays(7)?now.AddDays(7):until;
    }
    private static DateTimeOffset TransientRetry(DateTimeOffset now)=>now.AddSeconds(30+Random.Shared.Next(31));
    private async Task QueryStrictNextTraceAsync(string address,MetadataOptions options,NextTraceProviderPolicy policy,CancellationToken token)
    {
        var now=DateTimeOffset.UtcNow;
        if(policy.Mode=="offline")return;
        if(Volatile.Read(ref _metadataPersistenceFailed)!=0)throw new InvalidDataException("可选定位状态无法保存；请检查存储后重新应用设置。");
        if(IsFresh(_history.LoadProviderMetadata(address,"nexttrace"),options,now))return;
        if(AuthenticationBlocked(options,policy,now)){SetNextTraceState("authentication-blocked");return;}
        if(_history.MetadataCooldown("nexttrace")>now)return;
        if(_history.LoadProviderMetadata(address,"nexttrace",true) is {Success:false} failed&&failed.Expires>now)return;
        var next=(StateTime("nexttrace:last-attempt")??DateTimeOffset.MinValue).AddMilliseconds(300);
        if(_providerNext.TryGetValue("nexttrace",out var localNext)&&localNext>next)next=localNext;
        if(next>now)await Task.Delay(next-now,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();now=DateTimeOffset.UtcNow;
        if(!_history.ReserveMetadataRequest(now,"nexttrace")){SetNextTraceState("budget-exhausted");return;}
        _providerNext["nexttrace"]=now.AddMilliseconds(300);
        RequireMetadataWrite(_history.SetMetadataValue("nexttrace:last-attempt",now.ToUnixTimeMilliseconds()));
        RequireMetadataWrite(_history.SetMetadataValue("nexttrace:attempt:"+address,now.ToUnixTimeMilliseconds()));
        SetNextTraceState("querying",address);Volatile.Write(ref _activeNextTraceAddress,address);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(policy.RequestTimeout??TimeSpan.FromSeconds(18));
        NodeMetadata result;string outcome;
        try
        {
            string json;
            if(policy.Mode=="v4")
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.nxtrace.org/v4/ipGeo?ip="+Uri.EscapeDataString(address));
                request.Headers.Add("X-NextTrace-Token",options.NextTraceToken);
                using var response=_nextTrace is INextTraceV4Lookup portable
                    ?await portable.LookupV4Async(address,options.NextTraceToken,deadline.Token).ConfigureAwait(false)
                    :await _http.SendAsync(request,deadline.Token).ConfigureAwait(false);
                if(response.Headers.TryGetValues("X-NextTrace-Quota-Expires-At",out var values)&&DateTimeOffset.TryParse(values.FirstOrDefault(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var expiry))
                    RequireMetadataWrite(_history.SetMetadataValue(StrictCredentialKey(options,policy)+":expiry",expiry.ToUnixTimeMilliseconds()));
                if(!response.IsSuccessStatusCode)throw new NextTraceProviderException((int)response.StatusCode,response.Headers.TryGetValues("Retry-After",out var retry)?retry.FirstOrDefault():null);
                json=await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            }
            else json=await _nextTrace.LookupAsync(address,deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            result=ParseNextTrace(address,json,DateTimeOffset.UtcNow,requireIP:policy.Mode=="v4");outcome="success";
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
            SetNextTraceState("canceled",address);throw;
        }
        catch(Exception) when(token.IsCancellationRequested)
        {
            SetNextTraceState("canceled",address);token.ThrowIfCancellationRequested();throw;
        }
        catch(NextTraceProviderException ex) when(ex.StatusCode is 401 or 403)
        {
            RequireMetadataWrite(_history.SetMetadataValue(StrictCredentialKey(options,policy)+":invalid",1));outcome="authentication-blocked";
            result=Failure(address,now,"NextTrace 鉴权失败；请更换令牌或显式重置") with{Expires=now};
        }
        catch(NextTraceProviderException ex) when(ex.StatusCode==429)
        {
            var until=RetryUntil(ex.RetryAfter,DateTimeOffset.UtcNow);RequireMetadataWrite(_history.SetMetadataCooldown(until,"nexttrace"));outcome="rate-limited";
            result=Failure(address,now,"NextTrace 服务限流；保留最近成功结果") with{Expires=until};
        }
        catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException or TimeoutException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            bool timedOut=ex is OperationCanceledException or TimeoutException||deadline.IsCancellationRequested;
            var until=TransientRetry(DateTimeOffset.UtcNow);RequireMetadataWrite(_history.SetMetadataCooldown(until,"nexttrace"));outcome=timedOut?"timeout":"failed";
            result=Failure(address,now,timedOut?"NextTrace 本次查询超时；保留最近成功结果":"NextTrace 查询失败；保留最近成功结果") with{Expires=until};
        }
        finally{Volatile.Write(ref _activeNextTraceAddress,null);}
        // A generation change after the wire response must not publish a completed observation.
        if(token.IsCancellationRequested){SetNextTraceState("canceled",address);token.ThrowIfCancellationRequested();}
        RequireMetadataWrite(_history.SaveNodeMetadata(result with{ProviderId="nexttrace",Expires=result.Success?result.Queried.AddHours(options.RefreshHours):result.Expires,QueryState=result.Success?"NextTrace "+policy.Mode:outcome}));
        if(result.Success)RequireMetadataWrite(_history.SetMetadataValue("nexttrace:last-success",result.Queried.ToUnixTimeMilliseconds()));
        SetNextTraceState(outcome,address);
    }

    public const string NextTraceTokenPage="https://api.nxtrace.org/v4/api-tokens";
    private readonly INextTraceLookup _nextTrace;
    private string _nextTraceStatus="";
    public string NextTraceStatus=>Volatile.Read(ref _nextTraceStatus);
    private static string TokenKey(string value)=>"nt4:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public string DescribeNextTraceToken()
    {
        string value=Options.NextTraceToken;
        if(NextTracePolicy.Mode!="legacy")
        {
            if(NextTracePolicy.Mode=="offline")return "离线模式；不进行定位查询";
            if(NextTracePolicy.Mode=="v3")return "显式 v3 PoW 模式";
            if(value.Length==0)return "v4 令牌未配置；定位查询已阻止";
            if(AuthenticationBlocked(Options,NextTracePolicy,DateTimeOffset.UtcNow))return "v4 鉴权被阻止；请更换令牌或显式重置";
            return "显式 v4 模式；令牌已配置";
        }
        if(value.Length==0)return "未保存 v4 令牌；使用 v3 PoW";
        string key=TokenKey(value);var expiry=_history.MetadataValue(key+":expiry");
        if(_history.MetadataValue(key+":invalid")==1)return "v4 令牌被服务端拒绝；请领取新令牌，当前回退 v3 PoW";
        if(expiry is long stamp)
        {
            var time=DateTimeOffset.FromUnixTimeMilliseconds(stamp);
            return time<=DateTimeOffset.UtcNow?$"v4 已于 {time.ToLocalTime():MM-dd HH:mm} 过期；回退 v3 PoW":$"v4 服务端有效期至 {time.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
        return "v4 令牌已保存，有效期待首次查询确认（领取时间起 7 天）";
    }
    public bool NeedsRefresh(string address,DateTimeOffset now)
    {
        var options=Options;var policy=NextTracePolicy;
        if(LocalLabel(address) is not null||policy.Mode=="offline"||options.Primary=="nexttrace"&&Volatile.Read(ref _metadataPersistenceFailed)!=0||AuthenticationBlocked(options,policy,now))return false;
        address=CidrBlock.Normalize(address);
        if(IsFresh(_history.LoadProviderMetadata(address,options.Primary),options,now))return false;
        if(_history.LoadProviderMetadata(address,options.Primary,true) is {Success:false} failed&&failed.Expires>now)return false;
        return _history.MetadataCooldown(options.Primary)<=now;
    }
    public async Task<int> RefreshRecentAsync(CancellationToken token)
    {
        int refreshed=0;var now=DateTimeOffset.UtcNow;
        // Select all currently active nodes first, so fresh busy nodes cannot starve
        // an older due node behind a fixed result-page limit.
        foreach(var ip in OrderNextTraceCandidates(_history.RecentRouteNodes(now,4096)).Where(ip=>NeedsRefresh(ip,now)))
        {
            token.ThrowIfCancellationRequested();await GetAsync(ip,true,token).ConfigureAwait(false);refreshed++;
        }
        return refreshed;
    }
    private async Task QueryNextTraceAsync(string address,MetadataOptions options,CancellationToken token)
    {
        var now=DateTimeOffset.UtcNow;
        if(IsFresh(_history.LoadProviderMetadata(address,"nexttrace"),options,now))return;
        if(_history.LoadProviderMetadata(address,"nexttrace",true) is {Success:false} failed&&failed.Expires>now)return;
        if(_history.MetadataCooldown("nexttrace")>now)return;
        if(_providerNext.TryGetValue("nexttrace",out var next)&&next>now)await Task.Delay(next-now,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();now=DateTimeOffset.UtcNow;
        if(!_history.ReserveMetadataRequest(now,"nexttrace")){Volatile.Write(ref _nextTraceStatus,"达到本机每日查询预算，保留缓存");return;}
        _providerNext["nexttrace"]=now.AddMilliseconds(300);
        NodeMetadata? result=null;string transport="v3 PoW";
        try
        {
            string value=options.NextTraceToken,key=TokenKey(value);
            var expires=_history.MetadataValue(key+":expiry");
            bool v4=value.Length>0&&_history.MetadataValue(key+":invalid")!=1&&(!expires.HasValue||expires>now.ToUnixTimeMilliseconds())&&_history.MetadataCooldown(key)<=now;
            if(v4)
            {
                Volatile.Write(ref _nextTraceStatus,"正在通过 v4 查询");
                try
                {
                    using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.nxtrace.org/v4/ipGeo?ip="+Uri.EscapeDataString(address));
                    request.Headers.Add("X-NextTrace-Token",value);
                    using var response=_nextTrace is INextTraceV4Lookup portable
                        ?await portable.LookupV4Async(address,value,token).ConfigureAwait(false)
                        :await _http.SendAsync(request,token).ConfigureAwait(false);
                    if(response.Headers.TryGetValues("X-NextTrace-Quota-Expires-At",out var values)&&DateTimeOffset.TryParse(values.FirstOrDefault(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var expiry))
                        _history.SetMetadataValue(key+":expiry",expiry.ToUnixTimeMilliseconds());
                    if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        _history.SetMetadataValue(key+":invalid",1);
                    else if(response.StatusCode==HttpStatusCode.TooManyRequests)
                    {
                        var retry=response.Headers.RetryAfter;var until=retry?.Date??now+(retry?.Delta??TimeSpan.FromHours(1));
                        until=until<now.AddMinutes(1)?now.AddMinutes(1):until>now.AddDays(7)?now.AddDays(7):until;
                        // Honor service rate limits across both transports. Do not use PoW to
                        // evade an explicit v4 429 response on the same service.
                        _history.SetMetadataCooldown(until,"nexttrace");
                        result=Failure(address,now,$"NextTrace 限流，暂停至 {until.ToLocalTime():MM-dd HH:mm}") with{Expires=until};
                    }
                    else if(response.IsSuccessStatusCode)
                    {
                        result=ParseNextTrace(address,await response.Content.ReadAsStringAsync(token).ConfigureAwait(false),now,requireIP:true);transport="v4";
                    }
                    else _history.SetMetadataCooldown(now.AddMinutes(5),key);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException)
                {_history.SetMetadataCooldown(now.AddMinutes(5),key);}
            }
            if(result is null)
            {
                Volatile.Write(ref _nextTraceStatus,"正在连接 / 查询 v3 PoW");
                string json=await _nextTrace.LookupAsync(address,token).ConfigureAwait(false);
                result=ParseNextTrace(address,json,DateTimeOffset.UtcNow,requireIP:false);
            }
            Volatile.Write(ref _nextTraceStatus,result.Success?$"最近使用 {transport} · {DateTime.Now:HH:mm}":result.Message);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
            // Caller/generation cancellation is not a provider failure and never applies cooldown.
            Volatile.Write(ref _nextTraceStatus,"定位已取消或达到本轮时间上限；保留缓存");throw;
        }
        catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException or TimeoutException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _history.SetMetadataCooldown(DateTimeOffset.UtcNow.AddMinutes(2),"nexttrace");
            result=Failure(address,DateTimeOffset.UtcNow,"NextTrace 查询失败或超时；保留最近成功结果");
            Volatile.Write(ref _nextTraceStatus,result.Message);
        }
        _history.SaveNodeMetadata(result! with{ProviderId="nexttrace",Expires=result!.Success?result.Queried.AddHours(options.RefreshHours):result.Expires,QueryState=result.Success?"NextTrace "+transport:""});
    }
    public static NodeMetadata ParseNextTrace(string address,string json,DateTimeOffset now,bool requireIP=true)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        static string Text(JsonElement e,string name)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var p)?
            new string((p.ValueKind==JsonValueKind.String?p.GetString()??"":p.ValueKind==JsonValueKind.Number?p.GetRawText():"").Where(c=>!char.IsControl(c)).Take(160).ToArray()).Trim():"";
        string returned=Text(root,"ip");
        if((requireIP||returned.Length>0)&&(!IPAddress.TryParse(returned,out var ip)||CidrBlock.Normalize(ip.ToString())!=CidrBlock.Normalize(address)))throw new JsonException("定位响应 IP 不一致");
        string number=Text(root,"asnumber");if(number.StartsWith("AS",StringComparison.OrdinalIgnoreCase))number=number[2..];
        long? asn=long.TryParse(number,out var value)&&value>0&&value<=uint.MaxValue?value:null;
        string country=Text(root,"country"),region=Text(root,"prov"),city=Text(root,"city"),owner=Text(root,"owner"),domain=Text(root,"domain");
        if(country.Length==0&&region.Length==0&&city.Length==0&&asn is null)throw new JsonException("定位响应没有可用字段");
        return new(address,true,asn,Text(root,"isp"),owner.Length>0?owner:domain,country,region,city,now,now.AddDays(1),"")
        {ProviderId="nexttrace",NetworkName=Text(root,"whois")};
    }
}

