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
    public void DynamicTargetMustNotIncreaseDistanceToDestination()
    {
        var waypoints = Waypoints();
        var plan = Plan(waypoints);

        // Игрок находится на 250 м. Ближайшая фиксированная точка впереди —
        // №3 на 300 м. Точка №2 уже позади и не может стать динамической целью.
        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(0, 0, 250));

        Assert.Equal(2, nextPoint);

        var target = plan.Points[nextPoint].Position;
        var playerDistance = Math.Abs(target.Z - 250);
        var destinationDistance =
            Math.Abs(waypoints[^1].Position.Z - target.Z);

        Assert.True(
            destinationDistance < Math.Abs(waypoints[^1].Position.Z - 250));
        Assert.True(playerDistance >= 0);
    }

    [Fact]
    public void DynamicSegmentCannotTurnAwayFromDestinationNearTarget()
    {
        var waypoints = new[]
        {
            new RouteWaypoint(
                "bend",
                new WorldCoordinate(15, 0, 5),
                60),
            new RouteWaypoint(
                "destination",
                new WorldCoordinate(10, 0, 0),
                60)
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
                        waypoints[1].Position
                    },
                    Math.Sqrt(50) + Math.Sqrt(50))
            },
            Array.Empty<string>(),
            waypoints);

        var nextPoint = RouteMovementEngine.NextRoutePointForPlayer(
            plan,
            new WorldCoordinate(0, 0, 0));

        // Прямая к промежуточной точке сначала приближает игрока к
        // destination, но перед самой точкой начинает удалять от него.
        // Поэтому динамический сегмент выбирает сам пункт назначения.
        Assert.Equal(1, nextPoint);
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
