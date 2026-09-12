namespace TcpLatencyMonitor.App.Services;

public static class AppPaths
{
    // A separate data directory is also useful for isolated integration tests.
    public static string DataDirectory { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("TCP_MONITOR_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"));
    public static string DatabasePath => Path.Combine(DataDirectory,"history.db");
    public static string SettingsPath => Path.Combine(DataDirectory,"settings.json");
}
