namespace AssistQuestEditor.Domain;

public sealed record RouteWaypoint(
    string Id,
    WorldCoordinate Position,
    double SpeedKmh,
    bool IsOffRoad = false);

public sealed record RouteState(
    double DefaultSpeedKmh,
    IReadOnlyList<RouteWaypoint> Waypoints)
{
    public const double MinSpeedKmh = 0d;
    public const double MaxSpeedKmh = 150d;
    public const double DefaultSpeedKmhValue = 60d;

    public static RouteState Empty =>
        new(DefaultSpeedKmhValue, Array.Empty<RouteWaypoint>());

    public RouteState Normalize()
    {
        var speed = Math.Clamp(
            double.IsFinite(DefaultSpeedKmh) ? DefaultSpeedKmh : DefaultSpeedKmhValue,
            MinSpeedKmh,
            MaxSpeedKmh);

        var points = (Waypoints ?? Array.Empty<RouteWaypoint>())
            .Where(item => item is not null &&
                           !string.IsNullOrWhiteSpace(item.Id) &&
                           double.IsFinite(item.Position.X) &&
                           double.IsFinite(item.Position.Y) &&
                           double.IsFinite(item.Position.Z))
            .Select(item => item with
            {
                SpeedKmh = Math.Clamp(
                    double.IsFinite(item.SpeedKmh) ? item.SpeedKmh : DefaultSpeedKmhValue,
                    MinSpeedKmh,
                    MaxSpeedKmh)
            })
            .ToArray();

        return new RouteState(speed, points);
    }

    public RouteState WithDefaultSpeed(double speed) =>
        this with
        {
            DefaultSpeedKmh = Math.Clamp(
                double.IsFinite(speed) ? speed : DefaultSpeedKmhValue,
                MinSpeedKmh,
                MaxSpeedKmh)
        };
}

public readonly record struct RoadProjection(
    WorldCoordinate Position,
    int NodeA,
    int NodeB,
    double DistanceMeters);

public sealed record RouteLeg(
    int StartWaypointIndex,
    int EndWaypointIndex,
    IReadOnlyList<WorldCoordinate> Polyline,
    double LengthMeters);

public sealed record RoutePlan(
    IReadOnlyList<RouteLeg> Legs,
    IReadOnlyList<string> Errors)
{
    public static RoutePlan Empty { get; } =
        new(Array.Empty<RouteLeg>(), Array.Empty<string>());

    public bool IsUsable =>
        Legs.Count > 0 &&
        Errors.Count == 0 &&
        Legs.All(item => item.Polyline.Count >= 2);
}

public readonly record struct RouteCursor(
    bool Initialized,
    int LegIndex,
    int SegmentIndex,
    double SegmentProgressMeters,
    bool ResumeAfterStop,
    int? StoppedAtWaypointIndex,
    double LastHeadingDegrees)
{
    public static RouteCursor Initial =>
        new(false, 0, 0, 0, false, null, 0);
}

public sealed record RouteMovementResult(
    WorldCoordinate Position,
    double SpeedKmh,
    double HeadingDegrees,
    RouteCursor Cursor,
    bool Enabled,
    bool Completed,
    bool StoppedAtWaypoint);

public static class RouteMovementEngine
{
    public const double FovAngleDegrees = 80d;
    public const double FovMinLengthMeters = 30d;
    public const double FovMaxLengthMeters = 100d;
    public const double FovSpeedThresholdKmh = 80d;

    public static double FovLengthForSpeed(double speedKmh)
    {
        var speed = Math.Clamp(
            double.IsFinite(speedKmh) ? speedKmh : 0d,
            0d,
            FovSpeedThresholdKmh);

        return FovMinLengthMeters +
               (1d - speed / FovSpeedThresholdKmh) *
               (FovMaxLengthMeters - FovMinLengthMeters);
    }

    public static RouteMovementResult Advance(
        RouteState route,
        RoutePlan plan,
        RouteCursor cursor,
        WorldCoordinate currentPosition,
        double elapsedSeconds)
    {
        route = (route ?? RouteState.Empty).Normalize();

        var safeElapsed = Math.Max(
            0d,
            double.IsFinite(elapsedSeconds) ? elapsedSeconds : 0d);

        if (route.Waypoints.Count < 1 ||
            plan.Legs.Count == 0 ||
            plan.Errors.Count > 0)
        {
            return new RouteMovementResult(
                currentPosition,
                0d,
                cursor.LastHeadingDegrees,
                cursor,
                true,
                false,
                false);
        }

        if (!cursor.Initialized)
            cursor = ProjectCursor(plan, currentPosition, cursor);

        if (cursor.LegIndex < 0 || cursor.LegIndex >= plan.Legs.Count)
        {
            return new RouteMovementResult(
                currentPosition,
                0d,
                cursor.LastHeadingDegrees,
                cursor with { Initialized = false },
                false,
                true,
                false);
        }

        var position = currentPosition;
        var heading = cursor.LastHeadingDegrees;
        var resumeAfterStop = cursor.ResumeAfterStop;
        var legIndex = cursor.LegIndex;
        var segmentIndex = cursor.SegmentIndex;
        var progress = Math.Max(0d, cursor.SegmentProgressMeters);

        var speedLeg = plan.Legs[legIndex];
        var speed = resumeAfterStop ||
                    speedLeg.StartWaypointIndex < 0
            ? route.Waypoints[speedLeg.EndWaypointIndex].SpeedKmh
            : route.Waypoints[speedLeg.StartWaypointIndex].SpeedKmh;

        if (speed <= 0d && !resumeAfterStop)
        {
            var stoppedWaypointIndex = speedLeg.StartWaypointIndex < 0
                ? speedLeg.EndWaypointIndex
                : speedLeg.StartWaypointIndex;

            return new RouteMovementResult(
                position,
                0d,
                heading,
                cursor with
                {
                    Initialized = true,
                    StoppedAtWaypointIndex = stoppedWaypointIndex,
                    ResumeAfterStop = false
                },
                false,
                false,
                true);
        }

        var remainingSeconds = safeElapsed;

        while (remainingSeconds > 0.000001d && legIndex < plan.Legs.Count)
        {
            var leg = plan.Legs[legIndex];
            var points = leg.Polyline;

            if (points.Count < 2)
            {
                legIndex++;
                segmentIndex = 0;
                progress = 0d;
                resumeAfterStop = false;
                continue;
            }

            segmentIndex = Math.Clamp(segmentIndex, 0, points.Count - 2);

            var a = points[segmentIndex];
            var b = points[segmentIndex + 1];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var segmentLength = Math.Sqrt(dx * dx + dz * dz);

            if (segmentLength <= 0.000001d)
            {
                segmentIndex++;
                progress = 0d;
                continue;
            }

            progress = Math.Clamp(progress, 0d, segmentLength);
            var available = Math.Max(0d, segmentLength - progress);
            var directionX = dx / segmentLength;
            var directionZ = dz / segmentLength;
            heading = HeadingDegrees(directionX, directionZ);

            var distanceForThisSpeed = speed / 3.6d * remainingSeconds;
            if (distanceForThisSpeed < available - 0.000001d)
            {
                progress += distanceForThisSpeed;
                remainingSeconds = 0d;
                var t = progress / segmentLength;

                position = new WorldCoordinate(
                    a.X + dx * t,
                    currentPosition.Y,
                    a.Z + dz * t);

                return new RouteMovementResult(
                    position,
                    speed,
                    heading,
                    new RouteCursor(
                        true,
                        legIndex,
                        segmentIndex,
                        progress,
                        resumeAfterStop,
                        resumeAfterStop ? null : cursor.StoppedAtWaypointIndex,
                        heading),
                    true,
                    false,
                    false);
            }

            position = new WorldCoordinate(b.X, currentPosition.Y, b.Z);
            remainingSeconds -= available / Math.Max(speed, 0.000001d) * 3.6d;
            segmentIndex++;
            progress = 0d;

            if (segmentIndex < points.Count - 1)
                continue;

            var destinationIndex = leg.EndWaypointIndex;
            legIndex++;

            if (destinationIndex >= route.Waypoints.Count - 1)
            {
                return new RouteMovementResult(
                    position,
                    0d,
                    heading,
                    new RouteCursor(
                        true,
                        Math.Max(0, plan.Legs.Count - 1),
                        Math.Max(0, points.Count - 2),
                        0d,
                        false,
                        destinationIndex,
                        heading),
                    false,
                    true,
                    false);
            }

            var destinationSpeed = route.Waypoints[destinationIndex].SpeedKmh;
            if (destinationSpeed <= 0d)
            {
                return new RouteMovementResult(
                    position,
                    0d,
                    heading,
                    new RouteCursor(
                        true,
                        Math.Min(legIndex, Math.Max(0, plan.Legs.Count - 1)),
                        0,
                        0d,
                        false,
                        destinationIndex,
                        heading),
                    false,
                    false,
                    true);
            }

            speed = destinationSpeed;
            resumeAfterStop = false;
            segmentIndex = 0;
            progress = 0d;
        }

        return new RouteMovementResult(
            position,
            speed,
            heading,
            new RouteCursor(
                true,
                Math.Min(legIndex, Math.Max(0, plan.Legs.Count - 1)),
                segmentIndex,
                progress,
                false,
                resumeAfterStop ? null : cursor.StoppedAtWaypointIndex,
                heading),
            true,
            false,
            false);
    }

    public static RouteCursor CreateResumeCursor(
        RoutePlan plan,
        int stoppedWaypointIndex,
        double lastHeadingDegrees)
    {
        var nextLegIndex = stoppedWaypointIndex;

        // Если план начинается с виртуального leg «игрок → точка 1», то
        // следующий leg после ЛЮБОЙ достигнутой точки имеет индекс
        // waypointIndex + 1. Раньше это было исправлено только для точки 1,
        // поэтому после остановки на точке 2 Resume запускал снова leg 2
        // (точка 1 → точка 2).
        if (plan.Legs.Count > 0 &&
            plan.Legs[0].StartWaypointIndex < 0)
        {
            nextLegIndex = stoppedWaypointIndex + 1;
        }

        if (stoppedWaypointIndex < 0 ||
            nextLegIndex < 0 ||
            nextLegIndex >= plan.Legs.Count)
        {
            return RouteCursor.Initial with
            {
                ResumeAfterStop = true,
                StoppedAtWaypointIndex = stoppedWaypointIndex,
                LastHeadingDegrees = lastHeadingDegrees
            };
        }

        return new RouteCursor(
            true,
            nextLegIndex,
            0,
            0d,
            true,
            stoppedWaypointIndex,
            lastHeadingDegrees);
    }

    private static RouteCursor ProjectCursor(
        RoutePlan plan,
        WorldCoordinate position,
        RouteCursor previous)
    {
        // Первый leg специально начинается из ТОЙ САМОЙ позиции игрока, которую
        // использовал Planner. Не нужно искать «ближайший» поздний участок: при
        // петлях маршрута он может оказаться физически ближе и курсор перепрыгнет
        // через первые точки.
        if (plan.Legs.Count > 0 &&
            plan.Legs[0].StartWaypointIndex < 0)
        {
            return previous with
            {
                Initialized = true,
                LegIndex = 0,
                SegmentIndex = 0,
                SegmentProgressMeters = 0d
            };
        }

        var bestDistance = double.PositiveInfinity;
        var bestLeg = 0;
        var bestSegment = 0;
        var bestProgress = 0d;

        for (var legIndex = 0; legIndex < plan.Legs.Count; legIndex++)
        {
            var points = plan.Legs[legIndex].Polyline;
            for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex++)
            {
                var a = points[segmentIndex];
                var b = points[segmentIndex + 1];
                var dx = b.X - a.X;
                var dz = b.Z - a.Z;
                var lengthSquared = dx * dx + dz * dz;
                var t = lengthSquared <= 0.000001d
                    ? 0d
                    : ((position.X - a.X) * dx + (position.Z - a.Z) * dz) / lengthSquared;

                t = Math.Clamp(t, 0d, 1d);

                var px = a.X + dx * t;
                var pz = a.Z + dz * t;
                var distance = Math.Sqrt(
                    (position.X - px) * (position.X - px) +
                    (position.Z - pz) * (position.Z - pz));

                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestLeg = legIndex;
                bestSegment = segmentIndex;
                bestProgress = Math.Sqrt(lengthSquared) * t;
            }
        }

        return previous with
        {
            Initialized = true,
            LegIndex = bestLeg,
            SegmentIndex = bestSegment,
            SegmentProgressMeters = bestProgress
        };
    }

    private static double HeadingDegrees(double x, double z)
    {
        var heading = Math.Atan2(z, x) * 180d / Math.PI;
        return heading < 0d ? heading + 360d : heading;
    }
}
