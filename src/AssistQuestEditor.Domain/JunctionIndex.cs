namespace AssistQuestEditor.Domain;

/// <summary>
/// Точка перекрёстка дорожной сети.
///
/// Специально не переиспользует <see cref="WorldCoordinate"/>: перекрёсток — не
/// точка мира и не позиция игрока, у него нет ни идентификатора, ни категории.
/// Отдельный тип не даёт случайно передать перекрёсток туда, где ждут WorldPoint.
/// </summary>
public readonly record struct JunctionPoint(double X, double Z);

/// <summary>
/// Насколько перекрёсток «настоящий»: сколько дорожных веток из него выходит.
///
/// Значение приходит из предпосчитанных данных, где оно получено по направлениям
/// примыкающих дуг: 3 — Т-образное примыкание, 4 — крест, 5 и больше — развязка.
/// Хранится, чтобы автор мог отличить простой перекрёсток от сложного узла.
/// </summary>
public enum JunctionKind
{
    /// <summary>Три ветки: дорога примыкает к сквозной.</summary>
    TShape = 3,

    /// <summary>Четыре ветки: классический крест.</summary>
    Cross = 4,

    /// <summary>Пять и более веток: сложный узел или развязка.</summary>
    Complex = 5
}

/// <summary>
/// Индекс перекрёстков для пространственных критериев.
///
/// Перекрёстки приходят ГОТОВЫМ списком из <c>data/world/junctions.json</c>, а не
/// считаются здесь: их поиск требует нодировки всей дорожной сети (рассечение
/// 98 341 отрезка во всех пересечениях), это сотни миллисекунд и десятки
/// мегабайт промежуточных структур. Готовый список весит ~60 КБ.
///
/// Устроен как <see cref="RoadIndex"/> и по той же причине: критерий проверяется
/// для КАЖДОГО кандидата, а перекрёстков тысячи. Без сетки поиск ближайшего был бы
/// полным перебором.
/// </summary>
public sealed class JunctionIndex
{
    /// <summary>
    /// Размер ячейки сетки в метрах.
    ///
    /// Узлы «точечные» (в отличие от дорог), но в центре крупного города их до 38
    /// на клетку 2x2 км, поэтому ячейка мельче дорожной: 500 м дала бы в городе
    /// сотни узлов на ячейку и вернула почти полный перебор.
    /// </summary>
    private const double CellSize = 250d;

    private readonly Dictionary<(int CellX, int CellZ), List<JunctionPoint>> _cells = new();
    private readonly int _junctionCount;
    private readonly IReadOnlyList<JunctionPoint> _junctions;

    public JunctionIndex(IReadOnlyList<JunctionPoint> junctions)
    {
        _junctionCount = junctions.Count;
        _junctions = junctions;

        foreach (var junction in junctions)
        {
            if (!double.IsFinite(junction.X) || !double.IsFinite(junction.Z))
                continue;

            var cell = (CellOf(junction.X), CellOf(junction.Z));
            if (!_cells.TryGetValue(cell, out var bucket))
            {
                bucket = new List<JunctionPoint>();
                _cells[cell] = bucket;
            }

            bucket.Add(junction);
        }
    }

    public bool IsEmpty => _junctionCount == 0;

    public int JunctionCount => _junctionCount;

    /// <summary>
    /// Список перекрёстков в порядке загрузки.
    ///
    /// Порядок важен для панели ручной проверки: кнопки «следующий/предыдущий»
    /// обязаны идти в том порядке, в котором узлы нашлись на самом деле, а не в
    /// порядке, который случайно получился бы при обходе сетки.
    ///
    /// Возвращается та же ссылка: список неизменяем и копировать тысячи элементов
    /// на каждый запрос панели незачем.
    /// </summary>
    public IReadOnlyList<JunctionPoint> ToPoints() => _junctions;

    /// <summary>
    /// Расстояние от точки до ближайшего перекрёстка.
    ///
    /// <paramref name="searchRadius"/> ограничивает перебор окрестными ячейками.
    /// Если в окрестности ничего нет, возвращается
    /// <see cref="double.PositiveInfinity"/>: вызывающий код трактует это как
    /// «далеко» и отбраковывает кандидата. Для критерия с МАКСИМУМОМ это верно, но
    /// для диапазона с минимумом («не ближе N м») бесконечность тоже проходит
    /// проверку как «достаточно далеко» — и это правильное поведение.
    ///
    /// Радиус расширяется на размер ячейки: перекрёсток может лежать в соседней
    /// ячейке и всё равно быть ближе радиуса.
    /// </summary>
    public double DistanceToNearest(double x, double z, double searchRadius)
    {
        if (_junctionCount == 0)
            return double.PositiveInfinity;

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

            foreach (var junction in bucket)
            {
                var dx = junction.X - x;
                var dz = junction.Z - z;
                var distance = dx * dx + dz * dz;
                if (distance < best)
                    best = distance;
            }
        }

        return double.IsPositiveInfinity(best)
            ? double.PositiveInfinity
            : Math.Sqrt(best);
    }

    /// <summary>
    /// Классификация узла по числу веток из предпосчитанных данных.
    ///
    /// Ветки едут отдельным массивом: они нужны панели проверки (ручная валидация),
    /// но самому критерию безразличны, поэтому в <see cref="JunctionPoint"/> их нет.
    /// </summary>
    public static JunctionKind KindOf(int branches) => branches switch
    {
        <= 3 => JunctionKind.TShape,
        4 => JunctionKind.Cross,
        _ => JunctionKind.Complex
    };

    private static int CellOf(double value) => (int)Math.Floor(value / CellSize);
}
