using System.Text;

namespace TcpLatencyMonitor.App.Services;

public static class StartupDiagnostics
{
    private static readonly object SyncRoot = new();

    public static string LogPath => Path.Combine(AppPaths.DataDirectory, "startup.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [")
                .Append(Environment.ProcessId)
                .Append("] ")
                .AppendLine(message);

            for (var current = exception; current is not null; current = current.InnerException)
            {
                builder.Append(current.GetType().FullName)
                    .Append(" (0x")
                    .Append(current.HResult.ToString("X8"))
                    .Append("): ")
                    .AppendLine(current.Message)
                    .AppendLine(current.StackTrace);
            }

            lock (SyncRoot)
            {
                if(File.Exists(LogPath)&&new FileInfo(LogPath).Length>1048576)File.Move(LogPath,LogPath+".previous",true);
                File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never prevent the application from starting.
        }
    }
}
