using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно рисования черты города.
///
/// Единственное взаимодействие с картой — рисование многоугольника: автор
/// обводит область города, замыкает контур на начальной точке и сохраняет.
/// Точки не выделяются и не редактируются намеренно — окно существует ради одной
/// задачи, и любое лишнее действие с картой отнимало бы внимание от контура.
///
/// Внутри нарисованного контура обязан оказаться ровно один город: по нему и
/// определяется название черты. Если городов внутри нет — автор промахнулся
/// мимо города; если больше одного — область захватила соседний населённый пункт.
/// Оба случая отклоняются с объяснением, а не молча: черта с неверным названием
/// потом ищется критерием «в черте города X» и не находится.
///
/// Рисуется то же, что и в окне перекрёстков (дороги, города, все точки мира),
/// но с подписями: без названий населённых пунктов понять, какой город обводишь,
/// нельзя.
/// </summary>
public sealed class CityBoundaryForm : WebViewForm
{
    private readonly RoadIndex _roads;
    private readonly CityBoundaryStore _store = new();

    /// <summary>
    /// Точки мира для показа. Хранятся целиком, потому что окно живёт долго и
    /// перерисовывается на каждое сохранение: перечитывать мир из каналов при
    /// каждом действии значило бы зависеть от того, открыт ли Симулятор.
    /// </summary>
    private readonly IReadOnlyList<WorldPoint> _worldPoints;

    private CityBoundaryIndex _boundaries;

    public CityBoundaryForm(RoadIndex roads, IReadOnlyList<WorldPoint>? worldPoints = null)
        : base("Черты городов", "cityBoundaries.html", new Size(1500, 950), "cityBoundaries")
    {
        _roads = roads ?? throw new ArgumentNullException(nameof(roads));
        _worldPoints = worldPoints ?? Array.Empty<WorldPoint>();
        _boundaries = _store.Load();
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
                // Сохранение приходит ЦЕЛИКОМ, а не по одной черте: файл
                // перезаписывается, и две пары запросов подряд (сохранить A,
                // сохранить B) оставили бы в файле только B.
                case "save_city_boundary":
                    SaveBoundary(root);
                    break;

                case "delete_city_boundary":
                    DeleteBoundary(root);
                    break;

                // Перечитать файл: автор вернулся к окну после правки файла руками.
                case "reload_city_boundaries":
                    _boundaries = _store.Load();
                    PostSnapshot("перечитано с диска");
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("CityBoundaryForm: ошибка команды.", ex, $"action={action ?? "<none>"}");
            PostJson(JsonSerializer.Serialize(new
            {
                type = "city_boundary_error",
                message = "Ошибка команды черт городов: " + ex.Message
            }));
        }
    }

    /// <summary>
    /// Определяет город внутри контура и записывает черту.
    ///
    /// Город ищется ТУТ, а не в окне: это проверка ДАННЫХ (какие точки мира внутри
    /// контура), а не отрисовки, и решать её должен тот, у кого есть полный список
    /// точек. Окно присылает только контур.
    /// </summary>
    private void SaveBoundary(JsonElement root)
    {
        var outline = ReadOutline(root);
        var boundaryShape = new CityBoundary(string.Empty, string.Empty, outline);

        if (!boundaryShape.IsValid)
        {
            PostRejected("Контур не годен: нужно не меньше трёх вершин с числовыми координатами.");
            return;
        }

        var inside = _worldPoints
            .Where(point => point.IsCity && boundaryShape.Contains(point.Position.X, point.Position.Z))
            .ToArray();

        if (inside.Length == 0)
        {
            // Пустая область — почти всегда промах мимо города. Молча сохранить
            // такую черту значило бы создать область без названия, которую потом
            // не найдёт ни один критерий.
            PostRejected("Внутри контура нет ни одного города. Обведите город целиком.");
            return;
        }

        if (inside.Length > 1)
        {
            // Название черты берётся из единственного города, поэтому два города
            // внутри дали бы неоднозначное имя. Автору предлагается сузить область.
            var names = string.Join(", ", inside.Select(point => point.Name));
            PostRejected("Внутри контура больше одного города (" + names +
                         "). Обведите ровно один город.");
            return;
        }

        var city = inside[0];
        var boundary = new CityBoundary(city.Id, city.Name, outline);

        // Черта того же города ЗАМЕНЯЕТСЯ, а не добавляется: автор может
        // обвести город заново, и два контура одного города оставили бы
        // перекрытие, разрешаемое порядком в файле — то есть случайностью.
        var kept = _boundaries.Boundaries
            .Where(existing => !existing.CityId.Equals(city.Id, StringComparison.OrdinalIgnoreCase) &&
                               !existing.CityName.Equals(city.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        kept.Add(boundary);
        _store.Save(kept);
        _boundaries = new CityBoundaryIndex(kept);

        AppLogger.Info("CityBoundaryForm: черта сохранена.",
            $"city={city.Name}; vertices={outline.Count}; path={_store.FilePath}");

        PostSnapshot("черта сохранена");
        PostJson(JsonSerializer.Serialize(new
        {
            type = "city_boundary_saved",
            cityId = city.Id,
            cityName = city.Name,
            title = boundary.Title,
            vertexCount = outline.Count,
            path = _store.FilePath
        }));
    }

    /// <summary>
    /// Удаляет черту города.
    ///
    /// Нужно потому, что автор перерисовывает черты по мере расширения карты, и
    /// черта, нарисованная вокруг устаревших данных, должна уметь исчезать, а не
    /// только заменяться: иначе её нельзя переделать «с нуля».
    /// </summary>
    private void DeleteBoundary(JsonElement root)
    {
        var city = root.TryGetProperty("city", out var cityNode) ? cityNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(city))
        {
            PostRejected("Не указан город для удаления черты.");
            return;
        }

        var kept = _boundaries.Boundaries
            .Where(existing => !existing.CityId.Equals(city, StringComparison.OrdinalIgnoreCase) &&
                               !existing.CityName.Equals(city, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (kept.Count == _boundaries.Boundaries.Count)
        {
            PostRejected("У города «" + city + "» нет нарисованной черты.");
            return;
        }

        _store.Save(kept);
        _boundaries = new CityBoundaryIndex(kept);

        AppLogger.Info("CityBoundaryForm: черта удалена.", $"city={city}; path={_store.FilePath}");

        PostSnapshot("черта удалена");
        PostJson(JsonSerializer.Serialize(new
        {
            type = "city_boundary_deleted",
            city,
            path = _store.FilePath
        }));
    }

    private void PostRejected(string message)
    {
        AppLogger.Warn("CityBoundaryForm: контур отклонён.", message);
        PostJson(JsonSerializer.Serialize(new
        {
            type = "city_boundary_rejected",
            message
        }));
    }

    private static List<CityBoundaryPoint> ReadOutline(JsonElement root)
    {
        var result = new List<CityBoundaryPoint>();
        if (!root.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in points.EnumerateArray())
        {
            var x = item.TryGetProperty("x", out var xNode) ? xNode.GetDouble() : double.NaN;
            var z = item.TryGetProperty("z", out var zNode) ? zNode.GetDouble() : double.NaN;

            if (double.IsFinite(x) && double.IsFinite(z))
                result.Add(new CityBoundaryPoint(x, z));
        }

        return result;
    }

    /// <summary>
    /// Отправляет окну состояние: дороги, города, все точки мира и нарисованные
    /// черты.
    ///
    /// Точки мира уходят ЦЕЛИКОМ (~5 200 штук) — окно их показывает, потому что
    /// черта нужна именно для отбора точек, и автору полезно видеть, что в неё
    /// попадает. Это допустимо здесь и недопустимо в снимках Симулятора: там
    /// снимок уходит на каждое событие, а это окно обновляется только по команде
    /// автора.
    /// </summary>
    private void PostSnapshot(string reason)
    {
        var cities = LoadCities();
        var missing = _boundaries.MissingCities(
            cities.Select(city => new CityReference(city.Id, city.Name)));

        if (missing.Count > 0)
        {
            // Уведомление о недостающих чертах — прямо в журнал: мир расширяется,
            // города добавляются, и узнать об этом надо не по пустому результату
            // поиска, а сразу при открытии окна.
            AppLogger.Warn("CityBoundaryForm: у части городов нет черты.",
                $"missing={missing.Count}; cities={string.Join(", ", missing.Select(city => city.Name))}");
        }

        PostJson(JsonSerializer.Serialize(new
        {
            type = "city_boundary_review",
            reason,
            roads = _roads.ToFlatArray(),
            cities = cities.Select(city => new { name = city.Name, x = city.X, z = city.Z }).ToArray(),
            worldPoints = _worldPoints
                .Where(point => double.IsFinite(point.Position.X) && double.IsFinite(point.Position.Z))
                .Select(point => new
                {
                    name = point.Name,
                    category = point.Category,
                    x = point.Position.X,
                    z = point.Position.Z,
                    isCity = point.IsCity
                })
                .ToArray(),
            boundaries = _boundaries.Boundaries
                .Where(boundary => boundary.IsValid)
                .Select(boundary => new
                {
                    cityId = boundary.CityId,
                    cityName = boundary.CityName,
                    title = boundary.Title,
                    points = Flatten(boundary.Outline)
                })
                .ToArray(),
            missingCount = missing.Count,
            missingCities = missing.Select(city => city.Name).ToArray(),
            boundaryPath = _store.FilePath
        }));
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

    /// <summary>
    /// Города мира: идентификатор, имя и координаты.
    ///
    /// Читаются прямо из файла данных, а не из точек мира: точкам мира имя города
    /// известно, а идентификатор черты — нет, и сопоставлять черту с точкой по
    /// имени значило бы зависеть от совпадения подписей.
    ///
    /// Координаты нужны окну для подписи на карте: подпись ставится у самого
    /// города, а не у точки из общего списка — на мелком масштабе точка в кадр не
    /// попадает, а название города нужно всегда, по нему определяется черта.
    /// </summary>
    private static IReadOnlyList<CityLabel> LoadCities()
    {
        try
        {
            var path = Path.Combine(AppPaths.ResourceRoot, "world", "cities.json");
            if (!File.Exists(path))
            {
                AppLogger.Warn("CityBoundaryForm: файл городов не найден.", path);
                return Array.Empty<CityLabel>();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("cities", out var cities) ||
                cities.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CityLabel>();
            }

            var result = new List<CityLabel>();
            foreach (var city in cities.EnumerateArray())
            {
                var id = city.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                var name = city.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                var x = city.TryGetProperty("x", out var xNode) ? xNode.GetDouble() : double.NaN;
                var z = city.TryGetProperty("z", out var zNode) ? zNode.GetDouble() : double.NaN;

                if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(x) || !double.IsFinite(z))
                    continue;

                result.Add(new CityLabel(id ?? string.Empty, name, x, z));
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CityBoundaryForm: ошибка чтения городов.", ex);
            return Array.Empty<CityLabel>();
        }
    }

    /// <summary>
    /// Город для окна: то же, что <see cref="CityReference"/>, плюс координаты.
    ///
    /// Отдельный тип, а не расширение CityReference: координаты нужны ТОЛЬКО
    /// окну, а проверка полноты черт работает по идентификатору и имени —
    /// таскать в домен геометрию подписи незачем.
    /// </summary>
    private readonly record struct CityLabel(string Id, string Name, double X, double Z);
}
