using System.Text;

namespace AssistQuestEditor.App;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static string? _logPath;
    private static int _writesSuppressed;

    public static string LogPath => _logPath ??= ResolveLogPath();
    public static bool WritesSuppressed => Volatile.Read(ref _writesSuppressed) != 0;

    public static void SuppressWrites() =>
        Volatile.Write(ref _writesSuppressed, 1);

    public static void Info(string message, string? details = null) => Write("INFO", message, details);
    public static void Warn(string message, string? details = null) => Write("WARN", message, details);

    public static void Error(string message, Exception? exception = null, string? details = null)
    {
        var combined = details;
        if (exception is not null)
            combined = string.IsNullOrWhiteSpace(combined)
                ? exception.ToString()
                : combined + Environment.NewLine + exception;
        Write("ERROR", message, combined);
    }

    private static void Write(string level, string message, string? details)
    {
        if (WritesSuppressed)
        {
            return;
        }

        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("]")
                .Append(" [PID ").Append(Environment.ProcessId).Append("]")
                .Append(" [TID ").Append(Environment.CurrentManagedThreadId).Append("] ")
                .Append(message)
                .ToString();

            if (!string.IsNullOrWhiteSpace(details))
                line += " | " + details.Replace(Environment.NewLine, " \\n ");

            lock (Sync)
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // Диагностика не должна мешать работе приложения.
        }
    }

    private static string ResolveLogPath()
    {
        var roots = new[] { BuildInfo.RepositoryRoot, AppContext.BaseDirectory, Environment.CurrentDirectory }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var current = Path.GetFullPath(root);
            while (!string.IsNullOrWhiteSpace(current))
            {
                var memoryAi = Path.Combine(current, "MemoryAI");
                if (Directory.Exists(memoryAi))
                {
                    var logs = Path.Combine(memoryAi, "LOGS");
                    Directory.CreateDirectory(logs);
                    return Path.Combine(logs, "assist_quest_editor.log");
                }

                var parent = Directory.GetParent(current)?.FullName;
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent ?? string.Empty;
            }
        }

        var fallback = Path.Combine(AppContext.BaseDirectory, "MemoryAI", "LOGS");
        Directory.CreateDirectory(fallback);
        return Path.Combine(fallback, "assist_quest_editor.log");
    }
}
