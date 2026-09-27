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
            2d);

        Assert.Contains("wp-1", result.PassedWaypointIds);
        Assert.Equal(1, result.Cursor.NextRoutePointIndex);
        Assert.Equal(200d, result.Cursor.NextRoutePointIndex < plan.Points.Count
            ? plan.Points[result.Cursor.NextRoutePointIndex].Position.Z
            : -1d, 6);
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

        Assert.Equal(1, remaining.Waypoints.Count);
        Assert.Equal(30, remaining.Waypoints[0].Number);

        var renumbered = remaining.RenumberWaypoints();

        Assert.Equal(1, renumbered.Waypoints[0].Number);
    }
}
