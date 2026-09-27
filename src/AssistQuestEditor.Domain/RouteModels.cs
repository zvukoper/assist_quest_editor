namespace AssistQuestEditor.Domain;

public sealed record RouteWaypoint(
    string Id,
    WorldCoordinate Position,
    double SpeedKmh,
    bool IsOffRoad = false,
    int Number = 0)
{
    public int EffectiveNumber(int fallbackIndex) =>
        Number > 0 ? Number : fallbackIndex + 1;
}

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
            .Select((item, index) => item with
            {
                SpeedKmh = Math.Clamp(
                    double.IsFinite(item.SpeedKmh) ? item.SpeedKmh : DefaultSpeedKmhValue,
                    MinSpeedKmh,
                    MaxSpeedKmh),
                Number = item.Number > 0 ? item.Number : index + 1
            })
            .ToArray();

        return new RouteState(speed, points);
    }

    /// <summary>
    /// Нумерация изменяется только при ЯВНОМ редактировании маршрута
    /// пользователем. Автоматическое прохождение/отсечение точек этот метод
    /// не вызывает.
    /// </summary>
    public RouteState RenumberWaypoints() =>
        this with
        {
            Waypoints = Waypoints
                .Select((item, index) => item with { Number = index + 1 })
                .ToArray()
        };

    public RouteState RemoveLeadingWaypoints(int count)
    {
        if (count <= 0 || Waypoints.Count == 0)
            return this;

        if (count >= Waypoints.Count)
            return Empty with { DefaultSpeedKmh = DefaultSpeedKmh };

        return this with { Waypoints = Waypoints.Skip(count).ToArray() };
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

/// <summary>
/// Фиксированная точка маршрута. Внутренний Id пересоздаётся при каждом
/// перестроении и задаёт однозначный порядковый номер точки, по которой проходит
/// сохранённая геометрия маршрута.
/// </summary>
public sealed record RoutePoint(
    int Id,
    WorldCoordinate Position,
    int? WaypointIndex,
    int DestinationWaypointIndex,
    int CutWaypointCount,
    double TravelSpeedKmh);

/// <summary>Фиксированный сегмент между двумя точками маршрута.</summary>
public sealed record RouteSegment(
    int StartPointId,
    int EndPointId,
    double LengthMeters);

public sealed record RoutePlan(
    IReadOnlyList<RouteLeg> Legs,
    IReadOnlyList<string> Errors,
    IReadOnlyList<RouteWaypoint>? WaypointSource = null)
{
    public static RoutePlan Empty { get; } =
        new(Array.Empty<RouteLeg>(), Array.Empty<string>());

    public IReadOnlyList<RoutePoint> Points { get; } =
        BuildPoints(Legs, WaypointSource);

    public IReadOnlyList<RouteSegment> Segments { get; } =
        BuildSegments(Points);

    public double TotalDistanceMeters =>
        Segments.Sum(item => item.LengthMeters);

    public bool IsUsable =>
        Points.Count > 0 &&
        Errors.Count == 0;

    public RoutePlan ShiftWaypointIndices(int offset)
    {
        if (offset == 0 || Legs.Count == 0)
            return this;

        static int ShiftStart(int value, int delta) =>
            value < 0 ? value : value + delta;

        return new RoutePlan(
            Legs
                .Select(leg => leg with
                {
                    StartWaypointIndex = ShiftStart(leg.StartWaypointIndex, offset),
                    EndWaypointIndex = leg.EndWaypointIndex + offset
                })
                .ToArray(),
            Errors,
            WaypointSource);
    }

    private static IReadOnlyList<RoutePoint> BuildPoints(
        IReadOnlyList<RouteLeg> legs,
        IReadOnlyList<RouteWaypoint>? source)
    {
        if (legs is null || legs.Count == 0)
            return Array.Empty<RoutePoint>();

        var result = new List<RoutePoint>();
        var nextId = 0;

        for (var legIndex = 0; legIndex < legs.Count; legIndex++)
        {
            var leg = legs[legIndex];
            var polyline = leg.Polyline ?? Array.Empty<WorldCoordinate>();
            if (polyline.Count == 0)
                continue;

            var startWaypointIndex = leg.StartWaypointIndex;
            var endWaypointIndex = leg.EndWaypointIndex;

            var startSpeed = startWaypointIndex >= 0 &&
                             source is not null &&
                             startWaypointIndex < source.Count
                ? source[startWaypointIndex].SpeedKmh
                : RouteState.DefaultSpeedKmhValue;

            for (var pointIndex = 0; pointIndex < polyline.Count; pointIndex++)
            {
                var position = polyline[pointIndex];
                var isFirstLegPoint = pointIndex == 0;
                var isLastLegPoint = pointIndex == polyline.Count - 1;

                // Стыки двух legs используют одну физическую точку. Для второго
                // leg скорость уже должна быть скоростью предыдущей путевой точки,
                // поэтому дублированную стартовую точку не добавляем.
                if (result.Count > 0 && isFirstLegPoint &&
                    Distance2D(result[^1].Position, position) <= 0.01d)
                {
                    if (isLastLegPoint && endWaypointIndex >= 0)
                    {
                        result[^1] = result[^1] with
                        {
                            WaypointIndex = endWaypointIndex,
                            DestinationWaypointIndex = endWaypointIndex,
                            CutWaypointCount = endWaypointIndex
                        };
                    }

                    continue;
                }

                int? waypointIndex = null;
                if (legIndex == 0 && isFirstLegPoint && startWaypointIndex >= 0)
                    waypointIndex = startWaypointIndex;
                else if (isLastLegPoint && endWaypointIndex >= 0)
                    waypointIndex = endWaypointIndex;

                var destinationIndex =
                    endWaypointIndex >= 0 ? endWaypointIndex :
                    waypointIndex ?? Math.Max(0, startWaypointIndex);

                var cutCount = waypointIndex is int exactWaypoint
                    ? exactWaypoint
                    : Math.Max(0, startWaypointIndex + 1);

                var travelSpeed =
                    legIndex == 0 && isFirstLegPoint
                        ? (source?.Count > 0
                            ? source[0].SpeedKmh <= 0d
                                ? RouteState.DefaultSpeedKmhValue
                                : source[0].SpeedKmh
                            : RouteState.DefaultSpeedKmhValue)
                        : startSpeed;

                result.Add(new RoutePoint(
                    nextId++,
                    position,
                    waypointIndex,
                    destinationIndex,
                    cutCount,
                    travelSpeed));
            }
        }

        return result;
    }

    private static IReadOnlyList<RouteSegment> BuildSegments(
        IReadOnlyList<RoutePoint> points)
    {
        if (points is null || points.Count < 2)
            return Array.Empty<RouteSegment>();

        var result = new RouteSegment[points.Count - 1];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = new RouteSegment(
                points[index].Id,
                points[index + 1].Id,
                Distance2D(points[index].Position, points[index + 1].Position));
        }

        return result;
    }

    private static double Distance2D(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
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
    /// <summary>Следующая фиксированная точка маршрута, к которой привязан динамический сегмент.</summary>
    public int NextRoutePointIndex { get; init; } = -1;

    public static RouteCursor Initial =>
        new(false, 0, 0, 0, false, null, 0)
        {
            NextRoutePointIndex = -1
        };

    public RouteCursor ForPoint(int pointIndex, double heading = 0d) =>
        this with
        {
            Initialized = true,
            LegIndex = Math.Max(0, pointIndex),
            SegmentIndex = 0,
            SegmentProgressMeters = 0d,
            NextRoutePointIndex = pointIndex,
            LastHeadingDegrees = heading,
            StoppedAtWaypointIndex = null,
            ResumeAfterStop = false
        };
}

public sealed record RouteMovementResult(
    WorldCoordinate Position,
    double SpeedKmh,
    double HeadingDegrees,
    RouteCursor Cursor,
    bool Enabled,
    bool Completed,
    bool StoppedAtWaypoint)
{
    /// <summary>Путевые точки, физически достигнутые в этом тике.</summary>
    public IReadOnlyList<string> PassedWaypointIds { get; init; } =
        Array.Empty<string>();

    /// <summary>Идентификатор путевой точки, на которой произошла остановка.</summary>
    public string? StoppedWaypointId { get; init; }
}

public static class RouteMovementEngine
{
    public const double FovAngleDegrees = 80d;
    public const double FovMinLengthMeters = 30d;
    public const double FovMaxLengthMeters = 100d;
    public const double FovSpeedThresholdKmh = 80d;

    private const double PositionEpsilonMeters = 0.001d;

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

    /// <summary>
    /// Единственный двигатель движения. На каждом шаге движется ТОЛЬКО
    /// динамический сегмент «текущая позиция игрока → следующая фиксированная
    /// точка маршрута». Дорожная геометрия уже зафиксирована в RoutePlan.
    /// </summary>
    public static RouteMovementResult Advance(
        RouteState route,
        RoutePlan plan,
        RouteCursor cursor,
        WorldCoordinate currentPosition,
        double elapsedSeconds)
    {
        route = (route ?? RouteState.Empty).Normalize();

        if (route.Waypoints.Count == 0 ||
            plan is null ||
            plan.Errors.Count > 0 ||
            plan.Points.Count == 0)
        {
            return new RouteMovementResult(
                currentPosition,
                0d,
                cursor.LastHeadingDegrees,
                cursor,
                false,
                true,
                false);
        }

        var safeElapsed = Math.Max(
            0d,
            double.IsFinite(elapsedSeconds) ? elapsedSeconds : 0d);

        var nextPointIndex = cursor.NextRoutePointIndex;
        if (!cursor.Initialized ||
            nextPointIndex < 0 ||
            nextPointIndex >= plan.Points.Count)
        {
            nextPointIndex = 0;
            cursor = cursor.ForPoint(nextPointIndex, cursor.LastHeadingDegrees);
        }

        var position = currentPosition;
        var heading = cursor.LastHeadingDegrees;
        var remainingSeconds = safeElapsed;
        var passedIds = new List<string>();
        var resume = cursor.ResumeAfterStop;

        while (remainingSeconds > 0.000001d &&
               nextPointIndex >= 0 &&
               nextPointIndex < plan.Points.Count)
        {
            var target = plan.Points[nextPointIndex];
            var dx = target.Position.X - position.X;
            var dz = target.Position.Z - position.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);

            if (distance <= PositionEpsilonMeters)
            {
                position = new WorldCoordinate(
                    target.Position.X,
                    position.Y,
                    target.Position.Z);

                if (target.WaypointIndex is int waypointIndex &&
                    waypointIndex >= 0 &&
                    waypointIndex < route.Waypoints.Count)
                {
                    var waypoint = route.Waypoints[waypointIndex];

                    if (waypoint.SpeedKmh <= 0d && !resume)
                    {
                        return new RouteMovementResult(
                            position,
                            0d,
                            heading,
                            cursor.ForPoint(nextPointIndex, heading) with
                            {
                                StoppedAtWaypointIndex = waypointIndex,
                                ResumeAfterStop = false
                            },
                            false,
                            false,
                            true)
                        {
                            StoppedWaypointId = waypoint.Id
                        };
                    }

                    passedIds.Add(waypoint.Id);

                    if (waypointIndex >= route.Waypoints.Count - 1)
                    {
                        return new RouteMovementResult(
                            position,
                            0d,
                            heading,
                            cursor.ForPoint(nextPointIndex, heading),
                            false,
                            true,
                            false)
                        {
                            PassedWaypointIds = passedIds
                        };
                    }

                    nextPointIndex++;
                    resume = false;
                    continue;
                }

                nextPointIndex++;
                resume = false;
                continue;
            }

            var speed = Math.Max(0d, target.TravelSpeedKmh);
            if (speed <= 0d)
            {
                // Нулевая скорость имеет смысл только у самой путевой точки.
                // Для повреждённой/старой геометрии безопаснее не двигать игрока.
                return new RouteMovementResult(
                    position,
                    0d,
                    heading,
                    cursor.ForPoint(nextPointIndex, heading),
                    false,
                    false,
                    false);
            }

            var directionX = dx / distance;
            var directionZ = dz / distance;
            heading = HeadingDegrees(directionX, directionZ);

            var travelDistance = speed / 3.6d * remainingSeconds;
            if (travelDistance < distance)
            {
                var t = travelDistance / distance;
                position = new WorldCoordinate(
                    position.X + dx * t,
                    currentPosition.Y,
                    position.Z + dz * t);

                return new RouteMovementResult(
                    position,
                    speed,
                    heading,
                    cursor.ForPoint(nextPointIndex, heading),
                    true,
                    false,
                    false)
                {
                    PassedWaypointIds = passedIds
                };
            }

            var timeToTarget = distance / (speed / 3.6d);
            remainingSeconds -= timeToTarget;
            position = new WorldCoordinate(
                target.Position.X,
                currentPosition.Y,
                target.Position.Z);

            if (target.WaypointIndex is int reachedIndex &&
                reachedIndex >= 0 &&
                reachedIndex < route.Waypoints.Count)
            {
                var waypoint = route.Waypoints[reachedIndex];

                if (waypoint.SpeedKmh <= 0d && !resume)
                {
                    return new RouteMovementResult(
                        position,
                        0d,
                        heading,
                        cursor.ForPoint(nextPointIndex, heading) with
                        {
                            StoppedAtWaypointIndex = reachedIndex,
                            ResumeAfterStop = false
                        },
                        false,
                        false,
                        true)
                    {
                        PassedWaypointIds = passedIds,
                        StoppedWaypointId = waypoint.Id
                    };
                }

                passedIds.Add(waypoint.Id);

                if (reachedIndex >= route.Waypoints.Count - 1)
                {
                    return new RouteMovementResult(
                        position,
                        0d,
                        heading,
                        cursor.ForPoint(nextPointIndex, heading),
                        false,
                        true,
                        false)
                    {
                        PassedWaypointIds = passedIds
                    };
                }
            }

            nextPointIndex++;
            resume = false;
            cursor = cursor.ForPoint(nextPointIndex, heading);
        }

        var currentSpeed =
            nextPointIndex >= 0 && nextPointIndex < plan.Points.Count
                ? Math.Max(0d, plan.Points[nextPointIndex].TravelSpeedKmh)
                : 0d;

        return new RouteMovementResult(
            position,
            currentSpeed,
            heading,
            cursor.ForPoint(
                Math.Clamp(
                    nextPointIndex,
                    0,
                    Math.Max(0, plan.Points.Count - 1)),
                heading),
            true,
            false,
            false)
        {
            PassedWaypointIds = passedIds
        };
    }

    /// <summary>
    /// Следующая фиксированная точка маршрута ПО НАПРАВЛЕНИЮ К ПУНКТУ НАЗНАЧЕНИЯ.
    ///
    /// Игрок проецируется на все фиксированные сегменты маршрута. Выбирается
    /// сегмент, к которому он ближе всего, а затем берётся точка ПОСЛЕ проекции.
    /// При попадании ровно в стык сегментов предпочтение получает сегмент дальше
    /// по маршруту. Поэтому игрок никогда не начинает движение обратно к уже
    /// пройденной точке только потому, что она геометрически чуть ближе.
    /// </summary>
    public static int NextRoutePointForPlayer(
        RoutePlan plan,
        WorldCoordinate position)
    {
        if (plan is null || plan.Points.Count == 0)
            return -1;

        if (plan.Points.Count == 1)
            return 0;

        var bestSegment = 0;
        var bestT = 0d;
        var bestDistance = double.PositiveInfinity;

        for (var index = 0; index < plan.Points.Count - 1; index++)
        {
            var a = plan.Points[index].Position;
            var b = plan.Points[index + 1].Position;
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;

            var t = lengthSquared <= 0.000001d
                ? 0d
                : ((position.X - a.X) * dx + (position.Z - a.Z) * dz) / lengthSquared;

            t = Math.Clamp(t, 0d, 1d);

            var px = a.X + dx * t;
            var pz = a.Z + dz * t;
            var ddx = position.X - px;
            var ddz = position.Z - pz;
            var distance = ddx * ddx + ddz * ddz;

            // При одинаковом расстоянии выбираем более поздний сегмент:
            // это и есть правило «в пользу движения вперёд».
            if (distance < bestDistance - 0.000001d ||
                (Math.Abs(distance - bestDistance) <= 0.000001d &&
                 index > bestSegment))
            {
                bestDistance = distance;
                bestSegment = index;
                bestT = t;
            }
        }

        if (bestSegment == 0 && bestT <= 0.000001d)
            return 0;

        if (bestT >= 0.999999d)
            return Math.Min(bestSegment + 2, plan.Points.Count - 1);

        return bestSegment + 1;
    }

    /// <summary>
    /// Путевая точка, которая должна остаться первой после пересчёта маршрута
    /// до текущей позиции игрока.
    /// </summary>
    public static int? PreferredWaypointForPlayer(
        RoutePlan plan,
        WorldCoordinate position)
    {
        if (plan is null || plan.Points.Count == 0)
            return null;

        var pointIndex = NextRoutePointForPlayer(plan, position);
        if (pointIndex < 0 || pointIndex >= plan.Points.Count)
            return null;

        var destination = plan.Points[pointIndex].DestinationWaypointIndex;
        if (plan.WaypointSource is null || plan.WaypointSource.Count == 0)
            return Math.Max(0, destination);

        destination = Math.Clamp(
            destination,
            0,
            plan.WaypointSource.Count - 1);

        return destination;
    }

    public static int? PreferredForwardWaypoint(
        IReadOnlyList<RouteWaypoint>? waypoints,
        WorldCoordinate position,
        int? maxIndex = null,
        int? nearestIndex = null)
    {
        if (waypoints is null || waypoints.Count == 0)
            return null;

        var limit = maxIndex is int max
            ? Math.Clamp(max, 0, waypoints.Count - 1)
            : waypoints.Count - 1;

        var nearest = nearestIndex is int hinted &&
                      hinted >= 0 &&
                      hinted <= limit
            ? hinted
            : NearestForwardWaypoint(waypoints, position, limit);

        if (nearest is not int nearestValue)
            return null;

        var dx = waypoints[nearestValue].Position.X - position.X;
        var dz = waypoints[nearestValue].Position.Z - position.Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);

        // Ровно на достигнутой точке продолжаем вперёд, если такая точка есть.
        if (distance <= 0.01d && nearestValue < limit)
            return nearestValue + 1;

        return nearestValue;
    }

    public static int? NearestForwardWaypoint(
        IReadOnlyList<RouteWaypoint>? waypoints,
        WorldCoordinate position,
        int? maxIndex)
    {
        if (waypoints is null || waypoints.Count == 0)
            return null;

        var limit = maxIndex is int value
            ? Math.Clamp(value, 0, waypoints.Count - 1)
            : waypoints.Count - 1;

        var bestIndex = -1;
        var bestDistance = double.PositiveInfinity;

        for (var index = 0; index <= limit; index++)
        {
            var waypoint = waypoints[index];
            var dx = waypoint.Position.X - position.X;
            var dz = waypoint.Position.Z - position.Z;
            var distance = dx * dx + dz * dz;

            if (!double.IsFinite(distance))
                continue;

            if (distance < bestDistance - 0.000001d ||
                (Math.Abs(distance - bestDistance) <= 0.000001d &&
                 index > bestIndex))
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex >= 0 ? bestIndex : null;
    }

    public static int NearestRoutePoint(
        RoutePlan plan,
        WorldCoordinate position)
    {
        if (plan is null || plan.Points.Count == 0)
            return -1;

        var bestIndex = -1;
        var bestDistance = double.PositiveInfinity;

        for (var index = 0; index < plan.Points.Count; index++)
        {
            var point = plan.Points[index];
            var dx = point.Position.X - position.X;
            var dz = point.Position.Z - position.Z;
            var distance = dx * dx + dz * dz;

            if (!double.IsFinite(distance))
                continue;

            if (distance < bestDistance - 0.000001d ||
                (Math.Abs(distance - bestDistance) <= 0.000001d &&
                 index > bestIndex))
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    public static RouteCursor ProjectCursorToWaypoint(
        RoutePlan plan,
        int targetWaypointIndex,
        WorldCoordinate position,
        RouteCursor previous)
    {
        if (plan is null || plan.Points.Count == 0)
            return previous with { Initialized = false, NextRoutePointIndex = -1 };

        var bestIndex = -1;
        var bestDistance = double.PositiveInfinity;

        for (var index = 0; index < plan.Points.Count; index++)
        {
            if (plan.Points[index].DestinationWaypointIndex != targetWaypointIndex)
                continue;

            var dx = plan.Points[index].Position.X - position.X;
            var dz = plan.Points[index].Position.Z - position.Z;
            var distance = dx * dx + dz * dz;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex < 0
            ? previous with { Initialized = false, NextRoutePointIndex = -1 }
            : previous.ForPoint(bestIndex, previous.LastHeadingDegrees);
    }

    public static RouteCursor ProjectForwardCursor(
        RoutePlan plan,
        WorldCoordinate position,
        RouteCursor previous)
    {
        var index = NearestRoutePoint(plan, position);
        return index < 0
            ? previous with { Initialized = false, NextRoutePointIndex = -1 }
            : previous.ForPoint(index, previous.LastHeadingDegrees);
    }

    public static RouteCursor CreateResumeCursor(
        RoutePlan plan,
        int stoppedWaypointIndex,
        double lastHeadingDegrees)
    {
        if (plan is null || plan.Points.Count == 0)
            return RouteCursor.Initial with
            {
                ResumeAfterStop = true,
                StoppedAtWaypointIndex = stoppedWaypointIndex,
                LastHeadingDegrees = lastHeadingDegrees
            };

        var next = plan.Points
            .Select((point, index) => (point, index))
            .FirstOrDefault(item =>
                item.point.DestinationWaypointIndex > stoppedWaypointIndex);

        if (next.point is null)
        {
            return RouteCursor.Initial with
            {
                ResumeAfterStop = true,
                StoppedAtWaypointIndex = stoppedWaypointIndex,
                LastHeadingDegrees = lastHeadingDegrees
            };
        }

        return RouteCursor.Initial.ForPoint(next.index, lastHeadingDegrees) with
        {
            ResumeAfterStop = true,
            StoppedAtWaypointIndex = stoppedWaypointIndex
        };
    }

    public static double RouteDistanceMeters(
        RoutePlan plan,
        RouteCursor cursor,
        int? stoppedWaypointIndex,
        IReadOnlyList<double> cumulativeMeters)
    {
        if (plan is null || plan.Points.Count == 0)
            return 0d;

        if (stoppedWaypointIndex is int stopped &&
            stopped >= 0 &&
            stopped < cumulativeMeters.Count)
        {
            return cumulativeMeters[stopped];
        }

        var pointIndex = cursor.Initialized
            ? Math.Clamp(
                cursor.NextRoutePointIndex,
                0,
                plan.Points.Count - 1)
            : 0;

        var distance = 0d;
        for (var index = 0; index < pointIndex; index++)
            distance += plan.Segments.FirstOrDefault(
                item => item.StartPointId == plan.Points[index].Id)?.LengthMeters ?? 0d;

        return Math.Max(0d, distance);
    }

    private static double HeadingDegrees(double x, double z)
    {
        var heading = Math.Atan2(z, x) * 180d / Math.PI;
        return heading < 0 ? heading + 360d : heading;
    }
}
