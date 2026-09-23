using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Загрузчик дорожной геометрии.
///
/// Файл <c>data/world/roads.json</c> — компактное представление исходной
/// GeoJSON-геометрии ETS2 Assist: плоский массив чисел
/// <c>[x1,z1,x2,z2, ...]</c> и 98 341 отрезок (~3.5 МБ против 45 МБ исходника).
/// Полный GeoJSON здесь не подходит: его разбор занимал бы секунды при старте, а
/// структура (uid, roadType, length) редактору не нужна.
/// </summary>
public static class RoadWorldDataLoader
{
    private const string RelativePath = "world/roads.json";

    /// <summary>Ожидаемая раскладка чисел в файле. Проверяется, чтобы не читать чужой формат вслепую.</summary>
    private const string ExpectedLayout = "x1,z1,x2,z2";

    private const int FloatsPerSegment = 4;

    public static RoadIndex Load()
    {
        // Каталог ресурсов, а не BaseDirectory: в single-file публикации базовый
        // каталог — это кэш распаковки, где могут лежать данные прошлых сборок.
        var path = Path.Combine(AppPaths.ResourceRoot, RelativePath);
        AppLogger.Info("RoadWorldDataLoader.Load()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            // Не ошибка: дороги — дополнительная возможность. Редактор обязан
            // работать без них, а критерий «рядом с дорогой» сообщит о причине.
            AppLogger.Warn("Файл дорог не найден.",
                $"Ожидался путь: {path}. Критерий «Рядом с дорогой» будет недоступен.");
            return new RoadIndex(Array.Empty<RoadSegment>());
        }

        try
        {
            var json = File.ReadAllText(path);
            var bundle = JsonSerializer.Deserialize<RoadBundle>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bundle?.Segments is null)
            {
                AppLogger.Warn("В JSON дорог отсутствует массив segments.");
                return new RoadIndex(Array.Empty<RoadSegment>());
            }

            // Раскладка проверяется явно: при другом формате числа читались бы со
            // сдвигом, и дороги молча оказались бы в неверных местах.
            if (!string.IsNullOrWhiteSpace(bundle.Layout) &&
                !bundle.Layout.Trim().Equals(ExpectedLayout, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Error("Неизвестная раскладка дорог.",
                    details: $"layout={bundle.Layout}; ожидалось {ExpectedLayout}");
                return new RoadIndex(Array.Empty<RoadSegment>());
            }

            var segments = new List<RoadSegment>(bundle.Segments.Length / FloatsPerSegment);
            var invalid = 0;

            for (var i = 0; i + FloatsPerSegment - 1 < bundle.Segments.Length; i += FloatsPerSegment)
            {
                var x1 = bundle.Segments[i];
                var z1 = bundle.Segments[i + 1];
                var x2 = bundle.Segments[i + 2];
                var z2 = bundle.Segments[i + 3];

                if (!double.IsFinite(x1) || !double.IsFinite(z1) ||
                    !double.IsFinite(x2) || !double.IsFinite(z2))
                {
                    invalid++;
                    continue;
                }

                segments.Add(new RoadSegment(x1, z1, x2, z2));
            }

            if (bundle.Segments.Length % FloatsPerSegment != 0)
            {
                AppLogger.Warn("Массив дорог не кратен четырём числам: хвост отброшен.",
                    $"length={bundle.Segments.Length}");
            }

            AppLogger.Info("Дорожная геометрия готова.",
                $"source={bundle.Source}; declared={bundle.SegmentCount}; " +
                $"parsed={segments.Count}; invalid={invalid}");

            return new RoadIndex(segments);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка загрузки/десериализации дорог.", ex, $"path={path}");
            return new RoadIndex(Array.Empty<RoadSegment>());
        }
    }

    private sealed record RoadBundle(
        int SchemaVersion,
        string Source,
        string Layout,
        int SegmentCount,
        double[]? Segments);
}
