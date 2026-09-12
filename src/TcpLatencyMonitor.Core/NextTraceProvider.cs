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

public sealed partial class NodeMetadataClient
{
    public const string NextTraceTokenPage="https://api.nxtrace.org/v4/api-tokens";
    private readonly INextTraceLookup _nextTrace;
    private string _nextTraceStatus="";
    public string NextTraceStatus=>Volatile.Read(ref _nextTraceStatus);
    private static string TokenKey(string value)=>"nt4:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public string DescribeNextTraceToken()
    {
        string value=Options.NextTraceToken;
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
        var options=Options;
        if(IsFresh(_history.LoadProviderMetadata(address,options.Primary),options,now))return false;
        if(_history.LoadProviderMetadata(address,options.Primary,true) is {Success:false} failed&&failed.Expires>now)return false;
        return _history.MetadataCooldown(options.Primary)<=now;
    }
    public async Task<int> RefreshRecentAsync(CancellationToken token)
    {
        int refreshed=0;var now=DateTimeOffset.UtcNow;
        // Select all currently active nodes first, so fresh busy nodes cannot starve
        // an older due node behind a fixed result-page limit.
        foreach(var ip in _history.DueRouteNodes(now,Options).Where(ip=>NeedsRefresh(ip,now)))
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
            // Keep the last successful observation; short backoff prevents repeated PoW
            // launches after a route budget expires. The caller still receives cancellation.
            _history.SetMetadataCooldown(DateTimeOffset.UtcNow.AddMinutes(1),"nexttrace");
            Volatile.Write(ref _nextTraceStatus,"定位已取消或达到本轮时间上限；保留缓存");throw;
        }
        catch(Exception ex) when(ex is IOException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
