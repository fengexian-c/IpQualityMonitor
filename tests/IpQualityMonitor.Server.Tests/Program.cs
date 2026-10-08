using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using IpQualityMonitor.Application;
using IpQualityMonitor.Linux;
using TcpLatencyMonitor.Core;

if (args.Contains("--fake-mtr")) { await FakeMtr(args); return; }
int checks = 0;
void Check(bool condition, string label)
{ if (!condition) throw new InvalidOperationException("FAIL " + label); Console.WriteLine("PASS " + label); checks++; }
void Reject(Action action, string label)
{ var rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, label); }
var ip = IPAddress.Loopback;
var echo = PacketMapping.ToHop(PacketReply.Parse("1 reply ip-4 127.0.0.1 round-trip-time 1250"), ip, 1, 1);
Check(echo.Status == 0 && echo.RttMs == 1.25, "microsecond RTT converted without integer truncation");
var ttl = PacketMapping.ToHop(PacketReply.Parse("2 ttl-expired ip-4 192.0.2.1 round-trip-time 987"), ip, 2, 1);
Check(ttl.Status == 11013 && ttl.RttMs == .987, "TTL expiry keeps the legacy route contract");
var error = PacketMapping.ToHop(PacketReply.Parse("3 icmp-error ip-4 127.0.0.1 round-trip-time 100 icmp-type 3 icmp-code 3"), ip, 3, 1);
Check(error.Status == 11005 && error.RttMs is null, "ICMP port unreachable is never counted as Echo success");
var v6 = PacketMapping.ToHop(PacketReply.Parse("4 icmp-error ip-6 ::1 round-trip-time 100 icmp-type 1 icmp-code 4"), IPAddress.IPv6Loopback, 1, 1);
Check(v6.Status == 11005, "ICMPv6 port unreachable preserves the failure class");
foreach (var (family, type, code, status) in new[] {
    (4, 3, 0, 11002), (4, 3, 1, 11003), (4, 3, 2, 11004), (4, 3, 4, 11009),
    (4, 11, 1, 11014), (4, 12, 0, 11015), (6, 1, 0, 11002), (6, 1, 1, 11003),
    (6, 2, 0, 11009), (6, 3, 1, 11014), (6, 4, 0, 11015) })
{
    var destination = family == 4 ? ip : IPAddress.IPv6Loopback;
    var reply = PacketReply.Parse($"9 icmp-error ip-{family} {destination} round-trip-time 100 icmp-type {type} icmp-code {code}");
    Check(PacketMapping.ToHop(reply, destination, 1, 1).Status == status, $"ICMPv{family} type {type}/code {code} maps to {status}");
}
Check(PacketMapping.ToHop(PacketReply.Parse("5 no-reply"), ip, 1, 1).Status == 11010, "only no-reply maps to a timeout");
Check(PacketMapping.ToHop(PacketReply.Parse("6 probes-exhausted"), ip, 1, 1).Status == PacketMapping.LocalError, "resource exhaustion is local");
Reject(() => PacketReply.Parse("1 reply ip-4"), "reject odd field count");
Reject(() => PacketReply.Parse("1 reply ip-4 1.2.3.4 ip-4 1.2.3.5"), "reject duplicate keys");
Reject(() => PacketReply.Parse(new string('a', 8193)), "bound response length");
Reject(() => PacketMapping.ToHop(PacketReply.Parse("7 reply ip-4 127.0.0.1 round-trip-time -1"), ip, 1, 1), "reject negative RTT");
Reject(() => PacketMapping.ToHop(PacketReply.Parse("8 icmp-error ip-4 127.0.0.1"), ip, 1, 1), "reject errors missing type/code");
Reject(() => PacketMapping.ToHop(PacketReply.Parse("9 reply ip-4 ::1 round-trip-time 1"), ip, 1, 1), "reject mismatched response address family");
Reject(() => PacketMapping.ToHop(PacketReply.Parse("10 reply ip-4 127.0.0.1 round-trip-time 4294967296"), ip, 1, 1), "reject RTT outside native unsigned wire range");
Reject(() => PacketMapping.ToHop(PacketReply.Parse("11 reply ip-4 127.0.0.1 round-trip-time 1.5"), ip, 1, 1), "reject fractional microseconds not emitted by native helper");
var a = LinuxNetworkContext.Parse("[{\"dst\":\"1.1.1.1\",\"dev\":\"eth0\",\"prefsrc\":\"172.20.0.2\",\"gateway\":\"172.20.0.1\"}]", "site");
var b = LinuxNetworkContext.Parse("[{\"dst\":\"8.8.8.8\",\"dev\":\"eth0\",\"prefsrc\":\"172.20.0.2\",\"gateway\":\"172.20.0.1\"}]", "site");
Check(a.Key == b.Key && a.Description.Contains("bridge"), "stable context excludes destination and identifies bridge");
var c = LinuxNetworkContext.Parse("[{\"dev\":\"eth0\",\"prefsrc\":\"172.20.0.3\",\"gateway\":\"172.20.0.1\"}]", "site");
Check(a.Key != c.Key, "source-address change creates a context boundary");
Reject(() => LinuxNetworkContext.Parse("[]", "site"), "missing route is not invented");
Reject(() => LinuxNetworkContext.Parse("[{\"dev\":\"eth0\"}]", "site"), "source-less route is not comparable");
var directory = Path.Combine(Path.GetTempPath(), "iqm-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string identity;
    using (var storage = new ServerStorage(directory, "test"))
    {
        identity = storage.Site.Id; var config = storage.Load();
        Check(config.Profiles.Count == 0, "new installation has no automatic public targets");
        Reject(() => { using var second = new ServerStorage(directory, "other"); }, "second writer cannot own the dataset");
        storage.Save(config with { Revision = 1 });
    }
    using (var storage = new ServerStorage(directory, "renamed"))
        Check(storage.Site.Id == identity && storage.Load().Revision == 1, "site identity and config survive restart");
    File.WriteAllText(Path.Combine(directory, "settings.server.json"), "broken-json");
    using (var storage = new ServerStorage(directory, "test"))
        Reject(() => storage.Load(), "broken settings do not silently reset");
    Check(File.ReadAllText(Path.Combine(directory, "settings.server.json")) == "broken-json", "broken input preserved");
    var foreign = Path.Combine(directory, "foreign"); Directory.CreateDirectory(foreign);
    File.WriteAllText(Path.Combine(foreign, "history.db"), "untouched");
    Reject(() => { using var storage = new ServerStorage(foreign, "test"); }, "unmarked Windows history is refused before initialization");
    Check(File.ReadAllText(Path.Combine(foreign, "history.db")) == "untouched", "foreign history remains byte-for-byte intact");
}
finally { Directory.Delete(directory, true); }

var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
var fakeArguments = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    ? new[] { Assembly.GetExecutingAssembly().Location, "--fake-mtr" } : new[] { "--fake-mtr" };
await using (var client = new MtrPacketClient(executable, fakeArguments))
{
    var replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.ProbeAsync(ip, 128, 300, CancellationToken.None)));
    Check(replies.All(r => r.Kind == "reply") && replies.Select(r => r.Token).Distinct().Count() == 20, "concurrent requests stay associated");
    var clock = Stopwatch.StartNew();
    var timeout = await client.ProbeAsync(IPAddress.Parse("127.0.0.2"), 128, 150, CancellationToken.None);
    Check(timeout.Kind == "no-reply" && clock.ElapsedMilliseconds >= 100, "millisecond request sent to helper");
    using var cancel = new CancellationTokenSource(100);
    var cancelled = client.ProbeAsync(IPAddress.Parse("127.0.0.4"), 128, 3000, cancel.Token);
    var other = client.ProbeAsync(ip, 128, 500, CancellationToken.None);
    var wasCancelled = false;
    try { await cancelled; } catch (OperationCanceledException) { wasCancelled = true; }
    Check(wasCancelled && (await other).Kind == "reply", "one cancellation does not cancel another target");
    Check(client.Pending == 0, "acknowledged cancellation leaves no pending request");
    using (var burstCancel = new CancellationTokenSource())
    {
        var burst = Enumerable.Range(0, 100).Select(_ => client.ProbeAsync(IPAddress.Parse("127.0.0.4"), 128, 3000, burstCancel.Token)).ToArray();
        await Task.Delay(30);
        Check(client.Pending <= 64, "helper requests stay within the global 64-slot bound under overload");
        burstCancel.Cancel();
        var count = 0;
        foreach (var request in burst) { try { await request; } catch (OperationCanceledException) { count++; } }
        Check(count == 100 && client.Pending == 0, "queued and active cancellation drains all requests without leaking slots");
        Check((await client.ProbeAsync(ip, 128, 300, CancellationToken.None)).Kind == "reply", "client remains usable after cancellation burst");
    }
    var sample = await new LinuxIcmpProbe(client).RunAsync(Target.Parse("127.0.0.3", 0, ProbeProtocol.Icmp, 300),
        TimeSpan.FromMilliseconds(300), CancellationToken.None);
    Check(sample.Status == ProbeStatus.LocalError && sample.LatencyMs is null, "helper crash is not remote packet loss");
    await Task.Delay(5100);
    Check((await client.ProbeAsync(ip, 128, 300, CancellationToken.None)).Kind == "reply", "helper restarts after the bounded crash cooldown");
}
await using (var client = new MtrPacketClient(executable, [.. fakeArguments, "--no-raw"]))
{
    var sample = await new LinuxIcmpProbe(client).RunAsync(Target.Parse("127.0.0.1", 0, ProbeProtocol.Icmp, 300), TimeSpan.FromMilliseconds(300), CancellationToken.None);
    Check(sample.Status == ProbeStatus.LocalError && client.Pending == 0, "missing raw-ICMP support is local error with no lossy fallback");
}
await using (var client = new MtrPacketClient(executable, [.. fakeArguments, "--malformed"]))
{
    var sample = await new LinuxIcmpProbe(client).RunAsync(Target.Parse("127.0.0.1", 0, ProbeProtocol.Icmp, 300), TimeSpan.FromMilliseconds(300), CancellationToken.None);
    Check(sample.Status == ProbeStatus.LocalError && client.Pending == 0, "malformed helper output fails its generation and drains requests");
}
await using (var network = new LinuxNetworkContext(executable, "bounded-test"))
{
    // Invalid literals fail before process launch, so this stresses scheduling without I/O.
    for (var i = 0; i < 2000; i++) network.Read("invalid-address-" + i);
    var refreshing = (ConcurrentDictionary<string, Task>)typeof(LinuxNetworkContext).GetField("_refreshing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(network)!;
    Check(refreshing.Count <= 128, "network-context refresh admission is bounded");
    await Task.WhenAll(refreshing.Values);
    var cache = typeof(LinuxNetworkContext).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(network)!;
    Check((int)cache.GetType().GetProperty("Count")!.GetValue(cache)! <= 128, "network-context cache remains bounded after concurrent completion");
}
AuthChecks.Run(Check, Reject);
await RuntimeChecks.RunAsync(Check, Reject);
Console.WriteLine($"ALL {checks} SERVER CHECKS PASSED.");

static async Task FakeMtr(string[] args)
{
    var inFlight = new ConcurrentDictionary<int, CancellationTokenSource>();
    var outputLock = new object();
    void Reply(string line) { lock (outputLock) { Console.WriteLine(line); Console.Out.Flush(); } }
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        var parts = line.Split(' '); int id = int.Parse(parts[0]);
        if (parts[1] == "check-support")
        {
            if (args.Contains("--malformed")) Reply($"{id} malformed odd-field");
            else Reply($"{id} feature-support support {(args.Contains("--no-raw") && parts[3].StartsWith("iqm-raw-", StringComparison.Ordinal) ? "no" : "ok")}");
            continue;
        }
        if (parts[1] == "cancel-probe")
        { if (inFlight.TryRemove(id, out var old)) old.Cancel(); Reply($"{id} cancelled"); continue; }
        var fields = new Dictionary<string, string>();
        for (int i = 2; i < parts.Length; i += 2) fields.Add(parts[i], parts[i + 1]);
        var address = fields.GetValueOrDefault("ip-4", "::1");
        if (address == "127.0.0.3") Environment.Exit(42);
        var cts = new CancellationTokenSource(); inFlight[id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (address == "127.0.0.4") await Task.Delay(10000, cts.Token);
                else if (address == "127.0.0.2") await Task.Delay(int.Parse(fields["timeout-ms"]), cts.Token);
                else await Task.Delay(id % 9, cts.Token);
                if (inFlight.TryRemove(id, out _))
                    Reply(address == "127.0.0.2" ? $"{id} no-reply" : $"{id} reply ip-4 127.0.0.1 round-trip-time 100");
            }
            catch (OperationCanceledException) { }
        });
    }
}
