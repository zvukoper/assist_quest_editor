using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class QuestDefinitionLoader
{
    private const string RelativePath = "data/quests/tutorial_ruslan_shashlik.aqquest";

    /// <summary>
    /// Загружает учебный Quest Definition целиком (граф + метаданные документа).
    /// Возврат только QuestGraph терял Description/SceneIds уже на старте, поэтому
    /// первое же сохранение записывало документ без них.
    /// </summary>
    public static QuestDefinitionDocument LoadDocumentOrFallback()
    {
        var path = Path.Combine(AppContext.BaseDirectory, RelativePath);
        AppLogger.Info("QuestDefinitionLoader.LoadDocumentOrFallback()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            AppLogger.Warn("Учебный Quest Definition не найден. Используется встроенный starter graph.", path);
            return FallbackDocument();
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            var document = JsonSerializer.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path), options);

            if (document?.Definition?.Graph is null || document.SchemaVersion != 1 || !string.Equals(document.Format, "aqquest", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn("Учебный Quest Definition имеет неподдерживаемый формат.", $"path={path}; schema={document?.SchemaVersion}");
                return FallbackDocument();
            }

            AppLogger.Info("Учебный Quest Definition загружен.", $"questId={document.Definition.Id}; nodes={document.Definition.Graph.Nodes.Count}; connections={document.Definition.Graph.Connections.Count}");
            return document;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка загрузки учебного Quest Definition.", ex, $"path={path}");
            return FallbackDocument();
        }
    }

    private static QuestDefinitionDocument FallbackDocument()
    {
        var graph = QuestGraphFactory.CreateStarter();
        return new QuestDefinitionDocument(
            1,
            new QuestDefinition(graph.Id, graph.Name, string.Empty, graph, Array.Empty<string>()));
    }
}
