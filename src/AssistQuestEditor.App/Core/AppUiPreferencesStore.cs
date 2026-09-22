using System.Text.Json;

namespace AssistQuestEditor.App;

public sealed record WindowGeometry(
    int X,
    int Y,
    int Width,
    int Height,
    string WindowState = "Normal");

public sealed record AppUiPreferences(
    bool JournalDetached = true,
    string? LastQuestDefinitionPath = null,
    string? LastSceneDefinitionPath = null,
    Dictionary<string, WindowGeometry>? Windows = null,
    List<string>? RecentQuestFiles = null,
    List<string>? RecentSceneFiles = null);

public static class AppUiPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
            return Path.Combine(root, "AssistQuestEditor", "ui-settings.json");
        }
    }

    public static AppUiPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppUiPreferences();

            var preferences = JsonSerializer.Deserialize<AppUiPreferences>(
                File.ReadAllText(FilePath),
                JsonOptions) ?? new AppUiPreferences();

            return preferences with
            {
                Windows = preferences.Windows ?? new Dictionary<string, WindowGeometry>(StringComparer.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Не удалось загрузить настройки интерфейса.", ex.Message);
            return new AppUiPreferences();
        }
    }

    public static void Save(AppUiPreferences preferences)
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var normalized = preferences with
            {
                Windows = preferences.Windows is null
                    ? new Dictionary<string, WindowGeometry>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, WindowGeometry>(preferences.Windows, StringComparer.OrdinalIgnoreCase)
            };

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Не удалось сохранить настройки интерфейса.", ex.Message);
        }
    }

    public static void SaveWindow(string key, WindowGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        var preferences = Load();
        var windows = new Dictionary<string, WindowGeometry>(
            preferences.Windows ?? new Dictionary<string, WindowGeometry>(),
            StringComparer.OrdinalIgnoreCase)
        {
            [key] = geometry
        };

        Save(preferences with { Windows = windows });
    }

    public static bool TryGetWindow(string key, out WindowGeometry geometry)
    {
        var windows = Load().Windows;
        if (windows is not null && windows.TryGetValue(key, out var value))
        {
            geometry = value;
            return true;
        }

        geometry = default!;
        return false;
    }

    public static void ClearWindowGeometry()
    {
        var preferences = Load();
        Save(preferences with
        {
            Windows = new Dictionary<string, WindowGeometry>(StringComparer.OrdinalIgnoreCase)
        });
    }

    /// <summary>
    /// Сохраняет список недавно открытых ресурсов указанного вида.
    /// Ключ — <see cref="RecentFileKind"/>; неизвестный вид игнорируется,
    /// чтобы настройки не обрастали произвольными ключами.
    /// </summary>
    public static void SaveRecentFiles(string kind, IReadOnlyList<string> paths)
    {
        var preferences = Load();
        var stored = paths?.ToList() ?? new List<string>();

        Save(kind switch
        {
            RecentFileKind.Quest => preferences with { RecentQuestFiles = stored },
            RecentFileKind.Scene => preferences with { RecentSceneFiles = stored },
            _ => preferences
        });
    }

    /// <summary>Читает сохранённый список недавних ресурсов указанного вида.</summary>
    public static IReadOnlyList<string> LoadRecentFiles(string kind)
    {
        var preferences = Load();

        return kind switch
        {
            RecentFileKind.Quest => preferences.RecentQuestFiles ?? new List<string>(),
            RecentFileKind.Scene => preferences.RecentSceneFiles ?? new List<string>(),
            _ => new List<string>()
        };
    }
}

/// <summary>Виды ресурсов, для которых ведётся история последних открытий.</summary>
public static class RecentFileKind
{
    public const string Quest = "quest";
    public const string Scene = "scene";
}
