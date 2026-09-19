using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TcpLatencyMonitor.Core;

public sealed record HopProbe(int Ttl,int Sequence,string? Address,int Status,double? RttMs)
{
    public bool IsSupplemental { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string Label => Status==0?"目标回复":Status==11013?"TTL 到期":Status==11010?"未回应":$"状态 {Status}";
}
public sealed record RouteRun(string Id,string TargetKey,string Address,string Context,DateTimeOffset Started,DateTimeOffset Finished,
    string Reason,string Outcome,bool Reached,int TimeoutMs,int MaxHops,IReadOnlyList<HopProbe> Probes)
{
    public string ContextDescription { get; init; }="";
    public string? ProbeExecutionId { get; init; }
    public int BudgetSeconds{get;init;}=60;
    public int PayloadBytes=>32;
    // Null on historical snapshots: never invent a policy for old observations.
    public RouteOptions? ProbeOptions { get; init; }
    public bool? InitialReached { get; init; }
    public string SupplementOutcome { get; init; }="";
}
public sealed record RouteOptions(int MaxHops=32,int Queries=3,int TimeoutMs=1500,int BudgetSeconds=60)
{
    public int InitialSpacingMs { get; init; }=400;
    public int SupplementSpacingMs { get; init; }=1200;
    public int MaxAttemptsPerHop { get; init; }=10;
    public int SupplementBudgetSeconds { get; init; }=20;
}
public interface IHopProbe { Task<HopProbe> SendAsync(IPAddress target,int ttl,int sequence,int timeout,CancellationToken token); }

// Native calls have bounded timeouts. Cancellation waits for the owned call to return
// before releasing its handle/buffers. At most three calls are in flight per route.
public sealed class WindowsHopProbe : IHopProbe
{
    public Task<HopProbe> SendAsync(IPAddress target,int ttl,int sequence,int timeout,CancellationToken token) => Task.Run(()=>
    {
        token.ThrowIfCancellationRequested();
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("路由探测需要 Windows。");
        bool v6=target.AddressFamily==AddressFamily.InterNetworkV6;
        using var handle=new IcmpHandle(v6?Icmp6CreateFile():IcmpCreateFile());
        if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var request=Marshal.AllocHGlobal(32);var reply=Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.Copy(new byte[32],0,request,32);Marshal.Copy(new byte[4096],0,reply,4096);
            var options=new IpOptions{Ttl=(byte)ttl};
            uint count=v6?Icmp6SendEcho2(handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,SocketBytes(IPAddress.IPv6Any),SocketBytes(target),request,32,ref options,reply,4096,(uint)timeout)
                :IcmpSendEcho2(handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,BitConverter.ToUInt32(target.GetAddressBytes()),request,32,ref options,reply,4096,(uint)timeout);
            int error=Marshal.GetLastWin32Error();
            token.ThrowIfCancellationRequested();
            if(count==0)return new HopProbe(ttl,sequence,null,error==0?11050:error,null);
            int status=Marshal.ReadInt32(reply,v6?28:4);
            string? address;
            if(v6){var bytes=new byte[16];Marshal.Copy(reply+6,bytes,0,16);address=new IPAddress(bytes,unchecked((uint)Marshal.ReadInt32(reply,22))).ToString();}
            else{var bytes=new byte[4];Marshal.Copy(reply,bytes,0,4);address=new IPAddress(bytes).ToString();}
            double? rtt=status is 0 or 11013?unchecked((uint)Marshal.ReadInt32(reply,v6?32:8)):null;
            return new HopProbe(ttl,sequence,address,status,rtt);
        }
        finally{Marshal.FreeHGlobal(request);Marshal.FreeHGlobal(reply);}
    },CancellationToken.None);
    internal static byte[] SocketBytes(IPAddress ip)
    {
        var socket=new IPEndPoint(ip,0).Serialize();var bytes=new byte[socket.Size];for(int i=0;i<bytes.Length;i++)bytes[i]=socket[i];return bytes;
    }
    [StructLayout(LayoutKind.Sequential)]private struct IpOptions {public byte Ttl,Tos,Flags,Size;public IntPtr Data;}
    private sealed class IcmpHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public IcmpHandle(IntPtr value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>IcmpCloseHandle(handle); }
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern IntPtr IcmpCreateFile();
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern IntPtr Icmp6CreateFile();
    [DllImport("iphlpapi.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IcmpCloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint IcmpSendEcho2(IcmpHandle handle,IntPtr ev,IntPtr apc,IntPtr context,uint target,IntPtr data,ushort size,ref IpOptions options,IntPtr reply,uint replySize,uint timeout);
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint Icmp6SendEcho2(IcmpHandle handle,IntPtr ev,IntPtr apc,IntPtr context,byte[] source,byte[] target,IntPtr data,ushort size,ref IpOptions options,IntPtr reply,uint replySize,uint timeout);
}

public sealed class RouteProbe(IHopProbe probe)
{
    public async Task<RouteRun> RunAsync(Target target,string context,string reason,RouteOptions options,CancellationToken token,Action<string>? progress=null)
    {
        if(options.MaxHops is <1 or >64 || options.Queries is <1 or >3 || options.TimeoutMs is <100 or >5000 || options.BudgetSeconds is <1 or >180 ||
            options.InitialSpacingMs is <0 or >5000 || options.SupplementSpacingMs is <0 or >5000 ||
            options.MaxAttemptsPerHop<options.Queries || options.MaxAttemptsPerHop>10 || options.SupplementBudgetSeconds is <0 or >20)
            throw new ArgumentException("路由预算无效。");
        var start=DateTimeOffset.UtcNow;var clock=Stopwatch.StartNew();var ip=IPAddress.Parse(target.Address);
        long deadline=options.BudgetSeconds*1000L,lastSent=-5000;
        var samples=new List<HopProbe>();bool reached=false,allowSupplement=true;string outcome="达到最大跳数，路径不完整";
        async Task<bool> WaitForSlot(int spacing,long until)
        {
            token.ThrowIfCancellationRequested();
            long delay=Math.Max(0,lastSent+spacing-clock.ElapsedMilliseconds);
            if(until-clock.ElapsedMilliseconds<delay+100)return false;
            if(delay>0)await Task.Delay((int)delay,token).ConfigureAwait(false);
            return until-clock.ElapsedMilliseconds>=100;
        }
        async Task<HopProbe> Send(int ttl,int sequence,bool supplemental,long until)
        {
            token.ThrowIfCancellationRequested();
            lastSent=clock.ElapsedMilliseconds;var sentAt=DateTimeOffset.UtcNow;
            int timeout=(int)Math.Min(options.TimeoutMs,Math.Max(1,until-lastSent));
            var result=await probe.SendAsync(ip,ttl,sequence,timeout,token).ConfigureAwait(false);
            return result with{IsSupplemental=supplemental,SentAt=sentAt};
        }
        bool IsTarget(HopProbe p)=>p.Status==0&&p.Address is not null&&IPAddress.TryParse(p.Address,out var address)&&address.Equals(ip);
        for(int ttl=1;ttl<=options.MaxHops;ttl++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke($"初测第 {ttl} 跳");
            var tasks=new List<Task<HopProbe>>();HopProbe[] group;
            try
            {
                for(int sequence=1;sequence<=options.Queries;sequence++)
                {
                    if(!await WaitForSlot(options.InitialSpacingMs,deadline).ConfigureAwait(false))break;
                    tasks.Add(Send(ttl,sequence,false,deadline));
                }
            }
            finally
            {
                // Cancellation while pacing must still drain every native call we own.
                group=await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();samples.AddRange(group);
            if(group.Any(IsTarget)){reached=true;outcome="已收到目标回复";break;}
            if(group.Length==0){outcome="达到整轮时间预算，路径不完整";allowSupplement=false;break;}
            if(group.All(p=>p.Address is null&&p.Status!=11010)){outcome=$"本地或协议错误，状态 {group[0].Status}";allowSupplement=false;break;}
            if(group.All(p=>p.Status is 11002 or 11003 or 11004 or 11005)){outcome="收到明确不可达回复，路径不完整";allowSupplement=false;break;}
            if(group.Length<options.Queries){outcome="达到整轮时间预算，路径不完整";allowSupplement=false;break;}
        }
        bool initialReached=reached;string supplementOutcome="";
        // Only fill wholly silent TTLs. A 1/3 hop already has an identity; do not
        // repeatedly probe it until its diagnostic reply rate looks artificially good.
        var pending=new Queue<(int Ttl,int Attempts)>(samples.GroupBy(p=>p.Ttl)
            .Where(g=>g.Count()==options.Queries&&g.All(p=>p.Address is null&&p.Status==11010)&&g.Count()<options.MaxAttemptsPerHop)
            .Select(g=>(g.Key,g.Count())));
        if(allowSupplement&&pending.Count>0&&options.SupplementBudgetSeconds>0)
        {
            long supplementDeadline=Math.Min(deadline,clock.ElapsedMilliseconds+options.SupplementBudgetSeconds*1000L);
            int attempts=0,found=0;bool limited=false;string error="";
            while(pending.Count>0)
            {
                if(!await WaitForSlot(options.SupplementSpacingMs,supplementDeadline).ConfigureAwait(false)){limited=true;break;}
                var hop=pending.Dequeue();int sequence=hop.Attempts+1;
                progress?.Invoke($"正在补测第 {hop.Ttl} 跳 · 第 {sequence}/{options.MaxAttemptsPerHop} 次（含初测）");
                var result=await Send(hop.Ttl,sequence,true,supplementDeadline).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();samples.Add(result);attempts++;
                if(result.Address is not null)
                {
                    found++;
                    if(IsTarget(result)){reached=true;outcome="补测收到目标回复（初测未到达）";}
                }
                else if(result.Status!=11010){error=$"遇到探测错误 {result.Status}，停止补测";break;}
                else if(sequence<options.MaxAttemptsPerHop)pending.Enqueue((hop.Ttl,sequence));
            }
            supplementOutcome=$"补测 {attempts} 次 · 补全 {found} 跳"+(error.Length>0?" · "+error:limited?" · 时间预算已用尽，部分节点未补满上限":"");
        }
        token.ThrowIfCancellationRequested();
        return new RouteRun(Guid.NewGuid().ToString("N"),target.Key,target.Address,context,start,DateTimeOffset.UtcNow,reason,outcome,reached,options.TimeoutMs,options.MaxHops,samples)
            {ProbeExecutionId=Guid.NewGuid().ToString("N"),BudgetSeconds=options.BudgetSeconds,ProbeOptions=options,InitialReached=initialReached,SupplementOutcome=supplementOutcome};
    }
}

public sealed record PathComparison(string Kind,string Detail);
public static class RouteComparer
{
    private static List<HashSet<string>> Nodes(RouteRun run)=>run.Probes.Where(p=>!p.IsSupplemental).GroupBy(p=>p.Ttl).OrderBy(g=>g.Key)
        .Select(g=>g.Where(p=>p.Address is not null&&p.Status is 0 or 11013).Select(p=>p.Address!).ToHashSet()).ToList();
    public static PathComparison Compare(RouteRun old,RouteRun current)
    {
        if(old.TargetKey!=current.TargetKey||old.Context!=current.Context||old.TimeoutMs!=current.TimeoutMs||old.MaxHops!=current.MaxHops||old.BudgetSeconds!=current.BudgetSeconds||old.ProbeOptions!=current.ProbeOptions)return new("Incomparable","探测参数或本地网络环境不同");
        var a=Nodes(old);var b=Nodes(current);
        if((old.InitialReached??old.Reached)!=(current.InitialReached??current.Reached))return new("Visibility","初测目标到达状态变化");
        if(a.Count==b.Count&&a.Zip(b).All(p=>p.First.Overlaps(p.Second)||p.First.Count==0||p.Second.Count==0))
            return new(a.Zip(b).Any(p=>p.First.Count==0||p.Second.Count==0)?"Visibility":"Same","相同可见路径，或仍有重合的负载均衡节点");
        var aa=a.Where(s=>s.Count>0).ToList();var bb=b.Where(s=>s.Count>0).ToList();
        var lengths=new int[aa.Count+1,bb.Count+1];
        for(int i=1;i<=aa.Count;i++)for(int j=1;j<=bb.Count;j++)lengths[i,j]=aa[i-1].Overlaps(bb[j-1])?lengths[i-1,j-1]+1:Math.Max(lengths[i-1,j],lengths[i,j-1]);
        int common=lengths[aa.Count,bb.Count];
        if(common<3)return new("Incomparable","共同可见节点不足，无法确认路径变化");
        if(a.Any(s=>s.Count==0)||b.Any(s=>s.Count==0))return new("Visibility","节点可见性变化，保留快照但不确认线路切换");
        return new("Candidate",$"可见路径存在差异，共同节点 {common} 个；需复测确认");
    }
}
