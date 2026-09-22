using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class SceneCatalogLoader
{
    private const string RelativeDirectory = "data/scenes";

    public static SceneCatalog Load()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, RelativeDirectory);
        AppLogger.Info("SceneCatalogLoader.Load()", $"directory={directory}; exists={Directory.Exists(directory)}");

        if (!Directory.Exists(directory))
        {
            AppLogger.Warn("Каталог Scene Definition не найден.", $"Ожидался путь: {directory}");
            return SceneCatalogFactory.CreateStarter();
        }

        var scenes = new List<SceneDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.aqscene", SearchOption.TopDirectoryOnly).OrderBy(path => path))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<SceneDefinitionDocument>(File.ReadAllText(path));
                if (document?.Definition is null)
                {
                    AppLogger.Warn("Scene Definition пропущена: пустой документ.", $"path={path}");
                    continue;
                }
                if (document.SchemaVersion != 1 || !string.Equals(document.Format, "aqscene", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn("Scene Definition пропущена: неподдерживаемая schemaVersion.", $"path={path}; schema={document.SchemaVersion}");
                    continue;
                }
                scenes.Add(document.Definition);
                AppLogger.Info("Scene Definition загружена.", $"id={document.Definition.Id}; path={path}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Ошибка загрузки Scene Definition.", ex, $"path={path}");
            }
        }

        if (scenes.Count == 0)
        {
            AppLogger.Warn("В каталоге нет пригодных Scene Definition. Используется встроенный fixture.");
            return SceneCatalogFactory.CreateStarter();
        }

        AppLogger.Info("Scene catalog готов.", $"sceneCount={scenes.Count}");
        return new SceneCatalog(scenes);
    }
}
