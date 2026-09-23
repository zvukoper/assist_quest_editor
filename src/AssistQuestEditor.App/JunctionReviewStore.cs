using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Пользовательская правка списка перекрёстков: что исключить и что добавить.
///
/// Зачем нужна ручная правка, если есть автоматический поиск: алгоритм находит
/// перекрёстки по геометрии и не видит уровней. Мост или эстакада пересекаются в
/// плане, но повернуть там нельзя — такие узлы надо исключать. И наоборот,
/// примыкание, которое в данных нарисовано с большим разрывом, поиск пропустит —
/// его надо добавить руками.
///
/// Файл лежит рядом с данными пользователя, а не в публикации: это результат
/// работы автора, он не должен пропадать при обновлении EXE и не должен
/// попадать в репозиторий.
/// </summary>
public sealed class JunctionReviewStore
{
    private const string FileName = "junctions.json";

    private readonly string _path;

    public JunctionReviewStore(string? root = null)
    {
        _path = System.IO.Path.Combine(root ?? DefaultRoot, FileName);
    }

    /// <summary>
    /// Путь к файлу правок.
    ///
    /// Названо FilePath, а НЕ Path: свойство с именем Path затеняет
    /// System.IO.Path внутри класса, и Path.Combine перестаёт компилироваться
    /// (тот же класс ловушки, что с System в DataChannels.cs).
    /// </summary>
    public string FilePath => _path;

    /// <summary>
    /// Каталог правок. Отдельно от каталога ресурсов: правки — пользовательские
    /// данные, а не поставляемый контент.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppContext.BaseDirectory;

            return System.IO.Path.Combine(localAppData, "Assist Quest Editor", "world");
        }
    }

    /// <summary>
    /// Читает правки. Отсутствие файла — не ошибка: значит автор ещё ничего
    /// не исключал и не добавлял.
    /// </summary>
    public JunctionReview Load()
    {
        if (!File.Exists(_path))
        {
            AppLogger.Info("JunctionReviewStore.Load(): файла правок нет, правки пусты.", $"path={_path}");
            return JunctionReview.Empty;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var bundle = JsonSerializer.Deserialize<ReviewBundle>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var excluded = ReadPoints(bundle?.Excluded);
            var added = ReadPoints(bundle?.Added);

            AppLogger.Info("JunctionReviewStore.Load(): правки прочитаны.",
                $"excluded={excluded.Count}; added={added.Count}; path={_path}");

            return new JunctionReview(excluded, added);
        }
        catch (Exception ex)
        {
            // Испорченный файл правок не должен мешать работе: поиск идёт по
            // автоматическому списку, как будто правок нет.
            AppLogger.Error("JunctionReviewStore.Load(): ошибка чтения правок, работаем без них.", ex, $"path={_path}");
            return JunctionReview.Empty;
        }
    }

    /// <summary>
    /// Записывает правки. Формат — плоские массивы координат, как у дорог:
    /// список правок короткий, но единый стиль мира упрощает чтение глазами.
    /// </summary>
    public void Save(JunctionReview review)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = new
        {
            schemaVersion = 1,
            // Пояснение прямо в файле: его открывают руками, чтобы понять, что это.
            note = "Ручная правка списка перекрёстков: excluded — ложные (мосты, " +
                   "эстакады), added — пропущенные поиском. Координаты x,z в метрах.",
            layout = "x,z",
            units = "meters",
            excludedCount = review.Excluded.Count,
            addedCount = review.Added.Count,
            excluded = Flatten(review.Excluded),
            added = Flatten(review.Added)
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Кириллица в пояснении должна читаться, а не превращаться в \uXXXX.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(_path, json);
        AppLogger.Info("JunctionReviewStore.Save(): правки записаны.",
            $"excluded={review.Excluded.Count}; added={review.Added.Count}; path={_path}");
    }

    private static double[] Flatten(IReadOnlyList<JunctionPoint> points)
    {
        var result = new double[points.Count * 2];
        for (var i = 0; i < points.Count; i++)
        {
            result[i * 2] = Math.Round(points[i].X, 2);
            result[i * 2 + 1] = Math.Round(points[i].Z, 2);
        }

        return result;
    }

    private static List<JunctionPoint> ReadPoints(double[]? values)
    {
        var result = new List<JunctionPoint>();
        if (values is null)
            return result;

        for (var i = 0; i + 1 < values.Length; i += 2)
        {
            if (!double.IsFinite(values[i]) || !double.IsFinite(values[i + 1]))
                continue;

            result.Add(new JunctionPoint(values[i], values[i + 1]));
        }

        return result;
    }

    private sealed record ReviewBundle(double[]? Excluded, double[]? Added);
}
