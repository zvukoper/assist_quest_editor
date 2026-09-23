using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно ручной проверки перекрёстков.
///
/// Автоматический поиск находит перекрёстки по геометрии дорог и не знает об
/// уровнях: мост или эстакада пересекаются в плане, но повернуть там нельзя.
/// Здесь автор сам решает, какие узлы ложные (исключить), а какие поиск
/// пропустил (добавить). Правки сохраняются в файл и учитываются при поиске.
///
/// Рисуется только то, что нужно для проверки: дороги, подписи городов и сами
/// перекрёстки. Точки СДО намеренно не показываются — они мешали бы видеть
/// дорожную сетку, а к перекрёсткам отношения не имеют.
/// </summary>
public sealed class JunctionReviewForm : WebViewForm
{
    private readonly RoadIndex _roads;
    private readonly JunctionIndex _junctions;
    private readonly JunctionReviewStore _store = new();

    /// <summary>
    /// Показанные узлы: автоматический список минус исключённые плюс добавленные.
    /// </summary>
    private IReadOnlyList<JunctionPoint> _junctionsShown = Array.Empty<JunctionPoint>();

    private JunctionReview _review = JunctionReview.Empty;

    public JunctionReviewForm(RoadIndex roads, JunctionIndex junctions)
        : base("Проверка перекрёстков", "junctions.html", new Size(1500, 950), "junctions")
    {
        _roads = roads ?? throw new ArgumentNullException(nameof(roads));
        _junctions = junctions ?? throw new ArgumentNullException(nameof(junctions));
    }

    protected override void OnBrowserReady()
    {
        PostSnapshot("окно открыто");
    }

    protected override void OnWebMessage(string json)
    {
        string? action = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            action = root.TryGetProperty("action", out var actionNode) ? actionNode.GetString() : null;

            switch (action)
            {
                // Правки приходят списком координат целиком, а не по одной: автор
                // может за один проход отметить десяток узлов, и отправлять по
                // сообщению на каждую метку значило бы писать файл много раз.
                case "save_junction_review":
                    SaveReview(root);
                    break;

                // Пересобрать список: автор вернулся к окну после ручной правки
                // файла и хочет увидеть её результат.
                case "reload_junction_review":
                    _review = _store.Load();
                    PostSnapshot("перечитано с диска");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("JunctionReviewForm: ошибка команды.", ex, $"action={action ?? "<none>"}");
            PostJson(JsonSerializer.Serialize(new
            {
                type = "junction_review_error",
                message = "Ошибка команды проверки перекрёстков: " + ex.Message
            }));
        }
    }

    /// <summary>
    /// Сохраняет правки и сообщает результат окну.
    ///
    /// Список перекрёстков для отрисовки строится ТУТ ЖЕ из новых правок, а не
    /// берётся из присланного окном: тогда порядок и состав узлов гарантированно
    /// совпадают с тем, что записано на диск, и окно не может «запомнить»
    /// промежуточное состояние.
    /// </summary>
    private void SaveReview(JsonElement root)
    {
        var excluded = ReadPoints(root, "excluded");
        var added = ReadPoints(root, "added");

        _review = new JunctionReview(excluded, added);
        _store.Save(_review);

        AppLogger.Info("JunctionReviewForm: правки сохранены.",
            $"excluded={excluded.Count}; added={added.Count}; path={_store.FilePath}");

        PostSnapshot("правки сохранены");
        PostJson(JsonSerializer.Serialize(new
        {
            type = "junction_review_saved",
            excludedCount = excluded.Count,
            addedCount = added.Count,
            path = _store.FilePath
        }));
    }

    private static List<JunctionPoint> ReadPoints(JsonElement root, string property)
    {
        var result = new List<JunctionPoint>();
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in list.EnumerateArray())
        {
            var x = item.TryGetProperty("x", out var xNode) ? xNode.GetDouble() : double.NaN;
            var z = item.TryGetProperty("z", out var zNode) ? zNode.GetDouble() : double.NaN;

            if (double.IsFinite(x) && double.IsFinite(z))
                result.Add(new JunctionPoint(x, z));
        }

        return result;
    }

    /// <summary>
    /// Отправляет окну текущее состояние: дороги, города и итоговый список узлов.
    ///
    /// Дороги уходят целиком (98 341 отрезок, ~19 МБ JSON), потому что окно
    /// показывает карту сразу при открытии и не может дочитать файл само: страница
    /// живёт в опубликованном каталоге, а данные — рядом с EXE. Это допустимо
    /// именно здесь: окно открывается вручную и обновляется редко, в отличие от
    /// снимков Симулятора, которые уходят на каждое событие.
    /// </summary>
    private void PostSnapshot(string reason)
    {
        if (_junctions.IsEmpty)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "junction_review_empty",
                reason,
                message = "Список перекрёстков не загружен. Проверьте файл " +
                          "data/world/junctions.json рядом с приложением."
            }));
            return;
        }

        var detected = _junctions.ToPoints();
        _junctionsShown = _review.Apply(detected);

        PostJson(JsonSerializer.Serialize(new
        {
            type = "junction_review",
            reason,
            roads = _roads.ToFlatArray(),
            cities = LoadCities(),
            detectedCount = detected.Count,
            excludedCount = _review.Excluded.Count,
            addedCount = _review.Added.Count,
            reviewPath = _store.FilePath,
            // Правки уходят обратно в окно: итоговый список уже НЕ содержит
            // исключённые узлы, и вывести из него, что именно было исключено,
            // невозможно. Окно показывает зелёные добавленные точки, поэтому
            // ему нужны сами координаты.
            excluded = _review.Excluded.Select(point => new { x = point.X, z = point.Z }).ToArray(),
            added = _review.Added.Select(point => new { x = point.X, z = point.Z }).ToArray(),
            junctions = _junctionsShown.Select(point => new { x = point.X, z = point.Z }).ToArray()
        }));
    }

    /// <summary>
    /// Города: только имя и координаты.
    ///
    /// Точки не отдаются (в окне их не рисуют), поэтому из всего набора города
    /// едут облегчённым списком — это подписи-ориентиры, чтобы автор понимал, где
    /// именно находится проверяемый узел.
    /// </summary>
    private static object[] LoadCities()
    {
        try
        {
            var path = Path.Combine(AppPaths.ResourceRoot, "world", "cities.json");
            if (!File.Exists(path))
            {
                AppLogger.Warn("JunctionReviewForm: файл городов не найден.", path);
                return Array.Empty<object>();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("cities", out var cities) ||
                cities.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<object>();
            }

            var result = new List<object>();
            foreach (var city in cities.EnumerateArray())
            {
                var name = city.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                var x = city.TryGetProperty("x", out var xNode) ? xNode.GetDouble() : double.NaN;
                var z = city.TryGetProperty("z", out var zNode) ? zNode.GetDouble() : double.NaN;

                if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(x) || !double.IsFinite(z))
                    continue;

                result.Add(new { name, x, z });
            }

            return result.ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.Error("JunctionReviewForm: ошибка чтения городов.", ex);
            return Array.Empty<object>();
        }
    }
}
