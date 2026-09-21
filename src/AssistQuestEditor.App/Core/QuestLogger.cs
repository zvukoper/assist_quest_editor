using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AssistQuestEditor.App;

public static class QuestLogger
{
    private static readonly object Sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string LogPath
    {
        get
        {
            var directory = Path.GetDirectoryName(AppLogger.LogPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = !string.IsNullOrWhiteSpace(BuildInfo.RepositoryRoot)
                    ? Path.Combine(BuildInfo.RepositoryRoot, "MemoryAI", "LOGS")
                    : Path.Combine(AppContext.BaseDirectory, "MemoryAI", "LOGS");
                Directory.CreateDirectory(directory);
            }

            return Path.Combine(directory, "quest_runtime.log");
        }
    }

    public static void Info(string message, string? details = null) => Write("INFO", message, details);
    public static void Warn(string message, string? details = null) => Write("WARN", message, details);

    public static void Error(string message, Exception? exception = null, string? details = null)
    {
        var combined = details;
        if (exception is not null)
        {
            combined = string.IsNullOrWhiteSpace(combined)
                ? exception.ToString()
                : combined + Environment.NewLine + exception;
        }

        Write("ERROR", message, combined);
    }

    public static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    private static void Write(string level, string message, string? details)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] [QUEST] ")
                .Append(message)
                .ToString();

            if (!string.IsNullOrWhiteSpace(details))
                line += " | " + details.Replace(Environment.NewLine, " \\n ");

            lock (Sync)
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // Диагностический журнал не должен останавливать приложение.
        }
    }
}
