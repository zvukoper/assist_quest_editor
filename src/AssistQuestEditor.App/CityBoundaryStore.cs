using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Хранилище черт городов: файл, который автор наполняет рисованием вручную.
///
/// Как и правки перекрёстков, файл лежит в пользовательских данных, а не в
/// публикации: черты — результат работы автора, они не должны пропадать при
/// обновлении EXE и не должны попадать в репозиторий.
///
/// Отличие от правок перекрёстков: там файл СТРОГО дополняет поставляемые данные
/// (исключить/добавить), а здесь он единственный источник — черт в публикации нет
/// вообще, потому что провести границу города можно только глазами.
/// </summary>
public sealed class CityBoundaryStore
{
    private const string FileName = "city_boundaries.json";

    private readonly string _path;

    public CityBoundaryStore(string? root = null)
    {
        _path = System.IO.Path.Combine(root ?? JunctionReviewStore.DefaultRoot, FileName);
    }

    /// <summary>
    /// Путь к файлу черт.
    ///
    /// Названо FilePath, а НЕ Path: свойство с именем Path затеняет
    /// System.IO.Path внутри класса (та же ловушка, что уже была в
    /// JunctionReviewStore).
    /// </summary>
    public string FilePath => _path;

    /// <summary>
    /// Читает черты. Отсутствие файла — не ошибка: значит автор ещё ничего не
    /// нарисовал, и критерии честно сообщат об этом.
    /// </summary>
    public CityBoundaryIndex Load()
    {
        if (!File.Exists(_path))
        {
            AppLogger.Info("CityBoundaryStore.Load(): файла черт нет, черты не заданы.", $"path={_path}");
            return CityBoundaryIndex.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (!document.RootElement.TryGetProperty("cities", out var cities) ||
                cities.ValueKind != JsonValueKind.Array)
            {
                AppLogger.Warn("CityBoundaryStore.Load(): в файле нет массива cities.", $"path={_path}");
                return CityBoundaryIndex.Empty;
            }

            var boundaries = new List<CityBoundary>();
            var rejected = 0;

            foreach (var entry in cities.EnumerateArray())
            {
                var boundary = ReadBoundary(entry);
                if (boundary is null)
                {
                    rejected++;
                    continue;
                }

                boundaries.Add(boundary);
            }

            AppLogger.Info("CityBoundaryStore.Load(): черты прочитаны.",
                $"cities={boundaries.Count}; rejected={rejected}; path={_path}");

            return new CityBoundaryIndex(boundaries);
        }
        catch (Exception ex)
        {
            // Испорченный файл не должен мешать работе редактора: критерии просто
            // сообщат, что черт нет.
            AppLogger.Error("CityBoundaryStore.Load(): ошибка чтения черт, работаем без них.", ex, $"path={_path}");
            return CityBoundaryIndex.Empty;
        }
    }

    /// <summary>
    /// Записывает черты ЦЕЛИКОМ.
    ///
    /// Замена всего файла, а не дописывание: черту одного города автор может
    /// перерисовать, и тогда старый контур обязан исчезнуть. Дописывание оставило
    /// бы два контура одного города, и перекрытие решалось бы порядком в файле —
    /// то есть случайностью.
    /// </summary>
    public void Save(IReadOnlyList<CityBoundary> boundaries)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var cities = boundaries
            .Where(boundary => boundary.IsValid)
            .Select(boundary => new
            {
                cityId = boundary.CityId,
                cityName = boundary.CityName,
                title = boundary.Title,
                vertexCount = boundary.Outline.Count,
                // Плоский массив x,z — тот же приём, что у дорог и перекрёстков:
                // контуры короткие, но единый стиль мира читается глазами.
                points = Flatten(boundary.Outline)
            })
            .ToArray();

        var payload = new
        {
            schemaVersion = 1,
            // Пояснение прямо в файле: его открывают руками, чтобы понять, что это.
            note = "Черты городов, нарисованные автором вручную. cities[].points — " +
                   "плоский массив x,z в метрах, контур замкнут неявно (последняя " +
                   "вершина соединяется с первой). Внутри черты — только один город.",
            layout = "x,z",
            units = "meters",
            cityCount = cities.Length,
            cities
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Кириллица в пояснении и названиях городов должна читаться, а не
            // превращаться в \uXXXX.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(_path, json);
        AppLogger.Info("CityBoundaryStore.Save(): черты записаны.",
            $"cities={cities.Length}; path={_path}");
    }

    private static CityBoundary? ReadBoundary(JsonElement entry)
    {
        var cityId = entry.TryGetProperty("cityId", out var idNode) ? idNode.GetString() : null;
        var cityName = entry.TryGetProperty("cityName", out var nameNode) ? nameNode.GetString() : null;

        if (!entry.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return null;

        // Сначала все числа подряд, потом пары: нечётное количество значений
        // означает повреждённый файл, и «дополнить нулём» значило бы нарисовать
        // автору вершину, которой он не ставил.
        var flat = new List<double>();
        foreach (var value in points.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number)
                return null;

            flat.Add(value.GetDouble());
        }

        if (flat.Count % 2 != 0)
            return null;

        var vertices = new List<CityBoundaryPoint>(flat.Count / 2);
        for (var i = 0; i + 1 < flat.Count; i += 2)
            vertices.Add(new CityBoundaryPoint(flat[i], flat[i + 1]));

        var boundary = new CityBoundary(cityId ?? string.Empty, cityName ?? string.Empty, vertices);
        return boundary.IsValid ? boundary : null;
    }

    private static double[] Flatten(IReadOnlyList<CityBoundaryPoint> outline)
    {
        var flat = new double[outline.Count * 2];
        for (var i = 0; i < outline.Count; i++)
        {
            flat[i * 2] = Math.Round(outline[i].X, 2);
            flat[i * 2 + 1] = Math.Round(outline[i].Z, 2);
        }

        return flat;
    }
}
