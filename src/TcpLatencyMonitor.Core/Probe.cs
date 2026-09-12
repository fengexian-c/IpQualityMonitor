using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace TcpLatencyMonitor.Core;

public enum ProbeStatus { Success, Refused, Timeout, Unreachable, LocalError, Failed }

public enum ProbeProtocol { Tcp, Icmp }

public sealed record Target(string Address, int Port, ProbeProtocol Protocol = ProbeProtocol.Tcp, int TimeoutMs = 0)
{
    public string Key => Protocol == ProbeProtocol.Tcp && TimeoutMs == 0 ? $"[{Address}]:{Port}" : $"{Protocol}|[{Address}]:{Port}|timeout={TimeoutMs}|bytes={(Protocol==ProbeProtocol.Icmp?32:0)}";
    public string Label => Protocol == ProbeProtocol.Icmp ? $"{Address} · ICMP" : $"[{Address}]:{Port} · TCP";
    public static Target Parse(string address, double port=0, ProbeProtocol protocol=ProbeProtocol.Tcp, int timeoutMs=0)
    {
        if (!IPAddress.TryParse(address.Trim(), out var ip) ||
            (ip.AddressFamily == AddressFamily.InterNetwork && address.Trim().Split('.').Length != 4))
            throw new ArgumentException("请填写有效的 IPv4 或 IPv6 地址，不包含协议或端口。");
        if (protocol == ProbeProtocol.Tcp && (!double.IsFinite(port) || port != Math.Truncate(port) || port < 1 || port > 65535))
            throw new ArgumentException("端口必须是 1–65535 之间的整数。");
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) ||
            ip.IsIPv6Multicast || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224))
            throw new ArgumentException("请填写具体目标的单播 IP 地址。");
        if (ip.IsIPv6LinkLocal && ip.ScopeId==0) throw new ArgumentException("IPv6 链路本地地址需要 %接口编号。");
        if(timeoutMs<0 || timeoutMs>60000)throw new ArgumentException("超时参数无效。");
        return new Target(ip.ToString(), protocol==ProbeProtocol.Icmp?0:(int)port, protocol, timeoutMs);
    }
}

public sealed record Sample(DateTimeOffset Time, ProbeStatus Status, double? LatencyMs, double ElapsedMs, string Detail)
{
    public string Context { get; init; } = "";
    public int NativeStatus { get; init; }
    public bool IsAttempt => Status != ProbeStatus.LocalError;
    public static string Label(ProbeStatus status) => status switch
    {
        ProbeStatus.Success => "成功", ProbeStatus.Refused => "连接被拒绝",
        ProbeStatus.Timeout => "超时", ProbeStatus.Unreachable => "网络不可达", ProbeStatus.Failed => "连接失败", _ => "本地错误"
    };
}

public sealed class IcmpProbe : IProbe
{
    private readonly WindowsHopProbe _probe=new();
    public async Task<Sample> RunAsync(Target target,TimeSpan timeout,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var time=DateTimeOffset.UtcNow;var clock=Stopwatch.StartNew();
        try
        {
            var ip=IPAddress.Parse(target.Address);
            var reply=await _probe.SendAsync(ip,128,1,(int)timeout.TotalMilliseconds,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var status=reply.Status<11000&&reply.Status!=0?ProbeStatus.LocalError:MapStatus((IPStatus)reply.Status);
            if(status==ProbeStatus.Success&&(reply.Address is null||!ip.Equals(IPAddress.Parse(reply.Address))))status=ProbeStatus.Failed;
            return new Sample(time,status,status==ProbeStatus.Success?reply.RttMs:null,clock.Elapsed.TotalMilliseconds,
                $"ICMP {(IPStatus)reply.Status}"+(reply.Address is null?"":$" · 回复 {reply.Address}")){NativeStatus=reply.Status};
        }
        catch(System.ComponentModel.Win32Exception ex)
        {
            token.ThrowIfCancellationRequested();
            return new Sample(time,ProbeStatus.LocalError,null,clock.Elapsed.TotalMilliseconds,ex.Message){NativeStatus=ex.NativeErrorCode};
        }
    }
    public static ProbeStatus MapStatus(IPStatus status) => status switch
    {
        IPStatus.Success=>ProbeStatus.Success,IPStatus.TimedOut=>ProbeStatus.Timeout,
        IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable or IPStatus.DestinationUnreachable or
        IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable or IPStatus.BadRoute=>ProbeStatus.Unreachable,
        IPStatus.NoResources or IPStatus.BadOption or IPStatus.BadHeader or IPStatus.BadDestination or IPStatus.HardwareError or IPStatus.Unknown=>ProbeStatus.LocalError,
        _=>ProbeStatus.Failed
    };
}

public interface IProbe
{
    Task<Sample> RunAsync(Target target, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class TcpProbe : IProbe
{
    public async Task<Sample> RunAsync(Target target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var time = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var socket = new Socket(IPAddress.Parse(target.Address).AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(target.Address), target.Port), deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new Sample(time, ProbeStatus.Success, watch.Elapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds, "TCP 连接建立成功");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Sample(time, ProbeStatus.Timeout, null, watch.Elapsed.TotalMilliseconds, "超过设定的连接超时");
        }
        catch (SocketException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = MapError(ex.SocketErrorCode);
            return new Sample(time, status, null, watch.Elapsed.TotalMilliseconds, ex.SocketErrorCode.ToString());
        }
    }

    public static ProbeStatus MapError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => ProbeStatus.Refused,
        SocketError.TimedOut => ProbeStatus.Timeout,
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostDown => ProbeStatus.Unreachable,
        SocketError.AccessDenied or SocketError.AddressNotAvailable or SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets or SocketError.InvalidArgument or SocketError.ProtocolNotSupported or SocketError.OperationNotSupported => ProbeStatus.LocalError,
        _ => ProbeStatus.Failed
    };
}

public sealed class MonitorLoop(IProbe probe)
{
    public async Task RunAsync(Target target, TimeSpan interval, TimeSpan timeout, Func<Sample, Task> onSample, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var watch = Stopwatch.StartNew();
            var sample = await probe.RunAsync(target, timeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // A probe resumed well beyond its deadline is stale (sleep or a
            // suspended process). Do not turn that unobserved gap into a failure.
            if (DateTimeOffset.UtcNow - sample.Time <= timeout + TimeSpan.FromSeconds(5))
                await onSample(sample).ConfigureAwait(false);
            // No overlap, no catch-up burst after sleep, and no synthetic failure for skipped time.
            var delay = interval - watch.Elapsed;
            await Task.Delay(delay > TimeSpan.FromMilliseconds(100) ? delay : TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }
}
