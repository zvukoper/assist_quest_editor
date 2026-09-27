using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Выбор цели маршрута. Заказ: «алгоритм движения по маршруту всегда должен
/// выбирать цель в пользу следования по маршруту к точке со старшим номером».
///
/// Регрессии, которые эти тесты стерегут:
///   • после загрузки маршрута из файла целью становилась точка №1, даже когда
///     игрок стоял между точками 6 и 7 — он ехал назад через весь маршрут;
///   • после перемещения игрока на уже пройденную точку он «перескакивал» назад
///     и продолжал путь от неё.
/// Обе — это выбор МЕНЬШЕГО номера при неоднозначности.
/// </summary>
public sealed class RoutePreferredTargetTests
{
    private static RouteWaypoint Waypoint(string id, double z, double speed = 60) =>
        new(id, new WorldCoordinate(0, 0, z), speed);

    /// <summary>Семь точек подряд через 100 м: №1..№7.</summary>
    private static RouteWaypoint[] SevenWaypoints() =>
        Enumerable.Range(0, 7)
            .Select(index => Waypoint((index + 1).ToString(), index * 100d))
            .ToArray();

    /// <summary>
    /// Игрок стоит МЕЖДУ точками 6 и 7 (z ≈ 550). Цель — №7: продолжение пути
    /// вперёд. Раньше здесь выбиралась точка №1, потому что курсор был
    /// неинициализирован и цель бралась «по индексу 0».
    /// </summary>
    [Fact]
    public void BetweenWaypointsChoosesNextWaypointAhead()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, 550));

        Assert.Equal(6, target);
    }

    /// <summary>
    /// Игрок стоит РОВНО на точке №6 (z = 500). «Ближайшая» — она сама, но она уже
    /// достигнута, поэтому цель — №7. Именно этот случай давал «цель — точка №1»:
    /// игрок начинал путь с точки №1, оказывался на ней, и ближайшей оставалась она.
    /// </summary>
    [Fact]
    public void ExactlyOnWaypointContinuesToNextWaypoint()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, 500));

        Assert.Equal(6, target);
    }

    /// <summary>
    /// Точка на месте, но пройдено несколько: игрок стоит на №5, цель — №6, а не
    /// оставленная ранее №7 и уж точно не №1.
    /// </summary>
    [Fact]
    public void StandingOnEarlierWaypointGoesToItsNeighbour()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, 400));

        Assert.Equal(5, target);
    }

    /// <summary>
    /// Игрок стоит между точками, ближайшая из которых — СТАРШАЯ. Это и есть
    /// требование «в пользу точки со старшим номером»: при равных расстояниях
    /// (ровно посередине) берётся точка впереди.
    /// </summary>
    [Theory]
    [InlineData(550, 6)]  // ровно между №6 и №7 → вперёд, к №7
    [InlineData(250, 3)]  // ровно между №3 и №4 → вперёд, к №4
    [InlineData(450, 5)]  // ровно между №5 и №6 → вперёд, к №6
    [InlineData(150, 2)]  // ровно между №2 и №3 → вперёд, к №3
    public void TiesAreResolvedInFavourOfHigherIndex(double z, int expected)
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, z));

        Assert.Equal(expected, target);
    }

    /// <summary>
    /// Последняя точка: вперёд идти некуда, цель остаётся последней — маршрут на
    /// неё и завершится (или остановится при нулевой скорости).
    /// </summary>
    [Fact]
    public void StandingOnLastWaypointKeepsLastWaypoint()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, 600));

        Assert.Equal(6, target);
    }

    /// <summary>
    /// Прежняя цель ограничивает поиск сверху: перемещение «вперёд» не меняет
    /// намерение игрока. Игрок у точки №2, прежняя цель — №2, значит цель №2.
    /// </summary>
    [Fact]
    public void PreviousTargetCapsTheChoice()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, 100),
            maxIndex: 1);

        Assert.Equal(1, target);
    }

    /// <summary>
    /// Подсказка <c>nearestIndex</c> участвует в сравнении: если вызывающий уже
    /// знает ближайшую точку, выбирается старшая из неё и кандидата вперёд.
    /// </summary>
    [Fact]
    public void NearestIndexHintIsComparedWithCandidate()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            SevenWaypoints(),
            new WorldCoordinate(0, 0, 380),
            maxIndex: null,
            nearestIndex: 3);

        Assert.Equal(4, target);
    }

    /// <summary>Пустой маршрут — цели нет, вызывающий сохраняет прежнее поведение.</summary>
    [Fact]
    public void EmptyRouteHasNoTarget()
    {
        Assert.Null(RouteMovementEngine.PreferredForwardWaypoint(
            Array.Empty<RouteWaypoint>(),
            new WorldCoordinate(0, 0, 0)));

        Assert.Null(RouteMovementEngine.PreferredForwardWaypoint(
            null,
            new WorldCoordinate(0, 0, 0)));
    }

    /// <summary>
    /// Маршрут из одной точки: вперёд идти некуда, цель — она сама.
    /// </summary>
    [Fact]
    public void SingleWaypointRouteTargetsThatWaypoint()
    {
        var target = RouteMovementEngine.PreferredForwardWaypoint(
            new[] { Waypoint("1", 0) },
            new WorldCoordinate(0, 0, 0));

        Assert.Equal(0, target);
    }
}
