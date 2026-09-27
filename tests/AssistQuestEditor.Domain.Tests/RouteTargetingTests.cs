using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Выбор цели маршрута при РУЧНОМ перемещении игрока и подсчёт пройденной
/// дистанции. Оба правила жили в окне Симулятора и оба были неверны: цель
/// оставалась прежней (игрок «ехал» к далёкой точке, игнорируя промежуточную
/// остановку), а одометр всегда показывал ноль, пока игрок не доедет до первой
/// путевой точки.
/// </summary>
public sealed class RouteTargetingTests
{
    private static RouteWaypoint Waypoint(string id, double z, double speed = 60) =>
        new(id, new WorldCoordinate(0, 0, z), speed);

    /// <summary>
    /// Игрок перемещён между точками 1 и 2, а целью осталась точка 9. Новой
    /// целью обязана стать БЛИЖАЙШАЯ к игроку точка — №2, а не прежняя №9.
    /// </summary>
    [Fact]
    public void NearestForwardWaypointPicksClosestWaypointBeforePreviousTarget()
    {
        var waypoints = new[]
        {
            Waypoint("1", 0),
            Waypoint("2", 100),
            Waypoint("3", 1000),
            Waypoint("9", 2000)
        };

        var target = RouteMovementEngine.NearestForwardWaypoint(
            waypoints,
            new WorldCoordinate(0, 0, 60),
            maxIndex: 3);

        Assert.Equal(1, target);
    }

    /// <summary>
    /// Прежняя цель ограничивает поиск: дальше неё цель не назначается, иначе
    /// ручное перемещение «вперёд» незаметно меняло бы намерение игрока.
    /// </summary>
    [Fact]
    public void NearestForwardWaypointNeverGoesPastPreviousTarget()
    {
        var waypoints = new[]
        {
            Waypoint("1", 0),
            Waypoint("2", 100),
            Waypoint("3", 1000)
        };

        // Прежняя цель — точка №3 (индекс 2), но игрок стоит у точки №2.
        var target = RouteMovementEngine.NearestForwardWaypoint(
            waypoints,
            new WorldCoordinate(0, 0, 90),
            maxIndex: 2);

        Assert.Equal(1, target);

        // Ограничение сверху: при цели №2 поиск идёт только до неё.
        var limited = RouteMovementEngine.NearestForwardWaypoint(
            waypoints,
            new WorldCoordinate(0, 0, 1010),
            maxIndex: 1);

        Assert.Equal(1, limited);
    }

    /// <summary>Без цели (maxIndex = null) поиск идёт по всему маршруту.</summary>
    [Fact]
    public void NearestForwardWaypointWithoutLimitSearchesWholeRoute()
    {
        var waypoints = new[]
        {
            Waypoint("1", 0),
            Waypoint("2", 100),
            Waypoint("3", 1000)
        };

        var target = RouteMovementEngine.NearestForwardWaypoint(
            waypoints,
            new WorldCoordinate(0, 0, 980),
            maxIndex: null);

        Assert.Equal(2, target);
    }

    /// <summary>Пустой маршрут — цели нет, вызывающий обязан сохранить прежнее поведение.</summary>
    [Fact]
    public void NearestForwardWaypointReturnsNullForEmptyRoute()
    {
        Assert.Null(RouteMovementEngine.NearestForwardWaypoint(
            Array.Empty<RouteWaypoint>(),
            new WorldCoordinate(0, 0, 0),
            maxIndex: null));

        Assert.Null(RouteMovementEngine.NearestForwardWaypoint(
            null,
            new WorldCoordinate(0, 0, 0),
            maxIndex: null));
    }

    /// <summary>
    /// Дистанция считается и ВНУТРИ виртуального первого leg («игрок → точка 1»).
    ///
    /// Регрессия: расчёт требовал StartWaypointIndex &gt;= 0, а у виртуального leg
    /// он равен −1, поэтому подпись под маркером показывала «0.0 км» на всём
    /// первом участке пути — со стороны это выглядело как неработающий одометр.
    /// </summary>
    [Fact]
    public void RouteDistanceCountsVirtualFirstLeg()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(-1, 0,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(0, 0, 10) },
                    10),
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(0, 0, 10), new WorldCoordinate(0, 0, 100) },
                    90)
            },
            Array.Empty<string>());

        var cumulative = new[] { 0d, 90d };

        var insideVirtualLeg = RouteMovementEngine.RouteDistanceMeters(
            plan,
            new RouteCursor(true, 0, 0, 4, false, null, 0),
            stoppedWaypointIndex: null,
            cumulative);

        Assert.Equal(4, insideVirtualLeg, 6);

        var insideSecondLeg = RouteMovementEngine.RouteDistanceMeters(
            plan,
            new RouteCursor(true, 1, 0, 30, false, null, 0),
            stoppedWaypointIndex: null,
            cumulative);

        Assert.Equal(30, insideSecondLeg, 6);
    }

    /// <summary>
    /// Остановка на точке: игрок стоит на самой точке, значит пройдено ровно до
    /// неё — даже когда курсор уже не инициализирован (маршрут выключен).
    /// </summary>
    [Fact]
    public void RouteDistanceOnStoppedWaypointEqualsCumulativeLength()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(-1, 0,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(0, 0, 10) },
                    10),
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(0, 0, 10), new WorldCoordinate(0, 0, 100) },
                    90)
            },
            Array.Empty<string>());

        var distance = RouteMovementEngine.RouteDistanceMeters(
            plan,
            RouteCursor.Initial,
            stoppedWaypointIndex: 1,
            new[] { 0d, 90d });

        Assert.Equal(90, distance, 6);
    }

    /// <summary>Без маршрута и накопленных длин дистанция равна нулю, а не падает.</summary>
    [Fact]
    public void RouteDistanceIsZeroWithoutPlan()
    {
        Assert.Equal(0d, RouteMovementEngine.RouteDistanceMeters(
            RoutePlan.Empty,
            RouteCursor.Initial,
            stoppedWaypointIndex: null,
            Array.Empty<double>()), 6);
    }
}
