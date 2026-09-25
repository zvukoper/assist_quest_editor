using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class RouteTests
{
    [Fact]
    public void RouteStateClampsSpeedRange()
    {
        var route = new RouteState(999, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), -5),
            new RouteWaypoint("b", new WorldCoordinate(1, 0, 0), 999)
        }).Normalize();

        Assert.Equal(150, route.DefaultSpeedKmh);
        Assert.Equal(0, route.Waypoints[0].SpeedKmh);
        Assert.Equal(150, route.Waypoints[1].SpeedKmh);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(40, 65)]
    [InlineData(80, 30)]
    [InlineData(150, 30)]
    public void FovLengthUsesConfiguredSpeedCurve(double speed, double expected)
    {
        Assert.Equal(expected, RouteMovementEngine.FovLengthForSpeed(speed), 6);
    }

    [Fact]
    public void ExactRoadConnectionBuildsRoute()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(100, 0, 100, 100)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 90), 60)
        });

        var plan = planner.Build(route);

        Assert.True(plan.IsUsable);
        Assert.Single(plan.Legs);
        Assert.Contains(plan.Legs[0].Polyline, point => Math.Abs(point.X - 100) < .001 && Math.Abs(point.Z) < .001);
    }

    [Fact]
    public void SmallRoadGapIsRecovered()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 50, 0),
            new RoadSegment(54, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(90, 0, 0), 60)
        });

        Assert.True(planner.Build(route).IsUsable);
    }

    [Fact]
    public void NearbyParallelRoadsAreNotMergedByDirectionRecovery()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(0, 20, 100, 20)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(90, 0, 20), 60)
        });

        Assert.False(planner.Build(route).IsUsable);
    }

    [Fact]
    public void JunctionSplitsCrossingRoads()
    {
        var planner = new RoadRoutePlanner(
            new[]
            {
                new RoadSegment(0, 0, 100, 100),
                new RoadSegment(0, 100, 100, 0)
            },
            new[]
            {
                new JunctionPoint(50, 50)
            });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 10, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(90, 0, 10), 60)
        });

        var plan = planner.Build(route);

        Assert.True(plan.IsUsable);
        Assert.Contains(
            plan.Legs[0].Polyline,
            point => Math.Abs(point.X - 50) < .01 && Math.Abs(point.Z - 50) < .01);
    }

    [Fact]
    public void MovementUsesMetersPerSecond()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(
                    0,
                    1,
                    new[]
                    {
                        new WorldCoordinate(0, 0, 0),
                        new WorldCoordinate(100, 0, 0)
                    },
                    100)
            },
            Array.Empty<string>());

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 60)
        });

        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            1);

        Assert.Equal(60, result.SpeedKmh);
        Assert.Equal(50d / 3d, result.Position.X, 6);
        Assert.Equal(0, result.HeadingDegrees, 6);
        Assert.True(result.Enabled);
    }

    [Fact]
    public void ZeroSpeedWaypointStopsAndDisablesRoute()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(100, 0, 0) },
                    100),
                new RouteLeg(1, 2,
                    new[] { new WorldCoordinate(100, 0, 0), new WorldCoordinate(200, 0, 0) },
                    100)
            },
            Array.Empty<string>());

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 0),
            new RouteWaypoint("c", new WorldCoordinate(200, 0, 0), 90)
        });

        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            7);

        Assert.False(result.Enabled);
        Assert.True(result.StoppedAtWaypoint);
        Assert.Equal(100, result.Position.X, 6);
        Assert.Equal(0, result.SpeedKmh, 6);
    }

    [Fact]
    public void ResumeAfterZeroSpeedUsesNextWaypointSpeed()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(100, 0, 0) },
                    100),
                new RouteLeg(1, 2,
                    new[] { new WorldCoordinate(100, 0, 0), new WorldCoordinate(200, 0, 0) },
                    100)
            },
            Array.Empty<string>());

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 0),
            new RouteWaypoint("c", new WorldCoordinate(200, 0, 0), 90)
        });

        var cursor = RouteMovementEngine.CreateResumeCursor(plan, 1, 0);

        var result = RouteMovementEngine.Advance(
            route,
            plan,
            cursor,
            new WorldCoordinate(100, 0, 0),
            1);

        Assert.True(result.Enabled);
        Assert.Equal(90, result.SpeedKmh, 6);
        Assert.Equal(125d, result.Position.X, 6);
    }

    [Fact]
    public void FinalWaypointStopsRouteWithoutStoppingSimulation()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(50, 0, 0) },
                    50)
            },
            Array.Empty<string>());

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(50, 0, 0), 60)
        });

        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            10);

        Assert.False(result.Enabled);
        Assert.True(result.Completed);
        Assert.Equal(50, result.Position.X, 6);
        Assert.Equal(0, result.SpeedKmh, 6);
    }
}
