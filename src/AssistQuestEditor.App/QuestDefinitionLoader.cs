using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class QuestDefinitionLoader
{
    private const string RelativePath = "data/quests/tutorial_ruslan_shashlik.aqquest";

    public static QuestGraph LoadOrFallback()
    {
        var path = Path.Combine(AppContext.BaseDirectory, RelativePath);
        AppLogger.Info("QuestDefinitionLoader.LoadOrFallback()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            AppLogger.Warn("Учебный Quest Definition не найден. Используется встроенный starter graph.", path);
            return QuestGraphFactory.CreateStarter();
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            var document = JsonSerializer.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path), options);

            if (document?.Definition?.Graph is null || document.SchemaVersion != 1 || !document.Format.Equals("aqquest", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("Учебный Quest Definition имеет неподдерживаемый формат.", $"path={path}; schema={document?.SchemaVersion}");
                return QuestGraphFactory.CreateStarter();
            }

            AppLogger.Info("Учебный Quest Definition загружен.", $"questId={document.Definition.Id}; nodes={document.Definition.Graph.Nodes.Count}; connections={document.Definition.Graph.Connections.Count}");
            return document.Definition.Graph;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка загрузки учебного Quest Definition.", ex, $"path={path}");
            return QuestGraphFactory.CreateStarter();
        }
    }
}
