using System.Net;
using System.Net.Sockets;

namespace TcpLatencyMonitor.Core;

public sealed record PrefixRule(string Id,string Cidr,long NetworkAsn,string Name,string Kind,string RegistryName,string Source,DateTimeOffset Verified);

/// <summary>Parsed once when loading rules; queries never perform I/O.</summary>
public sealed class CidrBlock
{
    private readonly byte[] _network;
    public int Length {get;}
    private CidrBlock(byte[] network,int length){_network=network;Length=length;}
    public static string Normalize(string address)
    {
        var ip=IPAddress.Parse(address);return (ip.IsIPv4MappedToIPv6?ip.MapToIPv4():ip).ToString();
    }
    public static CidrBlock Parse(string value)
    {
        var parts=value.Split('/');
        if(parts.Length!=2||!IPAddress.TryParse(parts[0],out var ip)||ip.IsIPv4MappedToIPv6||
            !int.TryParse(parts[1],out int bits)||bits<0||bits>(ip.AddressFamily==AddressFamily.InterNetwork?32:128))
            throw new InvalidDataException("Invalid CIDR: "+value);
        var bytes=ip.GetAddressBytes();
        for(int i=0;i<bytes.Length;i++)
        {
            int keep=Math.Clamp(bits-i*8,0,8);int mask=keep==0?0:255<<(8-keep)&255;
            if((bytes[i]&mask)!=bytes[i])throw new InvalidDataException("CIDR contains host bits: "+value);
        }
        return new(bytes,bits);
    }
    public bool Contains(string address)
    {
        if(!IPAddress.TryParse(address,out var ip))return false;if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();
        var bytes=ip.GetAddressBytes();if(bytes.Length!=_network.Length)return false;
        for(int i=0;i<bytes.Length;i++)
        {
            int keep=Math.Clamp(Length-i*8,0,8);int mask=keep==0?0:255<<(8-keep)&255;
            if((bytes[i]&mask)!=_network[i])return false;
        }
        return true;
    }
}

public sealed class PrefixCatalog
{
    private readonly (PrefixRule Rule,CidrBlock Block)[] _rules;
    public PrefixCatalog(IEnumerable<PrefixRule> rules)
    {
        _rules=rules.Select(r=>(Rule:r,Block:CidrBlock.Parse(r.Cidr))).OrderByDescending(r=>r.Block.Length).ToArray();
        if(_rules.Select(r=>r.Rule.Id).Distinct().Count()!=_rules.Length||
            _rules.Select(r=>CidrBlock.Normalize(r.Rule.Cidr.Split('/')[0])+"/"+r.Block.Length).Distinct().Count()!=_rules.Length||
            _rules.Any(r=>string.IsNullOrWhiteSpace(r.Rule.Id)||string.IsNullOrWhiteSpace(r.Rule.Name)||r.Rule.NetworkAsn<=0||
                r.Rule.Kind!="registry-allocation"||string.IsNullOrWhiteSpace(r.Rule.RegistryName)||r.Rule.Verified==default||
                !Uri.TryCreate(r.Rule.Source,UriKind.Absolute,out var source)||source.Scheme!="https"))
            throw new InvalidDataException("Invalid or duplicate prefix evidence");
    }
    public PrefixRule? Match(string address)=>_rules.FirstOrDefault(r=>r.Block.Contains(address)).Rule;
}

public sealed record NodeLocation(string Label,string Key,string Level,bool Stale)
{
    public string State {get;init;}="estimated";
    public bool Conflict {get;init;}
    public string Details {get;init;}="";
    public string Marker=>State switch {"manual"=>"校准","conflict"=>"分歧","consistent"=>"一致","unknown"=>"","imported"=>"导入",_=>"估算"};
}
public sealed record NodeIdentity(string? Network,string? NetworkKey,bool Backbone,string Kind,bool Conflict,bool Local,PrefixRule? Prefix,NodeLocation Location);

public static class NodeClassifier
{
    public static NodeLocation Locate(NodeMetadata? data,DateTimeOffset now)
    {
        return GeoResolver.Locate(data,now);
    }
    internal static NodeLocation LegacyLocate(NodeMetadata? data,DateTimeOffset now)
    {
        if(data is not {Success:true})return new("地区未知","","unknown",false);
        string country=data.Country.Trim(),region=data.Region.Trim(),city=data.City.Trim();
        string NormalizeCity(string s)=>s switch {"北京市"=>"北京","上海市"=>"上海","天津市"=>"天津","重庆市"=>"重庆",_=>s};
        city=NormalizeCity(city);region=NormalizeCity(region);
        string level=city.Length>0?"city":region.Length>0?"region":country.Length>0?"country":"unknown";
        string label=level switch {"city"=>city,"region"=>region+"（省区）","country"=>country+"（国家）",_=>"地区未知"};
        bool stale=level!="unknown"&&data.Expires<=now;
        return new(label+(stale?"（旧缓存）":""),level=="unknown"?"":string.Join("|",country,region,city).ToUpperInvariant(),level,stale);
    }
    public static AnnotatedNode Classify(int ttl,string address,bool target,NodeMetadata? data,DateTimeOffset now)
    {
        address=CidrBlock.Normalize(address);
        if(data is not null&&(!IPAddress.TryParse(data.Address,out _)||CidrBlock.Normalize(data.Address)!=address))data=null;
        var location=Locate(data,now);string? local=NodeMetadataClient.LocalLabel(address);
        if(local is not null)return new(ttl,address,target,local,"本地地址规则",null)
            {Identity=new(null,null,false,"local",false,true,null,new("地区未知","","unknown",false))};
        long? asn=data is {Success:true,Asn:>0}?data.Asn:null;
        string? known=BackboneCatalog.Name(asn);var prefix=BackboneCatalog.Prefix(address);
        string name,evidence,kind;string? network=null,key=null;bool conflict=false,backbone=false;
        if(data?.AsnConflict==true)
        {
            name="归属待核对";evidence="数据源 ASN 存在分歧；详见节点依据";kind="conflict";conflict=true;
        }
        else if(known is not null&&prefix is not null&&asn!=prefix.NetworkAsn)
        {
            name="归属待核对";evidence=$"ASN：{known}（AS{asn}）；地址段注册归属：{prefix.Name}";kind="conflict";conflict=true;
        }
        else if(known is not null)
        {
            name=network=known;key="AS"+asn;backbone=true;kind=prefix is null?"asn":"asn+prefix";
            evidence=prefix is null?"ASN 匹配":"ASN 匹配 + 地址段匹配";
        }
        else if(prefix is not null)
        {
            network=prefix.Name;key="AS"+prefix.NetworkAsn;backbone=true;kind=asn is null?"prefix":"prefix-with-asn";
            name=asn is null?prefix.Name:$"AS{asn} · 地址归属 {prefix.Name}";evidence="IP 地址段匹配（注册归属）";
        }
        else if(asn is not null){name=network="AS"+asn;key=network;kind="asn";evidence="ASN 查询结果";}
        else{name="线路未识别";kind="unknown";evidence="归属信息不足";}
        if(data is {Success:true}&&data.Expires<=now)evidence+=" · 查询缓存过期";
        return new(ttl,address,target,name,evidence,data){Identity=new(network,key,backbone,kind,conflict,false,prefix,location)};
    }
}
