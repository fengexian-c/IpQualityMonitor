using System.Text.Json;
using System.Text.Json.Serialization;
using IpQualityMonitor.Application;
using IpQualityMonitor.Web;
using Microsoft.Extensions.Configuration;
using TcpLatencyMonitor.Core;

internal static class RuntimeChecks
{
    public static async Task RunAsync(Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(Path.GetTempPath(), "iqm-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "history.db-wal", "history.db-shm", "route-analysis.db-journal", "settings.json" })
            {
                var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, name); File.WriteAllText(path, "preserve-me");
                reject(() => { using var storage = new ServerStorage(directory, "test"); }, "refuse unmarked " + name);
                check(File.ReadAllText(path) == "preserve-me" && !File.Exists(Path.Combine(directory, "instance.json")),
                    "unmarked " + name + " is not claimed or modified");
            }
            var incomplete = Path.Combine(root, "incomplete");
            using (var storage = new ServerStorage(incomplete, "test")) storage.Load();
            File.Delete(Path.Combine(incomplete, "settings.server.json"));
            File.WriteAllText(Path.Combine(incomplete, "route-analysis.db-wal"), "preserve-analysis");
            reject(() => { using var storage = new ServerStorage(incomplete, "test"); }, "missing settings cannot reset an analysis-only backup");

            var atomic = Path.Combine(root, "atomic.json");
            ServerStorage.WriteAtomic(atomic, new { value = "first" });
            ServerStorage.WriteAtomic(atomic, new { value = "second" });
            check(File.ReadAllText(atomic).Contains("second") && Directory.GetFiles(root, "*.tmp").Length == 0,
                "atomic replacement commits and leaves no temporary file");
            if (!OperatingSystem.IsWindows())
                check(File.GetUnixFileMode(atomic) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "atomic JSON files are private from creation");

            Credentials(root, check, reject);
            await Runtime(root, check);

            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            json.Converters.Add(new JsonStringEnumConverter());
            using var hop = JsonDocument.Parse(JsonSerializer.Serialize(new HopProbe(1, 1, "127.0.0.1", 11013, .5), json));
            check(hop.RootElement.GetProperty("status").GetInt32() == 11013,
                "route hop status stays numeric with Web enum serialization");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Credentials(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        using var storage = new ServerStorage(Path.Combine(root, "credentials"), "test");
        var missing = new ConfigurationBuilder().Build();
        reject(() => _ = new AdminCredentials(storage, missing), "first startup fails closed without a password file");
        var secret = Path.Combine(root, "secret.txt");
        const string first = "first-password-123456";
        const string second = "second-password-654321";
        File.WriteAllText(secret, first + "\r\n");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["IPQUALITY_ADMIN_PASSWORD_FILE"] = secret }).Build();
        var original = new AdminCredentials(storage, settings);
        check(original.Verify("admin", first) && !original.Verify("admin", "incorrect") && !original.Verify("admin", null),
            "credential hash verifies only the original password");
        var authFile = Path.Combine(storage.DirectoryPath, "admin-auth.json");
        check(!File.ReadAllText(authFile).Contains(first), "plaintext password is never persisted to the data directory");
        File.WriteAllText(secret, second);
        var restarted = new AdminCredentials(storage, settings);
        check(restarted.Verify("admin", first) && !restarted.Verify("admin", second) && restarted.SecurityStamp == original.SecurityStamp,
            "restarting does not replace credentials from the bootstrap file");
        File.Delete(authFile);
        var reset = new AdminCredentials(storage, settings);
        check(reset.Verify("admin", second) && reset.SecurityStamp != original.SecurityStamp,
            "explicit credential reset changes the cookie security stamp");
        ServerStorage.WriteAtomic(authFile, new AdminHash(1, 210000, "invalid-base64", "invalid-base64"));
        reject(() => _ = new AdminCredentials(storage, settings), "malformed stored credentials fail closed without overwriting them");
        check(File.ReadAllText(authFile).Contains("invalid-base64"), "invalid credentials remain available for recovery");
    }

    private static async Task Runtime(string root, Action<bool, string> check)
    {
        async Task RejectAsync<TException>(Func<Task> action, string label) where TException : Exception
        {
            var rejected = false;
            try { await action(); } catch (TException) { rejected = true; }
            check(rejected, label);
        }
        var directory = Path.Combine(root, "runtime");
        using var storage = new ServerStorage(directory, "test");
        var resources = new MonitorResources(contexts: _ => new("test", "test"), probes: _ => new FakeProbe());
        await using var runtime = new MonitorRuntime(storage, resources);
        await RejectAsync<InvalidOperationException>(() => runtime.QueryAsync(_ => 1, CancellationToken.None),
            "queries cannot race database initialization");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => runtime.StartAsync(cancellation.Token),
                "cancelled host startup does not initialize a runtime");
        }
        await runtime.StartAsync();
        await runtime.StartAsync();
        check(runtime.Ready, "runtime startup is idempotent");
        await RejectAsync<ArgumentException>(() => runtime.AddAsync(null!, 0), "null target is a client validation error");
        await RejectAsync<ArgumentException>(() => runtime.SetPolicyAsync(null!, 0), "null policy is a client validation error");
        await RejectAsync<ArgumentException>(() => runtime.AddAsync(new(new string('x', 101), "127.0.0.1", "Tcp", 80), 0),
            "oversized target name is a client validation error");
        await RejectAsync<ArgumentException>(() => runtime.AddAsync(new("", "127.0.0.1", "invalid", 80), 0),
            "invalid mode is a client validation error");
        check(runtime.Configuration.Revision == 0 && runtime.Configuration.Profiles.Count == 0,
            "rejected input cannot mutate configuration");
        var id = await runtime.AddAsync(new("test", "127.0.0.1", "Tcp", 80), 0);
        await RejectAsync<ConfigurationConflictException>(() => runtime.AddAsync(new("stale", "127.0.0.2", "Tcp", 80), 0),
            "stale writers receive a configuration conflict");
        var copy = runtime.Configuration;
        copy.Profiles[0].Name = "mutated";
        check(runtime.Configuration.Profiles[0].Name == "test", "configuration snapshots cannot mutate shared profiles");
        await runtime.SetPolicyAsync(new GlobalMonitorSettings(EnableRoutes: false), runtime.Configuration.Revision);
        await runtime.SetRunningAsync(id, true, runtime.Configuration.Revision);
        check(runtime.States()[0].Running && runtime.Configuration.Profiles[0].ResumeOnLaunch,
            "start persists intent and owns a background collector");
        await runtime.SetRunningAsync(id, false, runtime.Configuration.Revision);
        check(!runtime.States()[0].Running && !runtime.Configuration.Profiles[0].ResumeOnLaunch,
            "pause persists intent and stops the collector");
        for (var i = 1; i < 20; i++)
            await runtime.AddAsync(new("extra", $"127.0.0.{i + 1}", "Tcp", 80), runtime.Configuration.Revision);
        var revision = runtime.Configuration.Revision;
        await RejectAsync<ArgumentException>(() => runtime.AddAsync(new("overflow", "127.0.0.30", "Tcp", 80), revision),
            "target limit is a client validation error");
        check(runtime.Configuration.Revision == revision && runtime.Configuration.Profiles.Count == 20,
            "target limit preserves the committed configuration");

        var timelines = new SharedTimelineReader(runtime);
        var target = runtime.RouteTarget(id);
        await RejectAsync<ArgumentException>(() => timelines.ReadAsync(target, 30, TimeZoneInfo.Utc, CancellationToken.None),
            "timeline refuses unbounded windows");
        using (var unblock = new ManualResetEventSlim())
        using (var viewer = new CancellationTokenSource())
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            int Hold(History history)
            {
                if (Interlocked.Increment(ref count) == 2) ready.SetResult();
                if (!unblock.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test slots were not released");
                return 1;
            }
            var blockers = new[] { runtime.QueryAsync(Hold, CancellationToken.None), runtime.QueryAsync(Hold, CancellationToken.None) };
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var cancelledView = timelines.ReadAsync(target, 1, TimeZoneInfo.Utc, viewer.Token);
                var survivingView = timelines.ReadAsync(target, 1, TimeZoneInfo.Utc, CancellationToken.None);
                viewer.Cancel();
                await RejectAsync<OperationCanceledException>(() => cancelledView,
                    "a cancelled viewer stops waiting without cancelling the shared timeline");
                unblock.Set();
                check((await survivingView.WaitAsync(TimeSpan.FromSeconds(5))).Days == 1,
                    "another viewer receives the shared timeline after cancellation");
            }
            finally { unblock.Set(); await Task.WhenAll(blockers); }
        }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = runtime.QueryAsync(history =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test reader was not released");
            history.RecordAsync(() => { }).GetAwaiter().GetResult();
            return 42;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? shutdown = null;
        try
        {
            shutdown = runtime.DisposeAsync().AsTask();
            check(!runtime.Ready && !shutdown.IsCompleted, "shutdown waits for an accepted query and withdraws readiness");
            await RejectAsync<InvalidOperationException>(() => runtime.QueryAsync(_ => 0, CancellationToken.None),
                "shutdown rejects new queries");
            var leased = false;
            try { using var second = new ServerStorage(directory, "second"); }
            catch (IOException) { leased = true; }
            check(leased, "dataset lease remains held while a query is draining");
        }
        finally { release.Set(); }
        check(await read.WaitAsync(TimeSpan.FromSeconds(5)) == 42,
            "accepted query completes before History is disposed");
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.DisposeAsync();
        using (var reopened = new ServerStorage(directory, "reopened"))
            check(reopened.Load().Revision == revision, "shutdown releases the lease without losing settings");
        await RejectAsync<InvalidOperationException>(() => runtime.StartAsync(), "a disposed runtime cannot restart");
    }

    private sealed class FakeProbe : IProbe
    {
        public Task<Sample> RunAsync(Target target, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Sample(DateTimeOffset.UtcNow, ProbeStatus.Success, .1, .1, "test-only"));
        }
    }
}
