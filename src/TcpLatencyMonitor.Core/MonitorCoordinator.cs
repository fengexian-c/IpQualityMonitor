using System.Diagnostics;
using System.Collections.Concurrent;

namespace TcpLatencyMonitor.Core;

public sealed record MonitorOptions(int IntervalSeconds=5,int TimeoutMs=3000,int RouteMinutes=10,bool EnableRoutes=true,RouteOptions? Routes=null,DetectionOptions? Detection=null);
public sealed class MonitorCoordinator(History history,Target target,MonitorOptions options,MonitorResources? shared=null)
{
    private sealed record SampleItem(Sample Sample);
    private readonly MonitorResources _resources=shared??new();
    private readonly ConcurrentQueue<MonitorEvent> _powerEvents=new();
    public event Action? SampleSkipped;
    public event Action<MonitorEvent>? EventSaved;
    private NetworkContext _context=new("unknown","正在读取系统选路信息");
    private string? _routeRequest;
    private int _generation,_suspended,_networkChanging;
    private long _diagnosedAt=-300000;
    private readonly Stopwatch _uptime=Stopwatch.StartNew();
    private readonly object _routeSync=new();
    private CancellationTokenSource? _activeRoute;
    private void CancelRoute(){lock(_routeSync)_activeRoute?.Cancel();}
    public event Action<Sample>? SampleSaved;
    public event Action? RecordsChanged;
    public event Action<RouteRun>? RouteSaved;
    public event Action<string>? RouteStateChanged;
    public event Action<string>? ContextChanged;
    public event Action<string>? DiagnosisRequested;
    public string ContextDescription=>_context.Description;
    public bool RequestRoute(string reason="手动检查")
    { if(!options.EnableRoutes)return false;return Interlocked.CompareExchange(ref _routeRequest,reason,null) is null; }
    public void PowerChanged(bool suspended)
    {
        if(Interlocked.Exchange(ref _suspended,suspended?1:0)==(suspended?1:0))return;
        Interlocked.Increment(ref _generation);
        CancelRoute();
        _powerEvents.Enqueue(Event(suspended?"Suspend":"Resume",suspended?"进入低功耗状态，停止采样":"恢复采样，空档不补记失败"));
        if(!suspended)RequestRoute("系统恢复");
    }
    private MonitorEvent Event(string kind,string detail,string? route=null,string? previous=null)=>new(Guid.NewGuid().ToString("N"),target.Key,DateTimeOffset.UtcNow,kind,detail,route,previous);
    public async Task RunAsync(CancellationToken token)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);var ct=lifetime.Token;
        try
        {
            _context=_resources.ReadContext(target.Address);ContextChanged?.Invoke(_context.Description);
            var rules=options.Detection??new();
            await PersistAsync(Event("Started",$"{target.Label} · 间隔 {options.IntervalSeconds}s · 超时 {options.TimeoutMs}ms\n失败 {rules.Failures} 次 / 恢复 {rules.Recoveries} 次；延迟增幅 {rules.IncreaseMs}ms、倍数 {rules.Factor}、持续 {rules.SustainSeconds}s\n{_context.Description}"),ct);
            RequestRoute("开始监控");
            async Task Guard(Func<CancellationToken,Task> task)
            {try{await task(ct).ConfigureAwait(false);}catch{lifetime.Cancel();throw;}}
            await Task.WhenAll(Guard(SampleAsync),Guard(RoutesAsync),Guard(ContextAsync)).ConfigureAwait(false);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        finally
        {
            lifetime.Cancel();
            if(history.WriteFailure is null)await PersistAsync(Event("Stopped","监控结束，未监测期间不补造结果"),CancellationToken.None).ConfigureAwait(false);
            if(shared is null)await _resources.DisposeAsync();
        }
    }
    private async Task PersistAsync(object item,CancellationToken token)
    {
        // Once accepted, a write is drained even when this target is stopped.
        await history.RecordAsync(()=>
        {
            switch(item)
            {
                case SampleItem sample:history.Add(target,sample.Sample);break;
                case RouteRun run:history.SaveRoute(run);break;
                case MonitorEvent ev:history.AddEvent(ev);break;
            }
        }).ConfigureAwait(false);
        switch(item)
        {
            case SampleItem sample:SampleSaved?.Invoke(sample.Sample);break;
            case RouteRun run:RouteSaved?.Invoke(run);RecordsChanged?.Invoke();break;
            case MonitorEvent ev:EventSaved?.Invoke(ev);RecordsChanged?.Invoke();break;
        }
    }
    private async Task SampleAsync(CancellationToken token)
    {
        IProbe probe=_resources.ProbeFactory(target);
        using var probeLifetime=probe as IDisposable;
        var detector=new IncidentDetector(TimeSpan.FromSeconds(options.IntervalSeconds),options.Detection);int generation=_generation;
        DateTimeOffset? previous=null;long lastBusyEvent=-60000;
        await Task.Delay(_resources.StartDelay(options.IntervalSeconds),token);
        while(!token.IsCancellationRequested)
        {
            if(Volatile.Read(ref _suspended)!=0||Volatile.Read(ref _networkChanging)!=0){await Task.Delay(500,token);continue;}
            if(generation!=_generation){detector.Reset();generation=_generation;previous=null;}
            var clock=Stopwatch.StartNew();
            if(!await _resources.ProbeSlots.WaitAsync(TimeSpan.FromSeconds(options.IntervalSeconds),token).ConfigureAwait(false))
            {
                detector.Reset();previous=null;SampleSkipped?.Invoke();
                if(_uptime.ElapsedMilliseconds-lastBusyEvent>=60000){lastBusyEvent=_uptime.ElapsedMilliseconds;await PersistAsync(Event("Gap","本机探测并发已满，本次未执行；不计入目标失败"),token);}
                continue;
            }
            var env=_context;int before=_generation;Sample sample;
            try
            {
                if(Volatile.Read(ref _suspended)!=0||Volatile.Read(ref _networkChanging)!=0)continue;
                sample=await probe.RunAsync(target,TimeSpan.FromMilliseconds(options.TimeoutMs),token).ConfigureAwait(false);
            }
            finally{_resources.ProbeSlots.Release();}
            token.ThrowIfCancellationRequested();
            if(before==_generation&&Volatile.Read(ref _suspended)==0&&Volatile.Read(ref _networkChanging)==0&&DateTimeOffset.UtcNow-sample.Time<TimeSpan.FromMilliseconds(options.TimeoutMs+5000))
            {
                sample=sample with{Context=env.Key};
                if(previous is not null&&sample.Time-previous>TimeSpan.FromSeconds(Math.Max(options.IntervalSeconds,options.TimeoutMs/1000d)*2+5))
                {detector.Reset();await PersistAsync(Event("Gap","检测到未采样空档，已重置连续状态"),token);RequestRoute("采样恢复");}
                previous=sample.Time;
                await PersistAsync(new SampleItem(sample),token);
                foreach(var detection in detector.Observe(sample))
                {
                    await PersistAsync(Event(detection.Kind,detection.Detail),token);
                    if(detection.Diagnose&&_uptime.ElapsedMilliseconds-Interlocked.Read(ref _diagnosedAt)>=300000)
                    {Interlocked.Exchange(ref _diagnosedAt,_uptime.ElapsedMilliseconds);var reason=IncidentDetector.Label(detection.Kind);RequestRoute(reason);DiagnosisRequested?.Invoke(reason);}
                }
            }
            else{detector.Reset();previous=null;await PersistAsync(Event("Gap","丢弃跨休眠、环境切换或超出有效期限的在途采样"),token);}
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(100,options.IntervalSeconds*1000-clock.Elapsed.TotalMilliseconds)),token);
        }
    }
    private async Task ContextAsync(CancellationToken token)
    {
        NetworkContext? candidate=null;long candidateAt=0,nextCheck=0;
        while(!token.IsCancellationRequested)
        {
            await Task.Delay(500,token);while(_powerEvents.TryDequeue(out var power))await PersistAsync(power,token);if(Volatile.Read(ref _suspended)!=0)continue;
            if(_uptime.ElapsedMilliseconds<nextCheck)continue;
            nextCheck=_uptime.ElapsedMilliseconds+5000;
            var current=_resources.ReadContext(target.Address);
            if(current.Key==_context.Key){candidate=null;Interlocked.Exchange(ref _networkChanging,0);continue;}
            if(candidate?.Key!=current.Key){candidate=current;candidateAt=_uptime.ElapsedMilliseconds;Interlocked.Exchange(ref _networkChanging,1);Interlocked.Increment(ref _generation);CancelRoute();continue;}
            if(_uptime.ElapsedMilliseconds-candidateAt<5000)continue;
            var old=_context;_context=current;Interlocked.Increment(ref _generation);candidate=null;Interlocked.Exchange(ref _networkChanging,0);
            ContextChanged?.Invoke(current.Description);
            await PersistAsync(Event("NetworkChanged",$"原：{old.Description}\n新：{current.Description}"),token);RequestRoute("系统选路变化");
        }
    }
    private async Task RoutesAsync(CancellationToken token)
    {
        if(!options.EnableRoutes){await Task.Delay(Timeout.Infinite,token);return;}
        var prior=history.LoadRoutes(target,1).FirstOrDefault();RouteRun? candidate=null;RouteRun? baseline=null;
        long nextPeriodic=_uptime.ElapsedMilliseconds+options.RouteMinutes*60000L;long confirmation=long.MaxValue;
        while(!token.IsCancellationRequested)
        {
            if(Volatile.Read(ref _suspended)!=0||Volatile.Read(ref _networkChanging)!=0){await Task.Delay(500,token);continue;}
            string? reason=Interlocked.Exchange(ref _routeRequest,null);
            if(_uptime.ElapsedMilliseconds>=confirmation){reason??="路径变化复测";confirmation=long.MaxValue;}
            if(_uptime.ElapsedMilliseconds>=nextPeriodic)reason??="定期检查";
            if(reason is null){await Task.Delay(500,token);continue;}
            RouteStateChanged?.Invoke("路由已排队 · "+reason);int generation=_generation;var context=_context;
            RouteRun run;
            using var routeCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
            lock(_routeSync)_activeRoute=routeCancellation;
            try{run=await _resources.Routes.RunAsync(target,context.Key,reason,options.Routes??new(),routeCancellation.Token,
                state=>RouteStateChanged?.Invoke(reason+" · "+state)).ConfigureAwait(false);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException){RouteStateChanged?.Invoke("本地环境改变，已取消旧路由探测");candidate=baseline=null;RequestRoute("环境改变后重新检查");await Task.Delay(500,token);continue;}
            catch(Exception ex){await PersistAsync(Event("RouteError",ex.Message),token);RouteStateChanged?.Invoke("路由检查失败："+ex.Message);nextPeriodic=_uptime.ElapsedMilliseconds+options.RouteMinutes*60000L;continue;}
            finally{lock(_routeSync)_activeRoute=null;}
            if(generation!=_generation||_resources.ReadContext(target.Address).Key!=context.Key){candidate=baseline=null;RequestRoute("环境改变后重新检查");await Task.Delay(500,token);continue;}
            run=run with{ContextDescription=context.Description};
            await PersistAsync(run,token);
            await PersistAsync(Event("RouteFinished",$"{reason} · {run.Outcome} · {context.Description}",run.Id,prior?.Id),token);
            if(generation==_generation&&prior is not null)
            {
                var comparison=RouteComparer.Compare(prior,run);
                if(candidate is not null&&baseline is not null)
                {
                    if(RouteComparer.Compare(candidate,run).Kind=="Same"&&RouteComparer.Compare(baseline,run).Kind=="Candidate")
                        await PersistAsync(Event("RouteChanged","复测仍观察到不同路径；不等同于证实 BGP 切换",run.Id,baseline.Id),token);
                    candidate=baseline=null;
                }
                else if(comparison.Kind=="Candidate")
                {
                    candidate=run;baseline=prior;confirmation=_uptime.ElapsedMilliseconds+30000;
                    await PersistAsync(Event("RouteCandidate",comparison.Detail,run.Id,prior.Id),token);
                }
                else if(comparison.Kind=="Visibility")await PersistAsync(Event("RouteVisibility",comparison.Detail,run.Id,prior.Id),token);
            }
            prior=run;RouteStateChanged?.Invoke($"{run.Finished.ToLocalTime():HH:mm:ss} · {run.Outcome}");
            nextPeriodic=_uptime.ElapsedMilliseconds+options.RouteMinutes*60000L;
        }
    }
}
