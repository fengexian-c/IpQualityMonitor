using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TcpLatencyMonitor.Core;

public sealed record ImportedHop(int Ttl,string? Address,bool Reply,double? RttMs,NodeMetadata? Metadata);
public sealed record NextTraceImport(string Id,DateTimeOffset Imported,string FileName,string Sha256,string Note,List<ImportedHop> Hops)
{
    public DateTimeOffset? MeasuredAt {get;init;}
    public string StopReason {get;init;}="";
    public string Description=>$"{FileName} · 导入 {Imported.ToLocalTime():MM-dd HH:mm} · {Hops.Select(h=>h.Ttl).Distinct().Count()} 跳 · 测量时间{(MeasuredAt is {} t?t.ToLocalTime().ToString("yyyy-MM-dd HH:mm"):"未记录")}";
    public List<NodeMetadata> Candidates=>Hops.Where(h=>h.Metadata is not null).Select(h=>h.Metadata!).GroupBy(m=>m.Address)
        .Where(g=>g.Select(m=>(GeoResolver.Country(m.Country),m.Region,m.City)).Distinct().Count()==1)
        .Select(g=>g.First()).Where(m=>m.Country.Length>0&&m.City.Length>0).ToList();
}
public static class NextTraceImporter
{
    public const int MaxBytes=2*1024*1024;
    public static NextTraceImport Parse(string json,string fileName,DateTimeOffset imported)
    {
        if(Encoding.UTF8.GetByteCount(json)>MaxBytes)throw new InvalidDataException("JSON 超过 2 MB 上限。");
        using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=32});var root=doc.RootElement;
        static JsonElement Field(JsonElement value,string key)
        {
            if(value.ValueKind==JsonValueKind.Object)foreach(var property in value.EnumerateObject())if(property.Name.Equals(key,StringComparison.OrdinalIgnoreCase))return property.Value;
            return default;
        }
        static string Text(JsonElement value)=>value.ValueKind==JsonValueKind.String?new string((value.GetString()??"").Where(c=>!char.IsControl(c)).Take(160).ToArray()).Trim():"";
        var groups=Field(root,"Hops");
        if(groups.ValueKind!=JsonValueKind.Array||groups.GetArrayLength()>64)throw new InvalidDataException("需要 NextTrace --json 生成的 Hops 二维数组（最多 64 跳）。");
        var hops=new List<ImportedHop>();int index=0;
        foreach(var group in groups.EnumerateArray())
        {
            index++;
            if(group.ValueKind!=JsonValueKind.Array||group.GetArrayLength()>100)throw new InvalidDataException("每跳最多导入 100 次探测。");
            if(group.GetArrayLength()==0){hops.Add(new(index,null,false,null,null));continue;}
            foreach(var item in group.EnumerateArray())
            {
                if(item.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("探测条目必须是 JSON 对象。");
                var ttlField=Field(item,"TTL");int ttl=ttlField.ValueKind==JsonValueKind.Number&&ttlField.TryGetInt32(out var t)&&t>0?t:index;
                if(ttl is <1 or >64)throw new InvalidDataException("无效 TTL。");
                var addressField=Field(item,"Address");string address=Text(addressField);
                if(addressField.ValueKind==JsonValueKind.Object)address=Text(Field(addressField,"IP"));
                string? ip=null;if(address.Length>0){if(!IPAddress.TryParse(address,out var parsed))throw new InvalidDataException("响应 IP 格式无效。");ip=CidrBlock.Normalize(parsed.ToString());}
                bool reply=Field(item,"Success").ValueKind==JsonValueKind.True&&ip is not null;
                double? rtt=null;var rttField=Field(item,"RTT");
                // Go time.Duration is serialized as integer nanoseconds, not milliseconds.
                if(reply&&rttField.ValueKind==JsonValueKind.Number&&rttField.TryGetDouble(out var ns)&&double.IsFinite(ns)&&ns>=0&&ns<=600_000_000_000)rtt=ns/1_000_000d;
                NodeMetadata? metadata=null;var geo=Field(item,"Geo");
                string geoSource=Text(Field(geo,"source")),country=Text(Field(geo,"country"));
                if(country.Length==0)country=Text(Field(geo,"country_en"));
                if(reply&&ip is not null&&NodeMetadataClient.LocalLabel(ip) is null&&geo.ValueKind==JsonValueKind.Object&&
                    geoSource is not ("timeout" or "pending")&&country is not ("网络故障" or "Network Error"))
                {
                    string returned=Text(Field(geo,"ip"));
                    if(returned.Length>0&&(!IPAddress.TryParse(returned,out _)||CidrBlock.Normalize(returned)!=ip))throw new InvalidDataException("Geo 注释的 IP 与该跳响应 IP 不一致。");
                    var asnField=Field(geo,"asnumber");string asnText=asnField.ValueKind==JsonValueKind.Number?asnField.GetRawText():Text(asnField);
                    if(asnText.StartsWith("AS",StringComparison.OrdinalIgnoreCase))asnText=asnText[2..];
                    long? asn=long.TryParse(asnText,out var n)&&n>0&&n<=uint.MaxValue?n:null;
                    string region=Text(Field(geo,"prov")),city=Text(Field(geo,"city"));
                    if(region.Length==0)region=Text(Field(geo,"prov_en"));if(city.Length==0)city=Text(Field(geo,"city_en"));
                    metadata=new(ip,true,asn,Text(Field(geo,"isp")),Text(Field(geo,"owner")),country,region,city,imported,imported.AddDays(7),"文件未提供可验证的定位查询时间")
                    {ProviderId="NextTrace 文件"+(geoSource.Length>0?" / "+geoSource:"（数据源未记录）"),NetworkName=Text(Field(geo,"whois"))};
                }
                hops.Add(new(ttl,ip,reply,rtt,metadata));
            }
        }
        if(hops.Count==0)throw new InvalidDataException("JSON 中没有路由探测记录。");
        return new(Guid.NewGuid().ToString("N"),imported,Path.GetFileName(fileName),Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))),
            "用户导入的独立诊断；来源和测量时间未独立核验，RTT 不计入持续监控统计。",hops){StopReason=Text(Field(Field(root,"StopReason"),"reason"))};
    }
}
