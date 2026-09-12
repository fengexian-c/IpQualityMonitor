namespace TcpLatencyMonitor.Core;

public sealed record MeasurementState(Target Target,bool Running,Sample? Last,long Success,long Failure,long LocalErrors,long Consecutive,DateTimeOffset? FirstFailure,long Skipped,string RouteState,string Context,string Health);

public sealed class MultiTargetMonitor : IAsyncDisposable
{
    private sealed class Worker
    {
        public required Target Target;public required MonitorOptions Options;public required MonitorCoordinator Coordinator;
        public readonly CancellationTokenSource Stop=new();public readonly HashSet<string> Owners=new();
        public Task Task=Task.CompletedTask;public Sample? Last;public long Success,Failure,Errors,Consecutive,Skipped;
        public DateTimeOffset? FirstFailure;public string RouteState="等待路由检查",Context="等待系统选路信息",Health="等待采样";
        public bool Finished;
    }
    private readonly History _history;private readonly MonitorResources _resources;
    private readonly object _gate=new();private readonly SemaphoreSlim _operations=new(1);
    private readonly Dictionary<string,Worker> _workers=new();private readonly Dictionary<string,TargetProfile> _profiles=new();
    private readonly CancellationTokenSource _maintenanceStop=new();private readonly Task _maintenance;
    private bool _closing;
    public event Action? Changed;
    public event Action<RouteRun>? RouteSaved;
    public event Action<string,Exception>? TargetFailed;
    public event Action<Exception>? StorageFailed;
    public int PendingRoutes=>_resources.Routes.Pending;
    public MultiTargetMonitor(History history,MonitorResources? resources=null)
    {
        _history=history;_resources=resources??new();_history.WriteFailed+=OnStorageFailed;
        _maintenance=Task.Run(async()=>
        {
            try{while(true){await Task.Delay(TimeSpan.FromMinutes(1),_maintenanceStop.Token);await Task.Run(()=>_history.PruneStep(DateTimeOffset.UtcNow));}}
            catch(OperationCanceledException)when(_maintenanceStop.IsCancellationRequested){}
            catch(Exception ex){OnStorageFailed(ex);}
        });
    }
    private void OnStorageFailed(Exception ex)
    {
        lock(_gate)foreach(var worker in _workers.Values)worker.Stop.Cancel();
        StorageFailed?.Invoke(ex);
    }
    public bool IsRunning(string id){lock(_gate)return _profiles.ContainsKey(id)&&_workers.Values.Any(w=>w.Owners.Contains(id)&&!w.Finished&&!w.Stop.IsCancellationRequested);}
    public int RunningProfiles {get{lock(_gate)return _profiles.Keys.Count(IsRunning);}}
    public MeasurementState? State(Target target)
    {
        lock(_gate)
        {
            if(!_workers.TryGetValue(target.Key,out var w))return null;
            return new(target,!w.Finished&&!w.Stop.IsCancellationRequested,w.Last,w.Success,w.Failure,w.Errors,w.Consecutive,w.FirstFailure,w.Skipped,w.RouteState,w.Context,w.Health);
        }
    }
    public async Task StartAsync(TargetProfile profile)
    {
        profile.Validate();await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            lock(_gate)
            {
                if(_closing)throw new ObjectDisposedException(nameof(MultiTargetMonitor));
                if(_profiles.ContainsKey(profile.Id))return;
                if(_history.WriteFailure is not null)throw new IOException("写入中断后需解决磁盘问题并重新打开软件。",_history.WriteFailure);
                var targets=profile.Secondary is Target secondary?new[]{profile.Primary,secondary}:new[]{profile.Primary};
                var options=profile.Options();
                foreach(var target in targets)
                {
                    if(_workers.TryGetValue(target.Key,out var existing)&&existing.Owners.Count>0&&existing.Options!=options)
                        throw new ArgumentException($"{target.Label} 已由其他目标监测。共享检测需要相同的采样间隔、路由和诊断参数；请先统一参数或停止相关目标。");
                }
                _profiles.Add(profile.Id,profile.Copy());
                foreach(var target in targets)
                {
                    if(!_workers.TryGetValue(target.Key,out var worker)||worker.Owners.Count==0)
                    {
                        worker?.Stop.Dispose();
                        var coordinator=new MonitorCoordinator(_history,target,options,_resources);
                        worker=new(){Target=target,Options=options,Coordinator=coordinator};_workers[target.Key]=worker;
                        Wire(worker);var captured=worker;worker.Task=Task.Run(()=>RunWorkerAsync(captured));
                    }
                    worker.Owners.Add(profile.Id);
                }
            }
            Changed?.Invoke();
        }
        finally{_operations.Release();}
    }
    private void Wire(Worker w)
    {
        w.Coordinator.SampleSaved+=sample=>
        {
            lock(_gate)
            {
                if(w.Last is not null&&(w.Last.Context!=sample.Context||sample.Time-w.Last.Time>TimeSpan.FromMilliseconds(Math.Max(w.Options.IntervalSeconds*1000,w.Options.TimeoutMs)*2+1000)))
                {w.Consecutive=0;w.FirstFailure=null;w.Health="等待确认";}
                w.Last=sample;
                if(sample.Status==ProbeStatus.Success){w.Success++;w.Consecutive=0;w.FirstFailure=null;if(w.Health is "等待采样" or "等待确认" or "本地错误" or "本机繁忙" or "可达性未验证")w.Health="有回应";}
                else if(sample.Status==ProbeStatus.LocalError){w.Errors++;w.Consecutive=0;w.FirstFailure=null;w.Health="本地错误";}
                else{w.Failure++;w.Consecutive++;w.FirstFailure??=sample.Time;if(w.Health is not ("持续失败" or "延迟升高" or "可达性未验证"))w.Health="等待确认";}
            }
            Changed?.Invoke();
        };
        w.Coordinator.SampleSkipped+=()=>{lock(_gate){w.Skipped++;w.Health="本机繁忙";}Changed?.Invoke();};
        w.Coordinator.RouteStateChanged+=text=>{lock(_gate)w.RouteState=text;Changed?.Invoke();};
        w.Coordinator.ContextChanged+=text=>{lock(_gate)w.Context=text;Changed?.Invoke();};
        w.Coordinator.RouteSaved+=run=>RouteSaved?.Invoke(run);
        w.Coordinator.EventSaved+=ev=>
        {
            lock(_gate)
            {
                if(ev.Kind=="Unavailable")w.Health="持续失败";
                else if(ev.Kind=="Unverified")w.Health="可达性未验证";
                else if(ev.Kind=="LatencyHigh")w.Health="延迟升高";
                else if(ev.Kind is "Recovered" or "LatencyRecovered")w.Health="有回应";
                else if(ev.Kind is "Gap" or "NetworkChanged" or "Resume")w.Health="等待确认";
                else if(ev.Kind=="Suspend")w.Health="休眠";
            }
            Changed?.Invoke();
        };
    }
    private async Task RunWorkerAsync(Worker worker)
    {
        try{await worker.Coordinator.RunAsync(worker.Stop.Token).ConfigureAwait(false);}
        catch(Exception ex)
        {
            string[] owners;lock(_gate){worker.Health="监测中断";owners=worker.Owners.ToArray();}
            if(_history.WriteFailure is null)foreach(var owner in owners)TargetFailed?.Invoke(owner,ex);
        }
        finally{lock(_gate)worker.Finished=true;Changed?.Invoke();}
    }
    public async Task StopAsync(string id)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Worker[] stopped;
            lock(_gate)
            {
                _profiles.Remove(id);stopped=_workers.Values.Where(w=>w.Owners.Remove(id)&&w.Owners.Count==0).ToArray();
                foreach(var worker in stopped)worker.Stop.Cancel();
            }
            await Task.WhenAll(stopped.Select(w=>w.Task)).ConfigureAwait(false);
            Changed?.Invoke();
        }
        finally{_operations.Release();}
    }
    public async Task StopAllAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Worker[] workers;lock(_gate){_profiles.Clear();workers=_workers.Values.ToArray();foreach(var worker in workers){worker.Owners.Clear();worker.Stop.Cancel();}}
            await Task.WhenAll(workers.Select(w=>w.Task)).ConfigureAwait(false);Changed?.Invoke();
        }
        finally{_operations.Release();}
    }
    public bool RequestRoute(string id)
    {
        lock(_gate)
        {
            if(!_profiles.TryGetValue(id,out var profile))return false;
            return _workers.TryGetValue(profile.Primary.Key,out var w)&&!w.Finished&&w.Coordinator.RequestRoute();
        }
    }
    public void PowerChanged(bool suspended){lock(_gate)foreach(var w in _workers.Values.Where(w=>!w.Finished))w.Coordinator.PowerChanged(suspended);}
    public async ValueTask DisposeAsync()
    {
        lock(_gate)_closing=true;
        _maintenanceStop.Cancel();await StopAllAsync();await _maintenance.ConfigureAwait(false);
        _history.WriteFailed-=OnStorageFailed;await _resources.DisposeAsync();
        foreach(var worker in _workers.Values)worker.Stop.Dispose();_operations.Dispose();_maintenanceStop.Dispose();
    }
}
