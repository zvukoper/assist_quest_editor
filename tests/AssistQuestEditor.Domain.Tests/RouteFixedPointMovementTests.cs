using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class RouteFixedPointMovementTests
{
    private static RouteWaypoint[] Waypoints(
        double firstSpeed = 60d,
        int firstNumber = 10,
        int secondNumber = 20,
        int thirdNumber = 30) =>
    [
        new RouteWaypoint(
            "wp-1",
            new WorldCoordinate(0, 0, 100),
            firstSpeed,
            Number: firstNumber),
        new RouteWaypoint(
            "wp-2",
            new WorldCoordinate(0, 0, 200),
            60,
            Number: secondNumber),
        new RouteWaypoint(
            "wp-3",
            new WorldCoordinate(0, 0, 300),
            60,
            Number: thirdNumber)
    ];

    private static RoutePlan Plan(RouteWaypoint[] waypoints) =>
        new(
            new[]
            {
                new RouteLeg(
                    0,
                    1,
                    new[] { waypoints[0].Position, waypoints[1].Position },
                    100),
                new RouteLeg(
                    1,
                    2,
                    new[] { waypoints[1].Position, waypoints[2].Position },
                    100)
            },
            Array.Empty<string>(),
            waypoints);

    [Fact]
    public void PlanContainsOnlyFixedRoutePointsAndSegments()
    {
        var waypoints = Waypoints();
        var plan = Plan(waypoints);

        Assert.Equal(3, plan.Points.Count);
        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(100, plan.Segments[0].LengthMeters);
        Assert.Equal(100, plan.Segments[1].LengthMeters);
        Assert.All(plan.Points, point =>
            Assert.Equal(
                new[] { 100d, 200d, 300d }[point.Id],
                point.Position.Z));
    }

    [Fact]
    public void PlayerRoadAnchorIsTheDynamicTargetBeforeFirstWaypoint()
    {
        var waypoints = Waypoints();

        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(
                    -1,
                    0,
                    new[]
                    {
                        new WorldCoordinate(0, 0, 0),
                        new WorldCoordinate(0, 0, 50),
                        waypoints[0].Position
                    },
                    0),
                new RouteLeg(
                    0,
                    1,
                    new[] { waypoints[0].Position, waypoints[1].Position },
                    100),
                new RouteLeg(
                    1,
                    2,
                    new[] { waypoints[1].Position, waypoints[2].Position },
                    100)
            },
            Array.Empty<string>(),
            waypoints);

        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(0, 0, -20));

        Assert.Equal(0, nextPoint);
        Assert.Null(plan.Points[nextPoint].WaypointIndex);
        Assert.Equal(0, plan.Points[nextPoint].DestinationWaypointIndex);
    }

    [Fact]
    public void PlayerAfterWaypointSelectsTheNextForwardWaypoint()
    {
        var waypoints = Waypoints();
        var plan = Plan(waypoints);

        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(0, 0, 210));

        var preferredWaypoint = RouteMovementEngine.PreferredWaypointForPlayer(
            plan,
            new WorldCoordinate(0, 0, 210));

        Assert.Equal(2, nextPoint);
        Assert.Equal(2, preferredWaypoint);
    }

    [Fact]
    public void PlayerExactlyOnWaypointMovesToTheNextRoutePoint()
    {
        var waypoints = Waypoints();
        var plan = Plan(waypoints);

        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(0, 0, 200));

        Assert.Equal(2, nextPoint);
    }

    [Fact]
    public void PlayerMovesToNextFixedPointByRouteOrderEvenWhenDetourGetsFartherFromDestination()
    {
        var waypoints = new[]
        {
            new RouteWaypoint("start", new WorldCoordinate(0, 0, 0), 60),
            new RouteWaypoint("destination", new WorldCoordinate(100, 0, 0), 60)
        };

        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(
                    0,
                    1,
                    new[]
                    {
                        waypoints[0].Position,
                        new WorldCoordinate(-80, 0, 0),
                        new WorldCoordinate(-80, 0, 80),
                        waypoints[1].Position
                    },
                    0)
            },
            Array.Empty<string>(),
            waypoints);

        // Игрок находится ближе к destination по прямой, но fixed-маршрут сначала
        // обязан пройти через дорожную точку (-80, 0). Евклидова дальность до
        // destination здесь не имеет права выкинуть этот участок маршрута.
        //
        // Игрок проецируется внутрь участка start(-0) → bend(-80), поэтому
        // следующей целью становится КОНЕЦ этого участка — bend с индексом 1.
        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(-40, 0, 20));

        Assert.Equal(1, nextPoint);
        Assert.Equal(new WorldCoordinate(-80, 0, 0), plan.Points[nextPoint].Position);
    }

    [Fact]
    public void DynamicTargetFollowsRoadAnchorNotWaypoint()
    {
        var waypoints = Waypoints();

        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(
                    -1,
                    0,
                    new[]
                    {
                        new WorldCoordinate(0, 0, 20),
                        new WorldCoordinate(0, 0, 50),
                        waypoints[0].Position
                    },
                    80),
                new RouteLeg(
                    0,
                    1,
                    new[] { waypoints[0].Position, waypoints[1].Position },
                    100),
                new RouteLeg(
                    1,
                    2,
                    new[] { waypoints[1].Position, waypoints[2].Position },
                    100)
            },
            Array.Empty<string>(),
            waypoints);

        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(0, 0, 0));

        // Dynamic segment должен заканчиваться на ближайшей fixed road point,
        // а не сразу на путевой точке №1.
        Assert.Equal(0, nextPoint);
        Assert.Null(plan.Points[nextPoint].WaypointIndex);
    }

    [Fact]
    public void DynamicSegmentMovesStraightToCurrentRoutePoint()
    {
        var waypoints = Waypoints();
        var plan = Plan(waypoints);
        var cursor = RouteCursor.Initial.ForPoint(0);

        var first = RouteMovementEngine.Advance(
            new RouteState(60, waypoints),
            plan,
            cursor,
            new WorldCoordinate(0, 0, 0),
            1d);

        Assert.Equal(100d / 6d, first.Position.Z, 5);
        Assert.True(first.Enabled);
        Assert.False(first.Completed);
        Assert.Equal(0, first.Cursor.NextRoutePointIndex);

        var second = RouteMovementEngine.Advance(
            new RouteState(60, waypoints),
            plan,
            first.Cursor,
            first.Position,
            1d);

        Assert.Equal(100d / 3d, second.Position.Z, 5);
        Assert.Equal(0, second.Cursor.NextRoutePointIndex);
    }

    [Fact]
    public void ReachingFixedWaypointSwitchesDynamicSegmentForward()
    {
        var waypoints = Waypoints();
        var plan = Plan(waypoints);
        var cursor = RouteCursor.Initial.ForPoint(0);

        var result = RouteMovementEngine.Advance(
            new RouteState(60, waypoints),
            plan,
            cursor,
            new WorldCoordinate(0, 0, 90),
            0.7d);

        Assert.Contains("wp-1", result.PassedWaypointIds);
        Assert.Equal(1, result.Cursor.NextRoutePointIndex);
        Assert.Equal(200d, plan.Points[result.Cursor.NextRoutePointIndex].Position.Z, 6);
        Assert.True(result.Enabled);
        Assert.False(result.StoppedAtWaypoint);
    }

    [Fact]
    public void ZeroSpeedWaypointStopsAndPreservesStableIdentity()
    {
        var waypoints = Waypoints(firstSpeed: 0);
        var plan = Plan(waypoints);
        var cursor = RouteCursor.Initial.ForPoint(0);

        var result = RouteMovementEngine.Advance(
            new RouteState(60, waypoints),
            plan,
            cursor,
            new WorldCoordinate(0, 0, 0),
            10d);

        Assert.Equal(100, result.Position.Z, 6);
        Assert.False(result.Enabled);
        Assert.True(result.StoppedAtWaypoint);
        Assert.Equal("wp-1", result.StoppedWaypointId);
        Assert.Empty(result.PassedWaypointIds);
        Assert.Equal(0, result.Cursor.NextRoutePointIndex);
    }

    [Fact]
    public void AutomaticRemovalKeepsWaypointNumbersUntilExplicitRenumber()
    {
        var waypoints = Waypoints();
        var state = new RouteState(60, waypoints);

        var remaining = state.RemoveLeadingWaypoints(2);

        var last = Assert.Single(remaining.Waypoints);
        Assert.Equal(30, last.Number);

        var renumbered = remaining.RenumberWaypoints();

        Assert.Equal(1, renumbered.Waypoints[0].Number);
    }
}
