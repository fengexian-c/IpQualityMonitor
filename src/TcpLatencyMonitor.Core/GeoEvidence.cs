using System.Globalization;
using System.Text;

namespace TcpLatencyMonitor.Core;

public sealed record GeoEvidence(string Provider,string Country,string Region,string City,string CountryCode,string RegionCode,
    long? Asn,string Organization,string NetworkName,DateTimeOffset Queried,DateTimeOffset Expires,string QueryState)
{
    public static GeoEvidence From(NodeMetadata m)=>new(m.Source,m.Country,m.Region,m.City,m.CountryCode,m.RegionCode,m.Asn,m.Organization,m.NetworkName,m.Queried,m.Expires,m.QueryState);
}
public sealed record NodeCalibration(string Address,string Country,string Region,string City,string Note,DateTimeOffset Created,DateTimeOffset Expires,string? ImportId=null);
public sealed record MetadataOptions(string Primary="ipwho.is",bool CrossCheck=false,string IpinfoToken="",string NextTraceToken="",int RefreshHours=24);

public static class GeoResolver
{
    public static string Country(string value)=>value.Trim().ToUpperInvariant() switch
    {
        "US" or "USA" or "UNITED STATES" or "UNITED STATES OF AMERICA" or "美国" or "美國"=>"US",
        "CN" or "CHINA" or "中国" or "中國"=>"CN",
        "HK" or "HONG KONG" or "香港"=>"HK",
        "TW" or "TAIWAN" or "台湾" or "台灣"=>"TW",
        "JP" or "JAPAN" or "日本"=>"JP",
        "CR" or "COSTA RICA" or "哥斯达黎加" or "哥斯大黎加"=>"CR",
        _=>value.Trim().ToUpperInvariant()
    };
    public static string Region(string country,string value)
    {
        string v=value.Trim();
        if(country=="US")return v.ToUpperInvariant() switch
        {"CA" or "US-CA" or "CALIFORNIA" or "加州" or "加利福尼亚" or "加利福尼亚州" or "加利福尼亞州"=>"CA",
         "DC" or "US-DC" or "DISTRICT OF COLUMBIA" or "哥伦比亚特区" or "哥倫比亞特區" or "華盛頓哥倫比亞特區"=>"DC",_=>v.ToUpperInvariant()};
        if(country=="CN")return v.ToUpperInvariant() switch
        {"GD" or "CN-GD" or "CN-44" or "广东" or "广东省" or "廣東省" or "GUANGDONG"=>"广东省",
         "BJ" or "CN-BJ" or "CN-11" or "BEIJING" or "北京市" or "北京"=>"北京",
         "SH" or "CN-SH" or "CN-31" or "SHANGHAI" or "上海市" or "上海"=>"上海",
         "TJ" or "CN-TJ" or "CN-12" or "TIANJIN" or "天津市" or "天津"=>"天津",
         "CQ" or "CN-CQ" or "CN-50" or "CHONGQING" or "重庆市" or "重庆"=>"重庆",_=>v};
        return v.ToUpperInvariant();
    }
    public static string City(string country,string region,string value)
    {
        string v=value.Trim();
        if(country=="US"&&region=="CA")return v.ToUpperInvariant() switch
        {"SAN JOSE" or "SAN JOSÉ" or "圣何西" or "聖何西" or "圣何塞" or "聖何塞"=>"圣何塞",
         "FREMONT" or "弗里蒙特"=>"弗里蒙特","LOS ANGELES" or "洛杉矶" or "洛杉磯"=>"洛杉矶",_=>v};
        if(country=="CN")return v.ToUpperInvariant() switch {"BEIJING" or "北京市"=>"北京","SHANGHAI" or "上海市"=>"上海","TIANJIN" or "天津市"=>"天津","CHONGQING" or "重庆市"=>"重庆",
            "GUANGZHOU" or "广州市" or "廣州"=>"广州","SHENZHEN" or "深圳市"=>"深圳","DONGGUAN" or "东莞市" or "東莞"=>"东莞","HUIZHOU" or "惠州市"=>"惠州",_=>v};
        return v;
    }
    public static string CountryLabel(string code,string fallback)=>code switch {"US"=>"美国","CN"=>"中国","JP"=>"日本","CR"=>"哥斯达黎加","HK"=>"香港","TW"=>"台湾",_=>fallback};
    public static string RegionLabel(string country,string region)=>country=="US"?region switch {"CA"=>"加州","DC"=>"华盛顿哥伦比亚特区",_=>region}:region;
    private sealed record Place(string Country,string Region,string City,GeoEvidence Evidence);
    private static Place Normalize(GeoEvidence e)
    {
        string country=Country(e.CountryCode.Length>0?e.CountryCode:e.Country);
        string region=Region(country,e.RegionCode.Length>0?e.RegionCode:e.Region);
        return new(country,region,City(country,region,e.City),e);
    }
    public static NodeMetadata? Combine(string ip,IEnumerable<NodeMetadata> observations,NodeCalibration? calibration,string preferred,DateTimeOffset now)
    {
        var all=observations.Where(m=>m.Success).OrderBy(m=>m.Source==preferred?0:1).ThenByDescending(m=>m.Queried).ToArray();
        if(all.Length==0&&calibration is null)return null;
        var usable=all.Where(m=>m.Expires>now).ToArray();if(usable.Length==0)usable=all;
        var primary=usable.FirstOrDefault()??new NodeMetadata(ip,true,null,"","","","","",calibration!.Created,calibration.Expires,""){ProviderId="local-calibration"};
        var network=usable.FirstOrDefault(m=>m.Asn is >0);
        return primary with{Address=ip,Asn=network?.Asn,AsnSource=network?.Source??"",AsnConflict=usable.Where(m=>m.Asn is >0).Select(m=>m.Asn).Distinct().Count()>1,
            Sources=all.Select(GeoEvidence.From).ToList(),Calibration=calibration};
    }
    public static NodeLocation Locate(NodeMetadata? data,DateTimeOffset now)
    {
        if(data is not {Success:true})return new("地区未知","","unknown",false){State="unknown"};
        var evidence=data.Sources.Count>0?data.Sources:data.Source=="local-calibration"?new List<GeoEvidence>():new List<GeoEvidence>{GeoEvidence.From(data)};
        var details=string.Join("\n",evidence.Select(e=>$"{e.Provider}：{string.Join(" / ",new[]{e.Country,e.Region,e.City}.Where(s=>s.Length>0))} · {(e.Asn is >0?$"AS{e.Asn}":"ASN 未知")}\n查询 {e.Queried.ToLocalTime():yyyy-MM-dd HH:mm} · 有效至 {e.Expires.ToLocalTime():yyyy-MM-dd HH:mm}{(e.Expires<=now?"（过期）":"")}{(e.QueryState.Length>0?" · "+e.QueryState:"")}"));
        var active=evidence.Where(e=>e.Expires>now).ToArray();bool stale=active.Length==0;if(stale)active=evidence.ToArray();
        var places=active.Select(Normalize).Where(p=>p.Country.Length>0||p.Region.Length>0||p.City.Length>0).ToArray();
        if(data.Calibration is {} manual)
        {
            details+=$"\n手动校准：{manual.Country} / {manual.Region} / {manual.City}\n依据：{manual.Note} · {manual.Created.ToLocalTime():yyyy-MM-dd HH:mm} · 复核期限 {manual.Expires.ToLocalTime():yyyy-MM-dd HH:mm}{(manual.ImportId is not null?" · NextTrace 导入":"")}";
            if(manual.Expires>now)
            {
                string c=Country(manual.Country),r=Region(c,manual.Region),city=City(c,r,manual.City);
                return Make(c,r,city,manual.Country,false,"manual",false,details);
            }
            details+="\n校准已过复核期限，当前恢复采用数据源结果。";
        }
        if(places.Length==0)return new("地区未知","","unknown",false){State="unknown",Details=details};
        // A chosen NextTrace observation provides the displayed location; retain other
        // providers in the evidence panel instead of discarding a usable city on disagreement.
        var nextTrace=places.FirstOrDefault(p=>p.Evidence.Provider=="nexttrace");
        if(data.Source=="nexttrace"&&nextTrace is not null)
            return Make(nextTrace.Country,nextTrace.Region,nextTrace.City,nextTrace.Evidence.Country,stale,"estimated",false,details+"\n当前采用 NextTrace；其他来源保留供核对。");
        bool Different(IEnumerable<string> values)=>values.Where(v=>v.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        // Missing hierarchy fields are not disagreement, but never graft a child onto another source's parent.
        bool countryConflict=Different(places.Select(p=>p.Country));
        bool regionConflict=places.Where(p=>p.Country.Length>0&&p.Region.Length>0).GroupBy(p=>p.Country).Any(g=>Different(g.Select(p=>p.Region)));
        bool cityConflict=places.Where(p=>p.Country.Length>0&&p.City.Length>0).GroupBy(p=>p.Country).Any(g=>Different(g.Select(p=>p.City)));
        var chosen=places.OrderByDescending(p=>p.City.Length>0?3:p.Region.Length>0?2:1).First();
        if(countryConflict)return new("地区待核对","","unknown",stale){State="conflict",Conflict=true,Details=details};
        if(regionConflict)return Make(chosen.Country,"","",chosen.Evidence.Country,stale,"conflict",true,details);
        if(cityConflict)
        {
            var conflicting=places.Where(p=>p.Country==chosen.Country&&p.City.Length>0).ToArray();
            string commonRegion=conflicting.All(p=>p.Region.Length>0)&&!Different(conflicting.Select(p=>p.Region))?chosen.Region:"";
            return Make(chosen.Country,commonRegion,"",chosen.Evidence.Country,stale,"conflict",true,details);
        }
        int matches=places.Count(p=>p.Country.Length>0&&p.Region.Length>0&&p.City.Length>0&&p.Country==chosen.Country&&p.Region==chosen.Region&&p.City.Equals(chosen.City,StringComparison.OrdinalIgnoreCase));
        return Make(chosen.Country,chosen.Region,chosen.City,chosen.Evidence.Country,stale,matches>1?"consistent":"estimated",false,details);
    }
    private static NodeLocation Make(string country,string region,string city,string countryFallback,bool stale,string state,bool conflict,string details)
    {
        string level=city.Length>0?"city":region.Length>0?"region":country.Length>0?"country":"unknown";
        string label=level switch {"city"=>city,"region"=>RegionLabel(country,region)+"（省区）","country"=>CountryLabel(country,countryFallback)+"（国家）",_=>"地区未知"};
        if(conflict)label=CountryLabel(country,countryFallback)+(region.Length>0?" · "+RegionLabel(country,region)+"（城市待核对）":"（地区待核对）");
        return new(label+(stale?"（旧缓存）":""),string.Join("|",country,region,city).ToUpperInvariant(),level,stale){State=state,Conflict=conflict,Details=details};
    }
}
