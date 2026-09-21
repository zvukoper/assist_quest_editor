using System.Text.Json;

namespace AssistQuestEditor.App;

public sealed record AppUiPreferences(bool JournalDetached = false, string? LastQuestDefinitionPath = null);

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
            return JsonSerializer.Deserialize<AppUiPreferences>(File.ReadAllText(FilePath), JsonOptions) ?? new AppUiPreferences();
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
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Не удалось сохранить настройки интерфейса.", ex.Message);
        }
    }
}
