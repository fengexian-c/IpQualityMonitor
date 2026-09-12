using System.Text.Json.Serialization;

namespace TcpLatencyMonitor.Core;

public sealed record MeasurementRevision(string Address,int Port,string Mode,int TimeoutMilliseconds,DateTimeOffset SavedAt);

/// <summary>Presentation identity is independent of the historical measurement key.</summary>
public class TargetProfile
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Name {get;set;}="";
    public string Address {get;set;}="";
    public string Mode {get;set;}="Icmp";
    public int Port {get;set;}=443;
    public int IntervalSeconds {get;set;}=5;
    public int TimeoutMilliseconds {get;set;}=3000;
    public bool ResumeOnLaunch {get;set;}
    public bool EnableRoutes {get;set;}=true;
    public int RouteMinutes {get;set;}=10;
    public int RouteMaxHops {get;set;}=32;
    public int RouteTimeoutMs {get;set;}=1500;
    public int RouteBudgetSeconds {get;set;}=60;
    public int Failures {get;set;}=3;
    public int Recoveries {get;set;}=3;
    public double LatencyIncreaseMs {get;set;}=50;
    public double LatencyFactor {get;set;}=1.5;
    public int LatencySustainSeconds {get;set;}=30;
    public List<MeasurementRevision> PreviousMeasurements {get;set;}=new();
    [JsonIgnore] public string DisplayName=>string.IsNullOrWhiteSpace(Name)?Address:Name.Trim();
    [JsonIgnore] public Target Primary=>Target.Parse(Address,Port,Mode=="Tcp"?ProbeProtocol.Tcp:ProbeProtocol.Icmp,TimeoutMilliseconds);
    [JsonIgnore] public Target? Secondary=>Mode=="Both"?Target.Parse(Address,Port,ProbeProtocol.Tcp,TimeoutMilliseconds):null;
    public Target? ForProtocol(ProbeProtocol protocol)=>protocol==ProbeProtocol.Icmp?(Mode=="Tcp"?null:Primary):Mode=="Icmp"?null:Secondary??Primary;
    public MonitorOptions Options(bool routes=true)=>new(IntervalSeconds,TimeoutMilliseconds,RouteMinutes,EnableRoutes&&routes,
        new RouteOptions(RouteMaxHops,3,RouteTimeoutMs,RouteBudgetSeconds),
        new DetectionOptions(Failures,Recoveries,LatencyIncreaseMs,LatencyFactor,LatencySustainSeconds));
    public TargetProfile Copy()
    {
        var copy=new TargetProfile();CopyTo(copy);return copy;
    }
    public void CopyTo(TargetProfile copy)
    {
        copy.Id=Id;copy.Name=Name;copy.Address=Address;copy.Mode=Mode;copy.Port=Port;
        copy.IntervalSeconds=IntervalSeconds;copy.TimeoutMilliseconds=TimeoutMilliseconds;copy.ResumeOnLaunch=ResumeOnLaunch;
        copy.EnableRoutes=EnableRoutes;copy.RouteMinutes=RouteMinutes;copy.RouteMaxHops=RouteMaxHops;copy.RouteTimeoutMs=RouteTimeoutMs;copy.RouteBudgetSeconds=RouteBudgetSeconds;
        copy.Failures=Failures;copy.Recoveries=Recoveries;copy.LatencyIncreaseMs=LatencyIncreaseMs;copy.LatencyFactor=LatencyFactor;copy.LatencySustainSeconds=LatencySustainSeconds;
        copy.PreviousMeasurements=new(PreviousMeasurements);
    }
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Id))throw new ArgumentException("目标 ID 不能为空。");
        if(Mode is not ("Icmp" or "Tcp" or "Both"))throw new ArgumentException("未知的检测方式。");
        _=Primary;_=Secondary;
        if(IntervalSeconds is <1 or >3600||TimeoutMilliseconds is <100 or >60000)throw new ArgumentException("采样间隔或超时超出范围。");
        if(RouteMinutes is <1 or >1440||RouteMaxHops is <1 or >64||RouteTimeoutMs is <100 or >5000||RouteBudgetSeconds is <1 or >180)throw new ArgumentException("路由参数超出范围。");
        if(Failures is <2 or >20||Recoveries is <2 or >20||!double.IsFinite(LatencyIncreaseMs)||LatencyIncreaseMs is <1 or >10000||!double.IsFinite(LatencyFactor)||LatencyFactor is <1.1 or >10||LatencySustainSeconds is <10 or >600)throw new ArgumentException("异常判断参数超出范围。");
    }
    public void RetainPrevious(TargetProfile previous)
    {
        PreviousMeasurements=new(previous.PreviousMeasurements);
        if(previous.Primary.Key==Primary.Key&&previous.Secondary?.Key==Secondary?.Key)return;
        var revision=new MeasurementRevision(previous.Address,previous.Port,previous.Mode,previous.TimeoutMilliseconds,DateTimeOffset.UtcNow);
        PreviousMeasurements.RemoveAll(p=>p.Address==revision.Address&&p.Port==revision.Port&&p.Mode==revision.Mode&&p.TimeoutMilliseconds==revision.TimeoutMilliseconds);
        PreviousMeasurements.Insert(0,revision);
    }
}
