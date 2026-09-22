using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class SdoWorldDataLoader
{
    private const string RelativePath = "world/sdo_points.json";

    public static IReadOnlyList<WorldPoint> Load()
    {
        // Каталог ресурсов, а не BaseDirectory: в single-file публикации базовый
        // каталог — это кэш распаковки, где могут лежать данные прошлых сборок.
        var path = Path.Combine(AppPaths.ResourceRoot, RelativePath);
        AppLogger.Info("SdoWorldDataLoader.Load()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            AppLogger.Error("Файл СДО не найден.", details: $"Ожидался путь: {path}");
            return Array.Empty<WorldPoint>();
        }

        try
        {
            var json = File.ReadAllText(path);
            AppLogger.Info("Файл СДО прочитан.", $"bytes={new FileInfo(path).Length}; chars={json.Length}");

            var bundle = JsonSerializer.Deserialize<SdoBundle>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bundle is null)
            {
                AppLogger.Error("JSON СДО десериализован в null.");
                return Array.Empty<WorldPoint>();
            }

            AppLogger.Info("Метаданные СДО разобраны.",
                $"schema={bundle.SchemaVersion}; source={bundle.Source}; categoryCount={bundle.CategoryCount}; pointCount={bundle.PointCount}; parsedPoints={bundle.Points?.Count ?? 0}");

            if (bundle.Points is null)
            {
                AppLogger.Error("В JSON СДО отсутствует массив points.");
                return Array.Empty<WorldPoint>();
            }

            var valid = bundle.Points
                .Where(point =>
                    !string.IsNullOrWhiteSpace(point.Id) &&
                    double.IsFinite(point.X) &&
                    double.IsFinite(point.Y) &&
                    double.IsFinite(point.Z))
                .ToArray();

            AppLogger.Info("СДО-точки отфильтрованы.",
                $"input={bundle.Points.Count}; valid={valid.Length}; invalid={bundle.Points.Count - valid.Length}");

            var result = valid
                .Select(point => new WorldPoint(
                    point.Id,
                    string.IsNullOrWhiteSpace(point.Name) ? point.Category : point.Name,
                    point.Category,
                    new WorldCoordinate(point.X, point.Y, point.Z))
                {
                    Editable = false,
                    Color = string.IsNullOrWhiteSpace(point.Color) ? "#78c8f0" : point.Color
                })
                .ToArray();

            if (result.Length > 0)
            {
                AppLogger.Info("СДО world points готовы.",
                    $"count={result.Length}; X=[{result.Min(x => x.Position.X)},{result.Max(x => x.Position.X)}]; Y=[{result.Min(x => x.Position.Y)},{result.Max(x => x.Position.Y)}]; Z=[{result.Min(x => x.Position.Z)},{result.Max(x => x.Position.Z)}]");
            }
            else
            {
                AppLogger.Warn("После фильтрации не осталось СДО-точек.");
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка загрузки/десериализации СДО.", ex, $"path={path}");
            return Array.Empty<WorldPoint>();
        }
    }

    private sealed record SdoBundle(
        int SchemaVersion,
        string Source,
        string SourceRef,
        int CategoryCount,
        int PointCount,
        IReadOnlyDictionary<string, SdoCategory>? Categories,
        IReadOnlyList<SdoBundlePoint>? Points);

    private sealed record SdoCategory(string Name, string Color);

    private sealed record SdoBundlePoint(
        string Id,
        string Category,
        string Name,
        string Color,
        double X,
        double Y,
        double Z);
}
