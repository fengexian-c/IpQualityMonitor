namespace TcpLatencyMonitor.Core;

public sealed record Detection(string Kind,string Detail,bool Diagnose=false);
public sealed record DetectionOptions(int Failures=3,int Recoveries=3,double IncreaseMs=50,double Factor=1.5,int SustainSeconds=30);
public sealed class IncidentDetector(TimeSpan interval,DetectionOptions? options=null)
{
    private readonly DetectionOptions _rules=options??new();
    private readonly Queue<Sample> _recent=new();
    private Sample? _previous;
    private int _failed,_recovered;
    private bool _verified,_active,_unverifiedReported,_slow;
    private DateTimeOffset? _firstFailure,_slowSince,_normalSince;
    private double? _frozen;
    public IReadOnlyList<Detection> Observe(Sample sample)
    {
        var events=new List<Detection>();
        if(_previous is not null&&(sample.Context!=_previous.Context||sample.Time-_previous.Time>interval+TimeSpan.FromSeconds(8)))
        {Reset();events.Add(new("Gap","采样空档或环境改变，连续状态已重置"));}
        _previous=sample;
        if(sample.Status==ProbeStatus.LocalError){_failed=_recovered=0;_firstFailure=null;_slowSince=_normalSince=null;return events;}
        if(sample.Status==ProbeStatus.Success)
        {
            _verified=true;_failed=0;
            if(_active&&++_recovered>=_rules.Recoveries){events.Add(new("Recovered",$"连续 {_rules.Recoveries} 次成功；首次异常样本 {_firstFailure:O}，恢复确认 {sample.Time:O}（采样观测区间）",true));_active=false;_firstFailure=null;}
            else if(!_active)_firstFailure=null;
        }
        else
        {
            _failed++;_recovered=0;_firstFailure??=sample.Time;_slowSince=_normalSince=null;
            if(_failed>=_rules.Failures&&!_verified&&!_unverifiedReported){events.Add(new("Unverified","连续探测无成功结果，可达性尚未验证；不能据此判断目标离线",true));_unverifiedReported=true;}
            if(_failed>=_rules.Failures&&_verified&&!_active){_active=true;events.Add(new("Unavailable",$"连续 {_failed} 次探测失败；首次异常样本 {_firstFailure:O}",true));}
        }
        _recent.Enqueue(sample);while(_recent.Count>0&&sample.Time-_recent.Peek().Time>TimeSpan.FromMinutes(35))_recent.Dequeue();
        if(sample.Status!=ProbeStatus.Success)return events;
        var baseline=_recent.Where(s=>s.Status==ProbeStatus.Success&&s.Time<sample.Time.AddMinutes(-5)).Select(s=>s.LatencyMs!.Value).ToArray();
        var current=_recent.Where(s=>s.Status==ProbeStatus.Success&&s.Time>=sample.Time.AddMinutes(-1)).Select(s=>s.LatencyMs!.Value).ToArray();
        if(current.Length<Math.Max(3,(int)Math.Ceiling(60/Math.Max(1,interval.TotalSeconds)*.75))||(!_slow&&baseline.Length<60))return events;
        var b=_frozen??Median(baseline);var m=Median(current);double limit=Math.Max(b*_rules.Factor,b+_rules.IncreaseMs);
        if(!_slow)
        {
            if(m>limit){_slowSince??=sample.Time;if(sample.Time-_slowSince>=TimeSpan.FromSeconds(_rules.SustainSeconds)){_slow=true;_frozen=b;events.Add(new("LatencyHigh",$"近 1 分钟中位数 {m:F1} ms 持续高于 {limit:F1} ms；基线 {b:F1} ms，持续 {_rules.SustainSeconds} 秒",true));}}
            else _slowSince=null;
        }
        else
        {
            if(m<=Math.Max(b*(1+_rules.Factor)/2,b+_rules.IncreaseMs/2)){_normalSince??=sample.Time;if(sample.Time-_normalSince>=TimeSpan.FromSeconds(_rules.SustainSeconds)){events.Add(new("LatencyRecovered",$"延迟恢复并保持 {_rules.SustainSeconds} 秒；中位数 {m:F1} ms",true));_slow=false;_frozen=null;_normalSince=_slowSince=null;}}
            else _normalSince=null;
        }
        return events;
    }
    public void Reset(){_recent.Clear();_previous=null;_failed=_recovered=0;_verified=_active=_unverifiedReported=_slow=false;_firstFailure=_slowSince=_normalSince=null;_frozen=null;}
    private static double Median(double[] values){Array.Sort(values);int n=values.Length;return n==0?0:n%2==1?values[n/2]:(values[n/2-1]+values[n/2])/2;}
    public static string Label(string kind)=>kind switch
    {
        "Started"=>"开始监控","Stopped"=>"停止监控","Unavailable"=>"连续探测异常","Recovered"=>"探测恢复","Unverified"=>"可达性未验证",
        "LatencyHigh"=>"延迟持续升高","LatencyRecovered"=>"延迟恢复","RouteCandidate"=>"路径变化待确认","RouteChanged"=>"观察到持续路径变化",
        "RouteVisibility"=>"路由可见性变化","NetworkChanged"=>"系统选路变化","Suspend"=>"系统挂起","Resume"=>"系统恢复","Gap"=>"采样空档",
        "RouteFinished"=>"路由检查完成","RouteError"=>"路由检查错误",_=>kind
    };
}
