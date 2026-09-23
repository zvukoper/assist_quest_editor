namespace AssistQuestEditor.Domain;

/// <summary>
/// Вершина черты города в мировых координатах (метры).
///
/// Отдельный тип, а не <see cref="WorldCoordinate"/>: у вершины многоугольника нет
/// высоты, и передать её туда, где ждут точку мира, было бы ошибкой.
/// </summary>
public readonly record struct CityBoundaryPoint(double X, double Z);

/// <summary>
/// Город, для которого ищется черта: идентификатор и человекочитаемое имя.
///
/// Имя нужно для сообщений («у города Казань нет черты»), идентификатор — для
/// сопоставления. Хранятся вместе, потому что в отчёте о недостающих чертах
/// требуются оба, а брать их из разных источников значило бы рассинхронизировать.
/// </summary>
public readonly record struct CityReference(string Id, string Name);

/// <summary>
/// Геометрия многоугольника: проверка «точка внутри».
///
/// Вынесена отдельно, потому что применяется в ДВУХ разных направлениях:
///  - при поиске точек мира — «лежит ли точка внутри уже сохранённой черты»;
///  - при сохранении черты — «какой город попал внутрь нарисованного контура».
/// Второе — тот же самый вопрос, только аргументы другие, и дублировать алгоритм
/// значило бы получить две расходящиеся реализации.
/// </summary>
public static class CityBoundaryGeometry
{
    /// <summary>
    /// Точка внутри многоугольника (алгоритм трассировки луча).
    ///
    /// Условие <c>(zi &gt; z) != (zj &gt; z)</c> отбирает рёбра, которые пересекает
    /// горизонтальный луч, идущий вправо от точки; каждое пересечение меняет
    /// ответ на противоположный. Чётность пересечений и есть ответ: для замкнутого
    /// контура нечётное число пересечений означает «внутри».
    ///
    /// Вершина на самом луче попадает ровно в одно из двух рёбер (из-за строгого
    /// сравнения одна из пар даёт ложь), поэтому двойного счёта не происходит —
    /// это и есть причина, по которой сравнение строгое, а не <c>&gt;=</c>.
    /// </summary>
    public static bool ContainsPoint(IReadOnlyList<CityBoundaryPoint> outline, double x, double z)
    {
        if (outline is null || outline.Count < 3)
            return false;

        var inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            var xi = outline[i].X;
            var zi = outline[i].Z;
            var xj = outline[j].X;
            var zj = outline[j].Z;

            if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi)
                inside = !inside;
        }

        return inside;
    }
}

/// <summary>
/// Черта города: замкнутая область, нарисованная автором вручную.
///
/// Почему это отдельный слой, а не поле точки мира: одна и та же точка мира не
/// принадлежит городу — городу принадлежит МЕСТО. Точка у магазина на окраине
/// может оказаться и внутри черты, и снаружи, в зависимости от того, как автор
/// провёл границу, а границу он проводит по своему замыслу (где заканчивается
/// город ради квеста), а не по формальному признаку в данных.
///
/// Класс, а не запись: у области есть производное состояние (границы
/// прямоугольника), которое обязано считаться ОДИН раз при создании, иначе
/// проверка «внутри ли точка» для каждой из тысяч точек мира перебирала бы все
/// рёбра каждого многоугольника.
/// </summary>
public sealed class CityBoundary
{
    public CityBoundary(string cityId, string cityName, IReadOnlyList<CityBoundaryPoint>? outline)
    {
        CityId = cityId ?? string.Empty;
        CityName = cityName ?? string.Empty;
        Outline = outline ?? Array.Empty<CityBoundaryPoint>();

        // Прямоугольник вокруг контура: дешёвая отсечка до трассировки луча.
        // Точка, не попавшая в него, заведомо снаружи — а точек мира тысячи.
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minZ = double.PositiveInfinity;
        var maxZ = double.NegativeInfinity;

        foreach (var vertex in Outline)
        {
            if (!double.IsFinite(vertex.X) || !double.IsFinite(vertex.Z))
            {
                // Одна нечисловая вершина делает область непредсказуемой: контур
                // перестаёт быть замкнутым в том смысле, в каком его понимает
                // трассировка луча. Область помечается негодной целиком, а не
                // «чинится» выбрасыванием вершины: чинить чужой контур молча —
                // значит менять то, что автор нарисовал.
                IsValid = false;
                MinX = MinZ = MaxX = MaxZ = 0;
                return;
            }

            if (vertex.X < minX) minX = vertex.X;
            if (vertex.X > maxX) maxX = vertex.X;
            if (vertex.Z < minZ) minZ = vertex.Z;
            if (vertex.Z > maxZ) maxZ = vertex.Z;
        }

        MinX = minX; MaxX = maxX; MinZ = minZ; MaxZ = maxZ;
        IsValid = Outline.Count >= 3;
    }

    public string CityId { get; }

    public string CityName { get; }

    public IReadOnlyList<CityBoundaryPoint> Outline { get; }

    /// <summary>
    /// Годна ли область: не меньше трёх вершин и все координаты числовые.
    ///
    /// Нужна, чтобы отличить «автор нарисовал точку и точку» от настоящей черты:
    /// у вырожденной области нет внутренности, и она молча не находила бы ничего.
    /// </summary>
    public bool IsValid { get; }

    public double MinX { get; }
    public double MaxX { get; }
    public double MinZ { get; }
    public double MaxZ { get; }

    /// <summary>
    /// Название черты ровно так, как его просил автор: «Черта города Казань».
    ///
    /// Собирается здесь, а не в интерфейсе: название видят и окно рисования, и
    /// панель критериев, и журнал, и расходиться они не должны.
    /// </summary>
    public string Title => string.IsNullOrWhiteSpace(CityName)
        ? "Черта города (без названия)"
        : "Черта города " + CityName;

    /// <summary>Точка внутри области. Негодная область не содержит ничего.</summary>
    public bool Contains(double x, double z)
    {
        if (!IsValid)
            return false;

        // Отсечка по прямоугольнику: до трассировки луча доходят только точки,
        // которые в него попали.
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ)
            return false;

        return CityBoundaryGeometry.ContainsPoint(Outline, x, z);
    }
}

/// <summary>
/// Индекс черт городов для пространственных критериев.
///
/// Устроен проще, чем <see cref="RoadIndex"/> и <see cref="JunctionIndex"/>: черт
/// не тысячи, а десятки (по числу городов мира), поэтому сетка по клеткам была бы
/// лишней. Перебор идёт по списку областей, но у каждой есть прямоугольник, и
/// точка проверяется трассировкой луча только у тех областей, чей прямоугольник
/// её накрывает.
/// </summary>
public sealed class CityBoundaryIndex
{
    private readonly IReadOnlyList<CityBoundary> _boundaries;

    public CityBoundaryIndex(IReadOnlyList<CityBoundary>? boundaries = null)
    {
        _boundaries = boundaries ?? Array.Empty<CityBoundary>();
    }

    /// <summary>Пустой индекс: черт ещё нет. Не null — вызывающему коду проще.</summary>
    public static CityBoundaryIndex Empty { get; } = new();

    public bool IsEmpty => _boundaries.Count == 0;

    /// <summary>Сколько черт задано (не городов: у города ровно одна черта).</summary>
    public int Count => _boundaries.Count;

    public IReadOnlyList<CityBoundary> Boundaries => _boundaries;

    /// <summary>
    /// Черта, внутри которой лежит точка, или <c>null</c>.
    ///
    /// Возвращается сама область, а не «да/нет»: панели нужно имя города, и
    /// вывести его из признака нельзя. Если автор нарисовал перекрывающиеся
    /// области, побеждает первая в файле — окно рисования такие случаи не
    /// пропускает (внутри контура обязан быть ровно один город), но файл можно
    /// править и руками.
    /// </summary>
    public CityBoundary? FindCity(double x, double z)
    {
        foreach (var boundary in _boundaries)
        {
            if (boundary.Contains(x, z))
                return boundary;
        }

        return null;
    }

    /// <summary>Лежит ли точка внутри ЛЮБОЙ черты. Критерий «В любом городе».</summary>
    public bool Contains(double x, double z) => FindCity(x, z) is not null;

    /// <summary>
    /// Лежит ли точка внутри черты КОНКРЕТНОГО города.
    ///
    /// Сравнение идёт и по идентификатору, и по имени без учёта регистра: автор
    /// рисует контур и видит в списке имя, а в файле черта хранится с
    /// идентификатором вида <c>city:kazan</c>, и заставлять его помнить
    /// идентификаторы незачем.
    /// </summary>
    public bool ContainsCity(string city, double x, double z)
    {
        if (string.IsNullOrWhiteSpace(city))
            return false;

        foreach (var boundary in _boundaries)
        {
            if (Matches(boundary, city) && boundary.Contains(x, z))
                return true;
        }

        return false;
    }

    /// <summary>Есть ли черта у города. Нужно проверке полноты черт.</summary>
    public bool HasBoundaryFor(string city) =>
        !string.IsNullOrWhiteSpace(city) && _boundaries.Any(boundary => Matches(boundary, city));

    /// <summary>
    /// Города из переданного списка, у которых черты нет.
    ///
    /// Это и есть «проверка, которая уведомляет»: мир расширяется, города
    /// добавляются, а черты рисуются вручную — значит после обновления данных
    /// часть городов остаётся без границы, и узнать об этом надо не по пустому
    /// результату поиска точек, а явным списком.
    ///
    /// Порядок сохраняется таким, каким пришёл список городов: он задан данными
    /// мира, и переставлять его значило бы менять порядок ручной работы автора.
    /// </summary>
    public IReadOnlyList<CityReference> MissingCities(IEnumerable<CityReference> cities)
    {
        var missing = new List<CityReference>();

        foreach (var city in cities)
        {
            if (string.IsNullOrWhiteSpace(city.Id) && string.IsNullOrWhiteSpace(city.Name))
                continue;

            if (!HasBoundaryFor(city.Id) && !HasBoundaryFor(city.Name))
                missing.Add(city);
        }

        return missing;
    }

    private static bool Matches(CityBoundary boundary, string city) =>
        boundary.CityId.Equals(city, StringComparison.OrdinalIgnoreCase) ||
        boundary.CityName.Equals(city, StringComparison.OrdinalIgnoreCase);
}
