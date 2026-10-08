using System.Globalization;
using System.Net;
using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Linux;

public sealed record PacketReply(int Token, string Kind, IReadOnlyDictionary<string, string> Arguments)
{
    public static PacketReply Parse(string line)
    {
        if (line.Length > 8192) throw new InvalidDataException("探测组件响应过长。");
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length % 2 != 0 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token < 1)
            throw new InvalidDataException("无效的探测组件响应。");
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i += 2)
            if (!args.TryAdd(parts[i], parts[i + 1])) throw new InvalidDataException("重复的探测响应字段。");
        return new(token, parts[1], args);
    }
    public string? Get(string name) => Arguments.GetValueOrDefault(name);
}

/// <summary>Legacy numbers are an explicit compatibility mapping, NEVER Linux errno values.</summary>
public static class PacketMapping
{
    public const int LocalError = 1;
    public static HopProbe ToHop(PacketReply reply, IPAddress destination, int ttl, int sequence)
    {
        if (reply.Kind == "no-reply") return new(ttl, sequence, null, 11010, null);
        if (reply.Kind == "cancelled") throw new OperationCanceledException();
        if (reply.Kind is not ("reply" or "ttl-expired" or "icmp-error"))
            return new(ttl, sequence, null, LocalError, null);
        var text = reply.Get(destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "ip-4" : "ip-6");
        if (!IPAddress.TryParse(text, out var ip)) throw new InvalidDataException("回应缺少有效 IP。");
        int status;
        if (reply.Kind == "icmp-error")
        {
            // Preserve the original type/code in Sample.Detail. A route's legacy format stores
            // the neutral class only; never allow an ICMP error to count as an Echo Reply.
            var type = RequiredByte(reply, "icmp-type"); var code = RequiredByte(reply, "icmp-code");
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                status = type == 3 ? code switch { 0 => 11002, 1 => 11003, 2 => 11004, 3 => 11005, _ => 11003 } : 11050;
            else status = type == 1 ? 11003 : 11050;
            return new(ttl, sequence, ip.ToString(), status, null);
        }
        if (!double.TryParse(reply.Get("round-trip-time"), NumberStyles.None, CultureInfo.InvariantCulture, out var us) ||
            !double.IsFinite(us) || us < 0) throw new InvalidDataException("无效的探测 RTT。");
        status = reply.Kind == "ttl-expired" ? 11013 : 0;
        return new(ttl, sequence, ip.ToString(), status, us / 1000.0);
    }
    private static byte RequiredByte(PacketReply reply, string key) =>
        byte.TryParse(reply.Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException("ICMP 错误缺少 type/code。");
}
