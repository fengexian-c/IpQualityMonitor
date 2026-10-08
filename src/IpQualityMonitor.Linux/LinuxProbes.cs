using System.Diagnostics;
using System.Net;
using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Linux;

public sealed class LinuxHopProbe(MtrPacketClient client) : IHopProbe
{
    public async Task<HopProbe> SendAsync(IPAddress target, int ttl, int sequence, int timeout, CancellationToken token)
    {
        try { return PacketMapping.ToHop(await client.ProbeAsync(target, ttl, timeout, token).ConfigureAwait(false), target, ttl, sequence); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new HopProbe(ttl, sequence, null, PacketMapping.LocalError, null); }
    }
}

public sealed class LinuxIcmpProbe(MtrPacketClient client) : IProbe
{
    public async Task<Sample> RunAsync(Target target, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var start = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
        try
        {
            var ip = IPAddress.Parse(target.Address);
            var response = await client.ProbeAsync(ip, 128, (int)timeout.TotalMilliseconds, token).ConfigureAwait(false);
            var hop = PacketMapping.ToHop(response, ip, 128, 1);
            var status = hop.Status switch
            {
                0 => hop.Address is {} address && IPAddress.Parse(address).Equals(ip) ? ProbeStatus.Success : ProbeStatus.Failed,
                11010 => ProbeStatus.Timeout,
                11002 or 11003 or 11004 or 11005 => ProbeStatus.Unreachable,
                PacketMapping.LocalError => ProbeStatus.LocalError,
                _ => ProbeStatus.Failed
            };
            var detail = "mtr iqm-v1 · " + response.Kind;
            if (response.Kind == "icmp-error") detail += $" type={response.Get("icmp-type")} code={response.Get("icmp-code")}";
            if (hop.Address is not null) detail += " · 回应 " + hop.Address;
            return new(start, status, status == ProbeStatus.Success ? hop.RttMs : null, watch.Elapsed.TotalMilliseconds, detail)
                { NativeStatus = hop.Status };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { return new(start, ProbeStatus.LocalError, null, watch.Elapsed.TotalMilliseconds, "Linux ICMP 本地错误：" + ex.Message) { NativeStatus = PacketMapping.LocalError }; }
    }
}
