namespace TcpLatencyMonitor.Core;

/// <summary>Canonical monitoring policy shared by every active and future target.</summary>
public sealed record GlobalMonitorSettings(int IntervalSeconds=5,int TimeoutMilliseconds=3000,
    bool EnableRoutes=true,int RouteMinutes=10,int RouteMaxHops=32,int RouteTimeoutMs=1500,int RouteBudgetSeconds=60)
{
    public static GlobalMonitorSettings From(TargetProfile p)=>new(p.IntervalSeconds,p.TimeoutMilliseconds,p.EnableRoutes,p.RouteMinutes,p.RouteMaxHops,p.RouteTimeoutMs,p.RouteBudgetSeconds);
    public void Validate()
    {
        if(IntervalSeconds is <1 or >3600||TimeoutMilliseconds is <100 or >60000)throw new ArgumentException("采样间隔或超时超出范围。");
        if(RouteMinutes is <1 or >1440||RouteMaxHops is <1 or >64||RouteTimeoutMs is <100 or >5000||RouteBudgetSeconds is <1 or >180)throw new ArgumentException("路由参数超出范围。");
    }
    public void Apply(TargetProfile p,bool retainHistory=true)
    {
        Validate();var old=p.Copy();
        p.IntervalSeconds=IntervalSeconds;p.TimeoutMilliseconds=TimeoutMilliseconds;p.EnableRoutes=EnableRoutes;
        p.RouteMinutes=RouteMinutes;p.RouteMaxHops=RouteMaxHops;p.RouteTimeoutMs=RouteTimeoutMs;p.RouteBudgetSeconds=RouteBudgetSeconds;
        if(retainHistory&&!string.IsNullOrWhiteSpace(p.Address))p.RetainPrevious(old);
    }
}
