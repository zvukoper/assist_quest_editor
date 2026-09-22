using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class CityWorldDataLoader
{
    private const string RelativePath = "world/cities.json";

    public static IReadOnlyList<WorldPoint> Load()
    {
        // Каталог ресурсов, а не BaseDirectory: см. AppPaths о single-file публикации.
        var path = Path.Combine(AppPaths.ResourceRoot, RelativePath);
        AppLogger.Info("CityWorldDataLoader.Load()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            AppLogger.Warn("Файл городов не найден.", $"Ожидался путь: {path}");
            return Array.Empty<WorldPoint>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var bundle = JsonSerializer.Deserialize<CityBundle>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bundle?.Cities is null)
            {
                AppLogger.Warn("В JSON городов отсутствует массив cities.");
                return Array.Empty<WorldPoint>();
            }

            var cities = bundle.Cities
                .Where(city =>
                    !string.IsNullOrWhiteSpace(city.Id) &&
                    !string.IsNullOrWhiteSpace(city.Name) &&
                    double.IsFinite(city.X) &&
                    double.IsFinite(city.Y) &&
                    double.IsFinite(city.Z))
                .Select(city => new WorldPoint(
                    city.Id,
                    city.Name,
                    "Города",
                    new WorldCoordinate(city.X, city.Y, city.Z))
                {
                    Editable = false,
                    Color = "#e2c85f",
                    IsCity = true
                })
                .ToArray();

            AppLogger.Info("Города world points готовы.",
                $"input={bundle.Cities.Count}; valid={cities.Length}; invalid={bundle.Cities.Count - cities.Length}");
            return cities;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка загрузки/десериализации городов.", ex, $"path={path}");
            return Array.Empty<WorldPoint>();
        }
    }

    private sealed record CityBundle(
        int SchemaVersion,
        string Source,
        string SourceRef,
        int CityCount,
        IReadOnlyList<CityPoint>? Cities);

    private sealed record CityPoint(
        string Id,
        string Name,
        string Country,
        double X,
        double Y,
        double Z);
}
