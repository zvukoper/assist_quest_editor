using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class QuestDefinitionLoader
{
    private const string RelativePath = "quests/tutorial_ruslan_shashlik.aqquest";
    private const string RelativeDirectory = "quests";

    /// <summary>
    /// Загружает все canonical Quest Definitions из resource-каталога.
    /// Каждый .aqquest является самостоятельным документом.
    /// </summary>
    public static IReadOnlyList<QuestDefinition> LoadAllOrFallback()
    {
        var directory = Path.Combine(AppPaths.ResourceRoot, RelativeDirectory);
        if (!Directory.Exists(directory))
            return new[] { LoadDocumentOrFallback().Definition };

        var definitions = new List<QuestDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.aqquest", SearchOption.TopDirectoryOnly).OrderBy(path => path))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path));
                if (document?.Definition?.Graph is null ||
                    document.SchemaVersion != 1 ||
                    !string.Equals(document.Format, "aqquest", StringComparison.OrdinalIgnoreCase))
                    continue;

                definitions.Add(document.Definition);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Ошибка загрузки Quest Definition в Runtime catalog.", ex, path);
            }
        }

        return definitions.Count == 0
            ? new[] { LoadDocumentOrFallback().Definition }
            : definitions;
    }

    /// <summary>
    /// Загружает учебный Quest Definition целиком (граф + метаданные документа).
    /// Возврат только QuestGraph терял Description/SceneIds уже на старте, поэтому
    /// первое же сохранение записывало документ без них.
    ///
    /// Путь строится от каталога ресурсов, а не от <see cref="AppContext.BaseDirectory"/>:
    /// в single-file публикации базовый каталог — это кэш распаковки, где могут
    /// лежать ресурсы прошлых сборок.
    /// </summary>
    public static QuestDefinitionDocument LoadDocumentOrFallback()
    {
        var path = Path.Combine(AppPaths.ResourceRoot, RelativePath);
        AppLogger.Info("QuestDefinitionLoader.LoadDocumentOrFallback()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            AppLogger.Warn("Учебный Quest Definition не найден. Используется встроенный starter graph.", path);
            return FallbackDocument();
        }

        try
        {
            // Чтение и запись идут через один контракт формата resource-файла.
            var document = ResourceJsonFormat.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path));

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
            "aqquest",
            new QuestDefinition(graph.Id, graph.Name, string.Empty, graph, Array.Empty<string>()));
    }
}
