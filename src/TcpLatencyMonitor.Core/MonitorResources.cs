using System.Diagnostics;

namespace TcpLatencyMonitor.Core;

/// <summary>One instance per application, shared by all measurement series.</summary>
public sealed class MonitorResources : IAsyncDisposable
{
    public SemaphoreSlim ProbeSlots {get;}
    public RouteScheduler Routes {get;}
    private readonly object _contextLock=new();
    private readonly Dictionary<string,(NetworkContext Context,long Time)> _contexts=new();
    private readonly Func<string,NetworkContext> _readContext;
    private readonly RouteNotifications? _notifications;
    private int _startIndex;
    public Func<Target,IProbe> ProbeFactory {get;}
    public MonitorResources(int maximumProbes=32,Func<Target,IProbe>? probes=null,Func<string,NetworkContext>? contexts=null,
        Func<Target,string,string,RouteOptions,CancellationToken,Action<string>?,Task<RouteRun>>? routeProbe=null)
    {
        if(maximumProbes<1)throw new ArgumentOutOfRangeException(nameof(maximumProbes));
        ProbeSlots=new(maximumProbes,maximumProbes);ProbeFactory=probes??(t=>t.Protocol==ProbeProtocol.Icmp?new IcmpProbe():new TcpProbe());
        _readContext=contexts??NetworkContext.Read;
        if(contexts is null)_notifications=new RouteNotifications(()=>{lock(_contextLock)_contexts.Clear();});
        var implementation=routeProbe??((Target target,string context,string reason,RouteOptions options,CancellationToken token,Action<string>? progress)=>new RouteProbe(new WindowsHopProbe()).RunAsync(target,context,reason,options,token,progress));
        Routes=new((target,context,reason,options,token,progress)=>
        {
            if(ReadContext(target.Address,true).Key!=context)throw new OperationCanceledException("排队期间本机网络环境已改变。");
            return implementation(target,context,reason,options,token,progress);
        });
    }
    public NetworkContext ReadContext(string address,bool refresh=false)
    {
        lock(_contextLock)
        {
            long now=Environment.TickCount64;
            if(!refresh&&_contexts.TryGetValue(address,out var entry)&&now-entry.Time<2000)return entry.Context;
            var result=_readContext(address);_contexts[address]=(result,now);return result;
        }
    }
    public int StartDelay(int intervalSeconds)=>((Interlocked.Increment(ref _startIndex)-1)*97)%(Math.Max(1,intervalSeconds)*1000);
    public async ValueTask DisposeAsync(){_notifications?.Dispose();await Routes.DisposeAsync();ProbeSlots.Dispose();}
}

/// <summary>One route at a time. Equal pending/in-flight requests share work, not cancellation ownership.</summary>
public sealed class RouteScheduler : IAsyncDisposable
{
    private sealed class Request
    {
        public required string Key;public required Target Target;public required string Context;public required string Reason;
        public required RouteOptions Options;public long Sequence;public int Owners;public bool Urgent,Running;
        public readonly CancellationTokenSource Cancel=new();
        public readonly TaskCompletionSource<RouteRun> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<Action<string>> Progress=new();
    }
    private readonly object _gate=new();private readonly Dictionary<string,Request> _requests=new();
    private readonly SemaphoreSlim _signal=new(0);private readonly CancellationTokenSource _stop=new();
    private readonly Func<Target,string,string,RouteOptions,CancellationToken,Action<string>?,Task<RouteRun>> _probe;
    private readonly Task _worker;private long _sequence;private int _urgentStreak;
    public int Pending {get{lock(_gate)return _requests.Values.Count(r=>!r.Running);}}
    public RouteScheduler(Func<Target,string,string,RouteOptions,CancellationToken,Action<string>?,Task<RouteRun>> probe){_probe=probe;_worker=Task.Run(WorkAsync);}
    public async Task<RouteRun> RunAsync(Target target,string context,string reason,RouteOptions options,CancellationToken token,Action<string>? progress=null)
    {
        token.ThrowIfCancellationRequested();Request request;
        string key=$"{target.Address}|{context}|{options}";
        lock(_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            if(!_requests.TryGetValue(key,out request!)||request.Cancel.IsCancellationRequested)
            {
                request=new(){Key=key,Target=target,Context=context,Reason=reason,Options=options,Sequence=++_sequence};
                _requests[key]=request;_signal.Release();
            }
            request.Owners++;request.Urgent|=reason!="定期检查"&&reason!="开始监控";
            if(progress is not null)request.Progress.Add(progress);
        }
        progress?.Invoke("路由已排队 · 等待全局队列");
        try
        {
            var result=await request.Result.Task.WaitAsync(token).ConfigureAwait(false);
            return result.TargetKey==target.Key?result:result with{Id=Guid.NewGuid().ToString("N"),TargetKey=target.Key,Reason=reason};
        }
        finally
        {
            lock(_gate)
            {
                if(progress is not null)request.Progress.Remove(progress);
                if(--request.Owners==0&&!request.Result.Task.IsCompleted)request.Cancel.Cancel();
            }
        }
    }
    private void Notify(Request request,string text)
    {
        Action<string>[] callbacks;lock(_gate)callbacks=request.Progress.ToArray();
        foreach(var callback in callbacks)callback(text);
    }
    private async Task WorkAsync()
    {
        try
        {
            while(true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);Request? request;
                lock(_gate)
                {
                    var pending=_requests.Values.Where(r=>!r.Running).OrderBy(r=>r.Sequence).ToArray();
                    request=_urgentStreak>=3?pending.FirstOrDefault():pending.FirstOrDefault(r=>r.Urgent)??pending.FirstOrDefault();
                    if(request is null)continue;request.Running=true;_urgentStreak=request.Urgent?_urgentStreak+1:0;
                }
                try
                {
                    using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token,request.Cancel.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    Notify(request,"正在检查路由 · "+request.Reason);
                    var result=await _probe(request.Target,request.Context,request.Reason,request.Options,lifetime.Token,text=>Notify(request,text)).ConfigureAwait(false);
                    request.Result.TrySetResult(result);
                }
                catch(OperationCanceledException){request.Result.TrySetCanceled();}
                catch(Exception ex){request.Result.TrySetException(ex);}
                finally
                {
                    lock(_gate){if(_requests.GetValueOrDefault(request.Key)==request)_requests.Remove(request.Key);}
                    // Owners can still be leaving their finally blocks; do not dispose their CTS here.
                }
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        finally{lock(_gate){foreach(var request in _requests.Values)request.Result.TrySetCanceled();_requests.Clear();}}
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();await _worker.ConfigureAwait(false);_signal.Dispose();_stop.Dispose();}
}
