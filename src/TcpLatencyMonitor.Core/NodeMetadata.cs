using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed record NodeMetadata(string Address,bool Success,long? Asn,string Isp,string Organization,
    string Country,string Region,string City,DateTimeOffset Queried,DateTimeOffset Expires,string Message)
{
    public const string Provider="ipwho.is";
    // Source-generated record deserialization can supply null for absent init properties in old JSON.
    private string? _providerId;
    public string ProviderId {get=>string.IsNullOrEmpty(_providerId)?Provider:_providerId;init=>_providerId=value;}
    public string Source => ProviderId;
    private string? _countryCode,_regionCode,_networkName,_queryState,_asnSource;
    private List<GeoEvidence>? _sources;
    public string CountryCode {get=>_countryCode??"";init=>_countryCode=value;}
    public string RegionCode {get=>_regionCode??"";init=>_regionCode=value;}
    public string NetworkName {get=>_networkName??"";init=>_networkName=value;}
    public List<GeoEvidence> Sources {get=>_sources??=[];init=>_sources=value;}
    public NodeCalibration? Calibration {get;init;}
    public string QueryState {get=>_queryState??"";init=>_queryState=value;}
    public bool AsnConflict {get;init;}
    public string AsnSource {get=>_asnSource??"";init=>_asnSource=value;}
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description => Success
        ? $"ASN：{(Asn is >0?$"AS{Asn}":"未知")} · 组织：{(Organization.Length>0?Organization:"未知")}\n地区：{(string.Join(" / ",new[]{Country,Region,City}.Where(s=>s.Length>0).Distinct()) is {Length:>0} location?location:"未知")}"+(NetworkName.Length>0?"\n网络名："+NetworkName:"")+(Isp.Length>0&&Isp!=Organization?"\n原始 ISP 字段："+Isp:"")
        : Message;
}

/// <summary>Optional enrichment, independent from all measurement and incident decisions.</summary>
public sealed partial class NodeMetadataClient : IDisposable
{
    private readonly History _history;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _configurationSync=new();

    public NodeMetadataClient(History history,HttpMessageHandler? handler=null,INextTraceLookup? nextTrace=null,NextTraceProviderPolicy? nextTracePolicy=null)
    {
        _history=history;
        _nextTracePolicy=ValidateNextTracePolicy(nextTracePolicy??new());
        _nextTrace=nextTrace??new NextTraceProcessClient(Path.Combine(AppContext.BaseDirectory,"NextTraceGeoHelper.exe"));
        _http=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false})
        {Timeout=TimeSpan.FromSeconds(6),MaxResponseContentBufferSize=65536};
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("IpQualityMonitor/2.6");
    }
    public static string? LocalLabel(string value)
    {
        if(!IPAddress.TryParse(value,out var ip))return "无有效节点 IP";
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
        if(IPAddress.IsLoopback(ip))return "本机回环";
        var b=ip.GetAddressBytes();
        if(ip.AddressFamily==AddressFamily.InterNetwork)
        {
            if(b[0]==10||b[0]==172&&b[1] is >=16 and <=31||b[0]==192&&b[1]==168)return "私网地址";
            if(b[0]==100&&b[1] is >=64 and <=127)return "共享地址（CGNAT）";
            if(b[0]==169&&b[1]==254)return "链路本地地址";
            if(b[0]==0||b[0]>=224||b[0]==192&&b[1]==0&&b[2] is 0 or 2||
                b[0]==192&&b[1]==88&&b[2]==99||b[0]==198&&b[1] is 18 or 19||
                b[0]==198&&b[1]==51&&b[2]==100||b[0]==203&&b[1]==0&&b[2]==113)
                return "特殊或保留地址";
        }
        else
        {
            if(ip.IsIPv6LinkLocal)return "链路本地地址";
            if((b[0]&0xfe)==0xfc)return "IPv6 本地地址";
            // Conservative global-unicast allowlist; exclude transition, documentation and special blocks.
            if((b[0]&0xe0)!=0x20||b[0]==0x20&&b[1]==0x01&&b[2]<2||
                b[0]==0x20&&b[1]==0x01&&b[2]==0x0d&&b[3]==0xb8||
                b[0]==0x20&&b[1]==0x02||b[0]==0x3f&&(b[1]&0xf0)==0xf0)
                return "特殊或保留 IPv6 地址";
        }
        return null;
    }
    private MetadataOptions _options=new();
    public MetadataOptions Options=>Volatile.Read(ref _options);
    public void Configure(MetadataOptions options)
    {
        lock(_configurationSync)ConfigureCore(options,_nextTracePolicy);
    }
    public void Configure(MetadataOptions options,NextTraceProviderPolicy policy)
    {
        lock(_configurationSync)ConfigureCore(options,ValidateNextTracePolicy(policy));
    }
    private void ConfigureCore(MetadataOptions options,NextTraceProviderPolicy policy)
    {
        if(options.Primary is not ("ipwho.is" or "ipinfo-core" or "nexttrace"))throw new ArgumentException("不支持的数据源。");
        if((options.Primary=="ipinfo-core"||options.CrossCheck&&options.Primary=="ipwho.is")&&string.IsNullOrWhiteSpace(options.IpinfoToken))throw new ArgumentException("请先配置 IPinfo 城市接口 Token。");
        if(options.IpinfoToken.Length>1024||options.IpinfoToken.Any(char.IsControl))throw new ArgumentException("Token 格式无效。");
        if(options.NextTraceToken.Length>4096||options.NextTraceToken.Any(char.IsControl))throw new ArgumentException("NextTrace Token 格式无效。");
        if(options.RefreshHours is <1 or >168)throw new ArgumentException("缓存刷新间隔须为 1–168 小时。");
        if(policy.Mode!="legacy"&&options.Primary!="nexttrace")throw new ArgumentException("严格模式仅支持 NextTrace 数据源。");
        if((policy.Mode is "v3" or "offline")&&options.NextTraceToken.Length>0)throw new ArgumentException("仅 v4 模式可以配置 NextTrace Token。");
        _history.MetadataPreference=options.Primary;Volatile.Write(ref _options,options);Volatile.Write(ref _nextTracePolicy,policy);
        Volatile.Write(ref _metadataPersistenceFailed,0);
    }
    public async Task<NodeMetadata> GetAsync(string address,bool allowNetwork,CancellationToken token)
    {
        var now=DateTimeOffset.UtcNow;
        if(LocalLabel(address) is string local)return Failure(address,now,local);
        address=CidrBlock.Normalize(address);
        Interlocked.Increment(ref _pendingMetadata);
        _queuedAddresses.AddOrUpdate(address,1,(_,count)=>count+1);
        bool admitted=false;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);admitted=true;
            if(!allowNetwork)return _history.LoadNodeMetadata(address)??Failure(address,now,"在线注释未开启");
            MetadataOptions options;NextTraceProviderPolicy policy;
            lock(_configurationSync){options=_options;policy=_nextTracePolicy;}
            if(options.Primary=="nexttrace")
            {
                if(policy.Mode=="legacy")await QueryNextTraceAsync(address,options,token).ConfigureAwait(false);
                else await QueryStrictNextTraceAsync(address,options,policy,token).ConfigureAwait(false);
            }
            else await QueryProviderAsync(address,options.Primary,options,token).ConfigureAwait(false);
            var main=_history.LoadProviderMetadata(address,options.Primary);
            string secondary=options.Primary=="ipwho.is"?"ipinfo-core":"ipwho.is";
            bool available=secondary=="ipwho.is"||options.IpinfoToken.Length>0;
            if(policy.Mode=="legacy"&&available&&(options.CrossCheck||options.Primary!="nexttrace"&&(main is not {Success:true}||main.City.Length==0||main.Expires<=DateTimeOffset.UtcNow)))
                await QueryProviderAsync(address,secondary,options,token).ConfigureAwait(false);
            bool combined=_history.RefreshCombinedMetadata(address);
            if(policy.Mode!="legacy")RequireMetadataWrite(combined);
            return _history.LoadNodeMetadata(address)??Failure(address,now,"查询暂无结果");
        }
        finally
        {
            Interlocked.Decrement(ref _pendingMetadata);
            int remaining=_queuedAddresses.AddOrUpdate(address,0,(_,count)=>Math.Max(0,count-1));
            if(remaining==0)((ICollection<KeyValuePair<string,int>>)_queuedAddresses).Remove(new(address,0));
            if(admitted)_gate.Release();
        }
    }
    private readonly Dictionary<string,DateTimeOffset> _providerNext=new();
    private async Task QueryProviderAsync(string address,string provider,MetadataOptions options,CancellationToken token)
    {
        var now=DateTimeOffset.UtcNow;
        var cached=_history.LoadProviderMetadata(address,provider);
        if(IsFresh(cached,options,now))return;
        var last=_history.LoadProviderMetadata(address,provider,true);
        if(last is {Success:false}&&last.Expires>now)return;
        var cooldown=_history.MetadataCooldown(provider);if(cooldown>now)return;
        if(_providerNext.TryGetValue(provider,out var next)&&next>now)await Task.Delay(next-now,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();now=DateTimeOffset.UtcNow;
        if(!_history.ReserveMetadataRequest(now,provider))return;
        _providerNext[provider]=now.AddMilliseconds(1200);
        NodeMetadata result;
        try
        {
            string uri=provider=="ipwho.is"?$"https://ipwho.is/{Uri.EscapeDataString(address)}?fields=ip,success,message,country,country_code,region,region_code,city,connection&lang=zh-CN":$"https://api.ipinfo.io/lookup/{Uri.EscapeDataString(address)}";
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            if(provider=="ipinfo-core")request.Headers.Authorization=new("Bearer",options.IpinfoToken);
            using var response=await _http.SendAsync(request,token).ConfigureAwait(false);
            if(response.StatusCode==HttpStatusCode.TooManyRequests)
            {
                var retry=response.Headers.RetryAfter;var until=retry?.Date??now+(retry?.Delta??TimeSpan.FromHours(1));
                until=until<now.AddMinutes(1)?now.AddMinutes(1):until>now.AddDays(7)?now.AddDays(7):until;
                _history.SetMetadataCooldown(until,provider);result=Failure(address,now,$"服务限流，暂停至 {until.ToLocalTime():MM-dd HH:mm}") with{Expires=until};
            }
            else if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _history.SetMetadataCooldown(now.AddHours(1),provider);result=Failure(address,now,"鉴权失败，请检查 Token 和城市接口权限；暂停 1 小时");
            }
            else
            {
                response.EnsureSuccessStatusCode();string json=await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                result=provider=="ipwho.is"?Parse(address,json,now):ParseIpinfo(address,json,now);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            _history.SetMetadataCooldown(now.AddMinutes(1),provider);result=Failure(address,now,"查询失败（网络、超时或响应异常）；保留最近成功结果");
        }
        _history.SaveNodeMetadata(result with{ProviderId=provider,Expires=result.Success?result.Queried.AddHours(options.RefreshHours):result.Expires});
    }
    public static NodeMetadata ParseIpinfo(string address,string json,DateTimeOffset now)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        static string Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String
            ?new string((v.GetString()??"").Where(c=>!char.IsControl(c)).Take(160).ToArray()).Trim():"";
        if(!IPAddress.TryParse(Text(root,"ip"),out var ip)||CidrBlock.Normalize(ip.ToString())!=CidrBlock.Normalize(address))throw new JsonException("IP mismatch");
        if(!root.TryGetProperty("geo",out var geo)||geo.ValueKind!=JsonValueKind.Object)
            return Failure(address,now,"接口未提供城市定位结构；请检查 IPinfo Core 权限（Lite 不含城市）") with{ProviderId="ipinfo-core"};
        var network=root.TryGetProperty("as",out var a)?a:default;string number=Text(network,"asn");
        long? asn=long.TryParse(number.StartsWith("AS",StringComparison.OrdinalIgnoreCase)?number[2..]:number,out var n)&&n>0&&n<=uint.MaxValue?n:null;
        return new(address,true,asn,Text(network,"name"),Text(network,"name"),Text(geo,"country"),Text(geo,"region"),Text(geo,"city"),now,now.AddDays(1),"")
        {ProviderId="ipinfo-core",CountryCode=Text(geo,"country_code"),RegionCode=Text(geo,"region_code")};
    }
    public static NodeMetadata Parse(string address,string json,DateTimeOffset now)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        string Text(JsonElement element,string key)
        {
            if(element.ValueKind!=JsonValueKind.Object||!element.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)return "";
            return new string((v.GetString()??"").Where(c=>!char.IsControl(c)).Take(160).ToArray()).Trim();
        }
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("success",out var ok)||ok.ValueKind!=JsonValueKind.True)
            return Failure(address,now,"查询服务暂无该 IP 的注释");
        if(!IPAddress.TryParse(Text(root,"ip"),out var returned)||!returned.Equals(IPAddress.Parse(address)))
            throw new JsonException("IP mismatch in metadata response");
        var connection=root.TryGetProperty("connection",out var data)&&data.ValueKind==JsonValueKind.Object?data:default;
        long? asn=connection.ValueKind==JsonValueKind.Object&&connection.TryGetProperty("asn",out var a)&&a.ValueKind==JsonValueKind.Number&&a.TryGetInt64(out var n)&&n>0&&n<=uint.MaxValue?n:null;
        return new(address,true,asn,Text(connection,"isp"),Text(connection,"org"),Text(root,"country"),Text(root,"region"),Text(root,"city"),now,now.AddDays(1),""){CountryCode=Text(root,"country_code"),RegionCode=Text(root,"region_code")};
    }
    private static NodeMetadata Failure(string ip,DateTimeOffset now,string message)=>new(ip,false,null,"","","","","",now,now.AddMinutes(10),message);
    public static bool IsFresh(NodeMetadata? value,MetadataOptions options,DateTimeOffset now)=>value is {Success:true}&&value.Expires>now&&value.Queried.AddHours(options.RefreshHours)>now;
    public void Dispose(){_http.Dispose();_nextTrace.Dispose();} // The gate may still be released by a cancelled UI request.
}

