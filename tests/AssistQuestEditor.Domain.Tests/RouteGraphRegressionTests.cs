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
}
