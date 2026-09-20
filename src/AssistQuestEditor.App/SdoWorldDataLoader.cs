using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public static class SdoWorldDataLoader
{
    private const string RelativePath = "data/world/sdo_points.json";

    public static IReadOnlyList<WorldPoint> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, RelativePath);
        if (!File.Exists(path))
        {
            return Array.Empty<WorldPoint>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var bundle = JsonSerializer.Deserialize<SdoBundle>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bundle?.Points is null)
            {
                return Array.Empty<WorldPoint>();
            }

            return bundle.Points
                .Where(point =>
                    !string.IsNullOrWhiteSpace(point.Id) &&
                    double.IsFinite(point.X) &&
                    double.IsFinite(point.Y) &&
                    double.IsFinite(point.Z))
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
        }
        catch
        {
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

    private sealed record SdoCategory(
        string Name,
        string Color);

    private sealed record SdoBundlePoint(
        string Id,
        string Category,
        string Name,
        string Color,
        double X,
        double Y,
        double Z);
}
