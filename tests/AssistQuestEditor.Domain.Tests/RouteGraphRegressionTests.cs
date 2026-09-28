using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class RouteGraphRegressionTests
{
    [Fact]
    public void ShortRoadFragmentsAreNotCollapsedIntoArtificialJunctions()
    {
        var planner = new RoadRoutePlanner(
        [
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(6, 0, 6, 100)
        ]);

        var route = new RouteState(
            60,
            [
                new RouteWaypoint("a", new WorldCoordinate(20, 0, 0), 60),
                new RouteWaypoint("b", new WorldCoordinate(6, 0, 80), 60)
            ]);

        var plan = planner.Build(route);

        // До фикса 8-метровая дедупликация склеивала (0,0) и (6,0),
        // превращая две разные дороги в искусственный перекрёсток.
        Assert.False(plan.IsUsable);
    }

    [Fact]
    public void RecoveryBridgeDoesNotReplaceARealLongerRoad()
    {
        var planner = new RoadRoutePlanner(
        [
            new RoadSegment(0, 0, 50, 0),
            new RoadSegment(50, 0, 50, 100),
            new RoadSegment(50, 100, 250, 100),
            new RoadSegment(250, 100, 250, 0),
            new RoadSegment(250, 0, 300, 0)
        ]);

        var route = new RouteState(
            60,
            [
                new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), 60),
                new RouteWaypoint("b", new WorldCoordinate(300, 0, 0), 60)
            ]);

        var plan = planner.Build(route);

        Assert.True(plan.IsUsable);
        Assert.True(
            plan.Legs[0].LengthMeters >= 499d,
            "A* должен предпочесть реальный дорожный путь, а не 300-метровый recovery-мост");
        Assert.Contains(
            plan.Legs[0].Polyline,
            point => Math.Abs(point.Z - 100d) < 0.01d);
    }

    [Fact]
    public void NormalRoadWaypointsNeverAddAnOffRoadStartConnector()
    {
        var planner = new RoadRoutePlanner(
        [
            new RoadSegment(0, 0, 200, 0)
        ]);

        var route = new RouteState(
            60,
            [
                new RouteWaypoint("a", new WorldCoordinate(20, 0, 7), 60),
                new RouteWaypoint("b", new WorldCoordinate(180, 0, 7), 60)
            ]);

        var plan = planner.Build(route);

        Assert.True(plan.IsUsable);
        Assert.All(
            plan.Legs[0].Polyline,
            point => Assert.InRange(point.Z, -0.001d, 0.001d));
    }

    /// <summary>
    /// Реальный дефект из журнала: изолированный район (X 143..168 км,
    /// Z -74..-89 км) не соединялся с остальной сетью, потому что «ближайшая
    /// точка впереди» почти всегда лежит в СВОЕЙ компоненте.
    ///
    /// Остров: A (0,0)–(100,0), петля B (0,0)–(0,-80)–(40,-80)–(120,-30)–(40,0).
    /// Единственный тупик — (100,0). Кандидатов у него два:
    ///   • D (120,-30)–(40,0) — 36 м, СВОЯ компонента, направление 0.53;
    ///   • материк (150,-400)–(150,400) — 50 м, ЧУЖАЯ, направление 1.0.
    /// Штатный проход выбирает ближайший (свой) и тратит единственный мост
    /// впустую. Сшивка компонент доводит дело до 50-метрового стыка.
    /// </summary>
    [Fact]
    public void NearestRecoveryTargetInOwnIslandDoesNotBlockStitchingToMainland()
    {
        var planner = new RoadRoutePlanner(
        [
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(0, 0, 0, -80),
            new RoadSegment(0, -80, 40, -80),
            new RoadSegment(40, -80, 120, -30),
            new RoadSegment(120, -30, 40, 0),
            new RoadSegment(150, -400, 150, 400)
        ]);

        var route = new RouteState(
            60,
            [
                new RouteWaypoint("a", new WorldCoordinate(80, 0, 0), 60),
                new RouteWaypoint("b", new WorldCoordinate(150, 0, 0), 60)
            ]);

        var plan = planner.Build(route);

        Assert.True(
            plan.IsUsable,
            "остров обязан сшиться с материком, а не остаться изолированным");
        Assert.Contains(
            plan.Legs[0].Polyline,
            point => Math.Abs(point.X - 150d) < 0.001d);
    }

    /// <summary>
    /// Сшивка компонент не должна превращаться в «дорогу»: дальний стык
    /// остаётся невосстановимым, и обход через него не появляется.
    /// </summary>
    [Fact]
    public void StitchingDoesNotInventLongDetours()
    {
        var planner = new RoadRoutePlanner(
        [
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(0, 0, 0, -80),
            new RoadSegment(0, -80, 40, -80),
            new RoadSegment(40, -80, 120, -30),
            new RoadSegment(120, -30, 40, 0),
            // Материк отнесён на 140 м — за предел сшивки (60 м).
            new RoadSegment(240, -400, 240, 400)
        ]);

        var route = new RouteState(
            60,
            [
                new RouteWaypoint("a", new WorldCoordinate(80, 0, 0), 60),
                new RouteWaypoint("b", new WorldCoordinate(240, 0, 0), 60)
            ]);

        Assert.False(planner.Build(route).IsUsable);
    }
}
