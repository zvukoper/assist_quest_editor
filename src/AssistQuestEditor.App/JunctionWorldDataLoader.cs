using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Загрузчик перекрёстков дорожной сети.
///
/// Файл <c>data/world/junctions.json</c> — результат нодировки дорог скриптом
/// <c>ci/import_junctions.mjs</c>: плоский массив координат <c>[x,z, ...]</c> и
/// 3 483 перекрёстка (~60 КБ). Перекрёстки НЕ считаются при старте редактора:
/// их поиск требует рассечения 98 341 отрезка во всех пересечениях и сборки
/// графа, это сотни миллисекунд и десятки мегабайт промежуточных структур.
/// </summary>
public static class JunctionWorldDataLoader
{
    private const string RelativePath = "world/junctions.json";

    /// <summary>Ожидаемая раскладка чисел. Проверяется, чтобы не читать чужой формат вслепую.</summary>
    private const string ExpectedLayout = "x,z";

    private const int FloatsPerJunction = 2;

    public static JunctionIndex Load()
    {
        // Каталог ресурсов, а не BaseDirectory: в single-file публикации базовый
        // каталог — это кэш распаковки, где могут лежать данные прошлых сборок.
        var path = Path.Combine(AppPaths.ResourceRoot, RelativePath);
        AppLogger.Info("JunctionWorldDataLoader.Load()", $"path={path}; exists={File.Exists(path)}");

        if (!File.Exists(path))
        {
            // Не ошибка: перекрёстки — дополнительная возможность. Редактор обязан
            // работать без них, а критерий сообщит о причине.
            AppLogger.Warn("Файл перекрёстков не найден.",
                $"Ожидался путь: {path}. Критерий «В радиусе от перекрёстка» будет недоступен.");
            return new JunctionIndex(Array.Empty<JunctionPoint>());
        }

        try
        {
            var json = File.ReadAllText(path);
            var bundle = JsonSerializer.Deserialize<JunctionBundle>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (bundle?.Points is null)
            {
                AppLogger.Warn("В JSON перекрёстков отсутствует массив points.");
                return new JunctionIndex(Array.Empty<JunctionPoint>());
            }

            // Раскладка проверяется явно: при другом формате числа читались бы со
            // сдвигом, и перекрёстки молча оказались бы в неверных местах.
            if (!string.IsNullOrWhiteSpace(bundle.Layout) &&
                !bundle.Layout.Trim().Equals(ExpectedLayout, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Error("Неизвестная раскладка перекрёстков.",
                    details: $"layout={bundle.Layout}; ожидалось {ExpectedLayout}");
                return new JunctionIndex(Array.Empty<JunctionPoint>());
            }

            var junctions = new List<JunctionPoint>(bundle.Points.Length / FloatsPerJunction);
            var invalid = 0;

            for (var i = 0; i + FloatsPerJunction - 1 < bundle.Points.Length; i += FloatsPerJunction)
            {
                var x = bundle.Points[i];
                var z = bundle.Points[i + 1];

                if (!double.IsFinite(x) || !double.IsFinite(z))
                {
                    invalid++;
                    continue;
                }

                junctions.Add(new JunctionPoint(x, z));
            }

            if (bundle.Points.Length % FloatsPerJunction != 0)
            {
                AppLogger.Warn("Массив перекрёстков не кратен двум числам: хвост отброшен.",
                    $"length={bundle.Points.Length}");
            }

            AppLogger.Info("Перекрёстки готовы.",
                $"source={bundle.Source}; declared={bundle.JunctionCount}; " +
                $"parsed={junctions.Count}; invalid={invalid}; method={bundle.Method}");

            return new JunctionIndex(junctions);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка загрузки/десериализации перекрёстков.", ex, $"path={path}");
            return new JunctionIndex(Array.Empty<JunctionPoint>());
        }
    }

    private sealed record JunctionBundle(
        int SchemaVersion,
        string Source,
        string Method,
        string Layout,
        int JunctionCount,
        double[]? Points);
}
