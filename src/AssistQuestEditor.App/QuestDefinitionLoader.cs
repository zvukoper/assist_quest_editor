using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class QuestDefinitionLoader
{
    private const string RelativePath = "quests/tutorial_ruslan_shashlik.aqquest";
    private const string RelativeDirectory = "quests";

    /// <summary>
    /// Загружает все canonical Quest Definitions из ресурсного каталога.
    /// Каждый .aqquest является самостоятельным документом.
    ///
    /// Пользовательская папка проверяется на существование, но НЕ подменяет
    /// поставку при пустом каталоге: пустая папка миров — это стартовая фаза
    /// («мир не создан»), а не повод показать учебный квест как будто он есть в
    /// мире. Иначе список выглядел бы наполненным при отсутствии миров.
    /// </summary>
    public static IReadOnlyList<QuestDefinition> LoadAllOrFallback()
    {
        var sourceDirectory = Path.Combine(AppPaths.ResourceRoot, RelativeDirectory);
        var directory = Directory.Exists(AppPaths.WorldsRoot) &&
                        Directory.EnumerateFiles(AppPaths.WorldsRoot, "*.aqquest", SearchOption.AllDirectories).Any()
            ? AppPaths.WorldsRoot
            : sourceDirectory;

        if (!Directory.Exists(directory))
            return new[] { LoadDocumentOrFallback().Definition };

        var definitions = new List<QuestDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.aqquest", SearchOption.AllDirectories).OrderBy(path => path))
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
    /// Порядок поиска: сначала миры пользователя (там живёт актуальный контент),
    /// затем поставка. Путь строится от каталога ресурсов, а не от
    /// <see cref="AppContext.BaseDirectory"/>: в single-file публикации базовый
    /// каталог — это кэш распаковки, где могут лежать ресурсы прошлых сборок.
    /// </summary>
    public static QuestDefinitionDocument LoadDocumentOrFallback()
    {
        var sourcePath = Path.Combine(AppPaths.ResourceRoot, RelativePath);
        var userPath = Directory.Exists(AppPaths.WorldsRoot)
            ? Directory.EnumerateFiles(
                    AppPaths.WorldsRoot,
                    Path.GetFileName(RelativePath),
                    SearchOption.AllDirectories)
                .FirstOrDefault()
            : null;
        var path = userPath ?? sourcePath;

        AppLogger.Info(
            "QuestDefinitionLoader.LoadDocumentOrFallback()",
            $"path={path}; userExists={userPath is not null}; sourceExists={File.Exists(sourcePath)}");

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

    /// <summary>
    /// Собирает документ квеста из графа с проставленным авторством и родителями.
    ///
    /// Один конструктор документа на всё приложение: «кто и когда создал» и
    /// «в каком мире и кампании живёт» обязаны проставляться одинаково, иначе
    /// одни файлы получат подпись, а другие нет.
    /// </summary>
    public static QuestDefinitionDocument CreateDocument(
        QuestGraph graph,
        string author,
        DateTimeOffset moment,
        string worldId,
        string campaignId,
        QuestDefinition? source = null)
    {
        var baseDefinition = source is null
            ? new QuestDefinition(graph.Id, graph.Name, string.Empty, graph, Array.Empty<string>())
            : source with { Graph = graph };

        // Авторство сохраняется, если документ уже существовал: «создал» при
        // перезаписи не меняется, а «изменил» обновляется.
        var metadata = baseDefinition.Metadata is { CreatedBy: not null } existing
            ? existing.WithModified(author, moment)
            : new ResourceMetadata().WithCreated(author, moment);

        return new QuestDefinitionDocument(
            1,
            "aqquest",
            baseDefinition with
            {
                WorldId = worldId,
                CampaignId = campaignId,
                Metadata = metadata
            });
    }

    private static QuestDefinitionDocument FallbackDocument()
    {
        var graph = QuestGraphFactory.CreateStarter();
        // activation намеренно не задаётся (Manual): это стартовый документ для отсутствующего
        // или повреждённого файла, привязывать его к точке мира наугад нельзя. В отличие от
        // описания и сцен, здесь null не является потерей данных — файла нет.
        return new QuestDefinitionDocument(
            1,
            "aqquest",
            new QuestDefinition(graph.Id, graph.Name, string.Empty, graph, Array.Empty<string>()));
    }
}
