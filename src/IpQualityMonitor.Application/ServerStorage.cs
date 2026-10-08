using System.Text.Json;
using TcpLatencyMonitor.Core;

namespace IpQualityMonitor.Application;

public sealed record ServerConfiguration(int SchemaVersion, long Revision, GlobalMonitorSettings Monitoring,
    List<TargetProfile> Profiles)
{
    public AnnotationSettings Annotations { get; init; } = new();
    public static ServerConfiguration Empty => new(1, 0, new(), new());
    public ServerConfiguration Copy() => this with { Profiles = Profiles.Select(p => p.Copy()).ToList() };
    public void Validate()
    {
        if (SchemaVersion != 1 || Revision < 0) throw new InvalidDataException("不支持的服务器配置版本。");
        if (Monitoring is null || Profiles is null || Profiles.Count > 20)
            throw new InvalidDataException("预览版最多支持 20 个目标。");
        Monitoring.Validate();
        (Annotations ?? throw new InvalidDataException("定位配置无效。")).Validate();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Profiles)
        {
            if (p is null || !Guid.TryParseExact(p.Id, "N", out _) || !ids.Add(p.Id))
                throw new InvalidDataException("目标 ID 无效或重复。");
            if (p.Name is null || p.Name.Length > 100 || p.Address is null || p.Address.Length > 100)
                throw new InvalidDataException("目标名称或地址过长。");
            if (p.PreviousMeasurements is null) throw new InvalidDataException("历史配置无效。");
            p.Validate();
            if (GlobalMonitorSettings.From(p) != Monitoring)
                throw new InvalidDataException("目标参数与统一设置不一致。");
        }
    }
}

public sealed record ProbeSite(int SchemaVersion, string Id, string Name, string NetworkMode, DateTimeOffset CreatedAt);
public sealed class ConfigurationConflictException() : Exception("配置已被其他页面更新，请重新读取后再提交。");

/// <summary>Only server-owned data is accepted. Never initialize/prune an unmarked Windows database.</summary>
public sealed class ServerStorage : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly FileStream _lease;
    public string DirectoryPath { get; }
    public ProbeSite Site { get; }
    public string ConfigurationPath => Path.Combine(DirectoryPath, "settings.server.json");
    public ServerStorage(string path, string name)
    {
        DirectoryPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
        else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _lease = new FileStream(Path.Combine(DirectoryPath, "server.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var manifest = Path.Combine(DirectoryPath, "instance.json");
            if (!File.Exists(manifest))
            {
                if (HasDatabaseFiles() || File.Exists(ConfigurationPath) ||
                    File.Exists(Path.Combine(DirectoryPath, "settings.json")))
                    throw new InvalidDataException("发现未标记的现有数据。请为服务器使用新目录，不要直接挂载 Windows data。");
                Site = new(1, Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? "Docker bridge" : name,
                    "bridge", DateTimeOffset.UtcNow);
                WriteAtomic(manifest, Site);
            }
            else
            {
                Site = Read<ProbeSite>(manifest);
                if (Site.SchemaVersion != 1 || !Guid.TryParseExact(Site.Id, "N", out _) || Site.NetworkMode != "bridge")
                    throw new InvalidDataException("不支持的采集点数据格式。");
                if (!File.Exists(ConfigurationPath) && HasDatabaseFiles())
                    throw new InvalidDataException("服务器配置丢失；请从完整备份恢复，不会自动覆盖历史。");
            }
        }
        catch { _lease.Dispose(); throw; }
    }
    private bool HasDatabaseFiles() => new[] { "history.db", "route-analysis.db" }
        .Any(name => new[] { "", "-wal", "-shm", "-journal" }
            .Any(suffix => File.Exists(Path.Combine(DirectoryPath, name + suffix))));
    public ServerConfiguration Load()
    {
        var config = File.Exists(ConfigurationPath) ? Read<ServerConfiguration>(ConfigurationPath) : ServerConfiguration.Empty;
        config.Validate();
        if (!File.Exists(ConfigurationPath)) WriteAtomic(ConfigurationPath, config);
        return config;
    }
    public void Save(ServerConfiguration config) { config.Validate(); WriteAtomic(ConfigurationPath, config); }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"空配置文件：{Path.GetFileName(path)}");
    public static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            // Credentials and configuration must never be world-readable, even before the rename.
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() => _lease.Dispose();
}

