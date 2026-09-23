namespace AssistQuestEditor.Domain;

/// <summary>
/// Один дорожный отрезок в мировых координатах.
///
/// Хранится парой точек, а не ломаной: дорожная геометрия ETS2 приходит именно
/// отрезками, и расстояние до отрезка считается напрямую. Склейка в полилинии
/// дала бы экономию памяти, но усложнила бы поиск без выигрыша по точности.
/// </summary>
public readonly record struct RoadSegment(
    double X1,
    double Z1,
    double X2,
    double Z2)
{
    /// <summary>
    /// Квадрат расстояния от точки до отрезка.
    ///
    /// Квадрат, а не расстояние: сравнение идёт по минимуму, а корень нужен один
    /// раз в самом конце. На 98 000 отрезков это заметно.
    /// </summary>
    public double DistanceSquaredTo(double x, double z)
    {
        var dx = X2 - X1;
        var dz = Z2 - Z1;
        var lengthSquared = dx * dx + dz * dz;

        // Вырожденный отрезок (обе точки совпали) — расстояние до точки.
        if (lengthSquared <= double.Epsilon)
        {
            var px = x - X1;
            var pz = z - Z1;
            return px * px + pz * pz;
        }

        // Проекция точки на прямую, ограниченная отрезком.
        var t = ((x - X1) * dx + (z - Z1) * dz) / lengthSquared;
        t = Math.Clamp(t, 0d, 1d);

        var cx = x - (X1 + t * dx);
        var cz = z - (Z1 + t * dz);
        return cx * cx + cz * cz;
    }
}

/// <summary>
/// Индекс дорожной геометрии для пространственных критериев.
///
/// Зачем индекс, а не простой перебор: «рядом с дорогой» проверяется для КАЖДОГО
/// кандидата, а дорог ~98 000. Полный перебор дал бы почти миллиард вычислений
/// расстояния на один прогон. Здесь точки раскладываются по сетке, и для
/// кандидата проверяются только отрезки из близких ячеек.
///
/// Сетка, а не категория (как в <c>WorldPointIndex</c>): дороги одной категории не
/// имеют, зато имеют протяжённость, поэтому ключ — пространственная ячейка.
/// </summary>
public sealed class RoadIndex
{
    /// <summary>
    /// Размер ячейки сетки в метрах.
    ///
    /// Выбран из типичных радиусов критерия (десятки-сотни метров): при 500 м
    /// каждый отрезок попадает в 1-2 ячейки, а окрестность кандидата — в горстку
    /// ячеек. Слишком мелкая сетка раздула бы словарь, слишком крупная вернула бы
    /// полный перебор.
    /// </summary>
    private const double CellSize = 500d;

    private readonly Dictionary<(int CellX, int CellZ), List<RoadSegment>> _cells = new();
    private readonly int _segmentCount;
    private readonly IReadOnlyList<RoadSegment> _segments;

    public RoadIndex(IReadOnlyList<RoadSegment> segments)
    {
        _segmentCount = segments.Count;
        _segments = segments;

        foreach (var segment in segments)
        {
            // Отрезок регистрируется во ВСЕХ ячейках, которые пересекает его
            // описывающий прямоугольник. Иначе длинная дорога (километры) не
            // нашлась бы из ячейки на своём конце.
            foreach (var cell in CellsOf(segment))
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    bucket = new List<RoadSegment>();
                    _cells[cell] = bucket;
                }

                bucket.Add(segment);
            }
        }
    }

    public bool IsEmpty => _segmentCount == 0;

    public int SegmentCount => _segmentCount;

    /// <summary>
    /// Расстояние от точки до ближайшей дороги.
    ///
    /// <paramref name="searchRadius"/> ограничивает перебор окрестными ячейками.
    /// Это важно для критерия «рядом с дорогой»: если в окрестности ничего нет,
    /// вызывающий код всё равно отбракует точку. Возвращаемое значение в таком
    /// случае — <see cref="double.PositiveInfinity"/>, и трактовать его как
    /// «бесконечно далеко» безопасно.
    ///
    /// Полный перебор оставлен как защита: если в окрестных ячейках пусто, но
    /// дороги в мире есть, значит ячейка кандидата шире радиуса поиска — и
    /// честнее посчитать точно, чем вернуть бесконечность и молча отбраковать.
    /// </summary>
    public double DistanceToNearest(double x, double z, double searchRadius)
    {
        if (_segmentCount == 0)
            return double.PositiveInfinity;

        // Радиус поиска расширяется на размер ячейки: отрезок может лежать в
        // соседней ячейке и всё равно быть ближе радиуса.
        var reach = Math.Max(0d, searchRadius) + CellSize;
        var minCellX = CellOf(x - reach);
        var maxCellX = CellOf(x + reach);
        var minCellZ = CellOf(z - reach);
        var maxCellZ = CellOf(z + reach);

        var best = double.PositiveInfinity;

        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!_cells.TryGetValue((cellX, cellZ), out var bucket))
                continue;

            foreach (var segment in bucket)
            {
                var distance = segment.DistanceSquaredTo(x, z);
                if (distance < best)
                    best = distance;
            }
        }

        return double.IsPositiveInfinity(best)
            ? double.PositiveInfinity
            : Math.Sqrt(best);
    }

    private static int CellOf(double value) => (int)Math.Floor(value / CellSize);

    /// <summary>
    /// Ячейки, которые пересекает описывающий прямоугольник отрезка.
    ///
    /// Прямоугольник, а не сама линия: так отрезок гарантированно находится из
    /// любой ячейки, к которой примыкает, и проверка не может «потерять» дорогу
    /// из-за диагонального прохода через угол.
    /// </summary>
    private static IEnumerable<(int CellX, int CellZ)> CellsOf(RoadSegment segment)
    {
        if (!double.IsFinite(segment.X1) || !double.IsFinite(segment.Z1) ||
            !double.IsFinite(segment.X2) || !double.IsFinite(segment.Z2))
        {
            yield break;
        }

        var minX = CellOf(Math.Min(segment.X1, segment.X2));
        var maxX = CellOf(Math.Max(segment.X1, segment.X2));
        var minZ = CellOf(Math.Min(segment.Z1, segment.Z2));
        var maxZ = CellOf(Math.Max(segment.Z1, segment.Z2));

        for (var cellX = minX; cellX <= maxX; cellX++)
        for (var cellZ = minZ; cellZ <= maxZ; cellZ++)
            yield return (cellX, cellZ);
    }
}
