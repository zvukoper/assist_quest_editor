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

    /// <summary>
    /// Сдвигает номера путевых точек всех leg на <paramref name="offset"/>.
    ///
    /// Нужно, когда план строится по УРЕЗАННОМУ списку точек (ведущие уже
    /// пройденные точки отброшены), а индексы обязаны остаться согласованными с
    /// ПОЛНЫМ списком: по ним ищется текущая цель, состояние курсора и подписи
    /// точек. Без сдвига «точка 1» плана указывала бы на пятую точку маршрута.
    /// </summary>
    public RoutePlan ShiftWaypointIndices(int offset)
    {
        if (offset == 0 || Legs.Count == 0)
            return this;

        // Виртуальный leg «игрок → точка» начинается НЕ путевой точкой, а
        // позицией игрока (-1). Сдвигать этот номер нельзя: движение стартовало
        // бы не от игрока, а от несуществующей точки.
        static int ShiftStart(int value, int delta) => value < 0 ? value : value + delta;

        return new RoutePlan(
            Legs
                .Select(leg => leg with
                {
                    StartWaypointIndex = ShiftStart(leg.StartWaypointIndex, offset),
                    EndWaypointIndex = leg.EndWaypointIndex + offset
                })
                .ToArray(),
            Errors);
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

            // Остановка проверяется ПЕРВОЙ, и это принципиально. Точка со
            // скоростью 0 останавливает маршрут даже когда она последняя:
            // раньше завершение проверялось раньше остановки, последняя точка
            // со скоростью 0 возвращала Completed вместо StoppedAtWaypoint,
            // пользовательский toggle оставался включённым, а игрок «проезжал»
            // остановку без события и без надписи.
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

    /// <summary>
    /// Проецирует позицию игрока только на leg, ведущий к указанной точке.
    ///
    /// Используется при перестроении уже начатого маршрута: геометрия leg могла
    /// измениться после редактирования, но логическая цель должна остаться той же.
    /// В отличие от общего поиска ближайшего сегмента метод не перепрыгивает на
    /// более ранний или более поздний leg.
    /// </summary>
    public static RouteCursor ProjectCursorToWaypoint(
        RoutePlan plan,
        int targetWaypointIndex,
        WorldCoordinate position,
        RouteCursor previous)
    {
        var legIndex = -1;

        for (var index = 0; index < plan.Legs.Count; index++)
        {
            if (plan.Legs[index].EndWaypointIndex == targetWaypointIndex)
            {
                legIndex = index;
                break;
            }
        }

        if (legIndex < 0)
        {
            return previous with
            {
                Initialized = false,
                ResumeAfterStop = false,
                StoppedAtWaypointIndex = null
            };
        }

        var leg = plan.Legs[legIndex];
        var points = leg.Polyline;
        var bestDistance = double.PositiveInfinity;
        var bestSegment = 0;
        var bestProgress = 0d;
        var bestHeading = previous.LastHeadingDegrees;

        for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex++)
        {
            var a = points[segmentIndex];
            var b = points[segmentIndex + 1];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;

            if (lengthSquared <= 0.000001d)
                continue;

            var length = Math.Sqrt(lengthSquared);
            var t = ((position.X - a.X) * dx + (position.Z - a.Z) * dz) / lengthSquared;
            t = Math.Clamp(t, 0d, 1d);

            var px = a.X + dx * t;
            var pz = a.Z + dz * t;
            var distance = Math.Sqrt(
                (position.X - px) * (position.X - px) +
                (position.Z - pz) * (position.Z - pz));

            if (distance >= bestDistance - 0.000001d)
                continue;

            bestDistance = distance;
            bestSegment = segmentIndex;
            bestProgress = length * t;
            bestHeading = HeadingDegrees(dx / length, dz / length);
        }

        if (!double.IsFinite(bestDistance))
        {
            return previous with
            {
                Initialized = false,
                ResumeAfterStop = false,
                StoppedAtWaypointIndex = null
            };
        }

        return new RouteCursor(
            true,
            legIndex,
            bestSegment,
            bestProgress,
            false,
            null,
            bestHeading);
    }

    /// <summary>
    /// Проецирует ручную позицию игрока на текущий физический сегмент маршрута.
    ///
    /// Если игрок находится между точками N и N+1, целевой всегда остаётся N+1,
    /// даже если до точки N физически ближе. При совпадении на общей вершине
    /// выбирается следующий leg с большим номером конечной точки.
    /// </summary>
    public static RouteCursor ProjectForwardCursor(
        RoutePlan plan,
        WorldCoordinate position,
        RouteCursor previous)
    {
        if (plan.Legs.Count == 0)
        {
            return previous with
            {
                Initialized = false,
                ResumeAfterStop = false,
                StoppedAtWaypointIndex = null
            };
        }

        var bestDistance = double.PositiveInfinity;
        var bestEndWaypointIndex = -1;
        var bestLeg = -1;
        var bestSegment = 0;
        var bestProgress = 0d;
        var bestHeading = previous.LastHeadingDegrees;

        for (var legIndex = 0; legIndex < plan.Legs.Count; legIndex++)
        {
            var leg = plan.Legs[legIndex];
            if (leg.StartWaypointIndex < 0)
                continue;

            var points = leg.Polyline;
            for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex++)
            {
                var a = points[segmentIndex];
                var b = points[segmentIndex + 1];
                var dx = b.X - a.X;
                var dz = b.Z - a.Z;
                var lengthSquared = dx * dx + dz * dz;

                if (lengthSquared <= 0.000001d)
                    continue;

                var length = Math.Sqrt(lengthSquared);
                var t = ((position.X - a.X) * dx + (position.Z - a.Z) * dz) / lengthSquared;
                t = Math.Clamp(t, 0d, 1d);

                var px = a.X + dx * t;
                var pz = a.Z + dz * t;
                var distance = Math.Sqrt(
                    (position.X - px) * (position.X - px) +
                    (position.Z - pz) * (position.Z - pz));

                if (distance >= bestDistance - 0.000001d &&
                    !(Math.Abs(distance - bestDistance) <= 0.000001d &&
                      leg.EndWaypointIndex > bestEndWaypointIndex))
                {
                    continue;
                }

                bestDistance = distance;
                bestEndWaypointIndex = leg.EndWaypointIndex;
                bestLeg = legIndex;
                bestSegment = segmentIndex;
                bestProgress = length * t;
                bestHeading = HeadingDegrees(dx / length, dz / length);
            }
        }

        if (bestLeg < 0)
        {
            return previous with
            {
                Initialized = false,
                ResumeAfterStop = false,
                StoppedAtWaypointIndex = null
            };
        }

        return new RouteCursor(
            true,
            bestLeg,
            bestSegment,
            bestProgress,
            false,
            null,
            bestHeading);
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

    /// <summary>
    /// Цель маршрута для позиции игрока, выбранная в пользу ДВИЖЕНИЯ ВПЕРЁД.
    ///
    /// Правило заказано явно: «алгоритм движения по маршруту всегда должен выбирать
    /// цель в пользу следования по маршруту к точке со старшим номером». Поэтому
    /// сначала берётся ближайшая к игроку точка, а при неоднозначности — та, у
    /// которой номер БОЛЬШЕ.
    ///
    /// Две разные неоднозначности, и обе решаются в пользу старшей точки:
    ///
    /// 1. Игрок стоит МЕЖДУ точками N и N+1 (равные или почти равные расстояния).
    ///    Продолжение пути — вперёд, к N+1; выбор N означал бы движение назад к
    ///    уже пройденной точке.
    /// 2. Игрок стоит РОВНО НА точке N. «Ближайшая» — это она сама, расстояние
    ///    ноль. Но стоящий на точке уже её достиг, и маршрут продолжается вперёд,
    ///    со следующей точки. Иначе после перезапуска или перемещения игрок
    ///    «застревал» бы на достигнутой точке, а цель показывалась бы на неё же.
    ///
    /// Именно второй случай давал «цель — точка №1»: после загрузки маршрута
    /// игрок начинал путь с точки №1, оказывался ровно на ней, и ближайшей
    /// оставалась она же — при нулевой разнице расстояний.
    ///
    /// <paramref name="maxIndex"/> ограничивает поиск сверху (прежняя цель): ручное
    /// перемещение «вперёд» не должно перескакивать намерение игрока.
    /// <paramref name="nearestIndex"/> — подсказка вызывающего: если он УЖЕ знает
    /// ближайшую точку (например, посчитал её другим правилом), она участвует в
    /// сравнении с кандидатом вперёд и выбирается старшая. Иначе ближайшая
    /// считается здесь.
    ///
    /// Возвращает <c>null</c>, если точек нет.
    /// </summary>
    public static int? PreferredForwardWaypoint(
        IReadOnlyList<RouteWaypoint>? waypoints,
        WorldCoordinate position,
        int? maxIndex = null,
        int? nearestIndex = null)
    {
        if (waypoints is null || waypoints.Count == 0)
            return null;

        var nearest = nearestIndex is int hint &&
                      hint >= 0 &&
                      hint < waypoints.Count
            ? hint
            : NearestForwardWaypoint(waypoints, position, maxIndex) ?? 0;

        var candidate = nearest + 1;

        // За пределами списка двигаться вперёд некуда: остаётся последняя точка.
        if (candidate >= waypoints.Count)
            return nearest;

        // Прежняя цель ограничивает поиск. Игрок стоит НА самой цели — это её
        // достижение, а не повод искать дальше: иначе телепорт на цель уводил бы
        // маршрут за неё.
        if (maxIndex is int limit && nearest >= limit)
            return nearest;

        return candidate;
    }

    /// <summary>
    /// Индекс путевой точки, ближайшей К ИГРОКУ среди тех, что лежат между ним и
    /// прежней целью.
    ///
    /// Нужен при РУЧНОМ перемещении игрока и при загрузке маршрута из файла: игрок
    /// может стоять в любом месте, а движение идёт только к текущей цели, поэтому
    /// промежуточные точки требуется найти явно.
    ///
    /// Именно БЛИЖАЙШАЯ, а не первая непройденная: при перемещении назад (игрок
    /// оказался перед точкой, которую уже проехал) первой непройденной была бы
    /// далёкая точка, и маршрут поехал бы через весь пропущенный участок.
    ///
    /// Возвращает <c>null</c>, если точек нет — вызывающий обязан оставить
    /// прежнее поведение.
    /// </summary>
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
            if (waypoint is null)
                continue;

            var dx = waypoint.Position.X - position.X;
            var dz = waypoint.Position.Z - position.Z;
            var distance = dx * dx + dz * dz;

            if (!double.IsFinite(distance) || distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestIndex = index;
        }

        // Ни одной конечной координаты (битые данные) — не назначаем цель.
        return bestIndex < 0 || !double.IsFinite(bestDistance)
            ? null
            : bestIndex;
    }

    /// <summary>
    /// Пройденная по маршруту дистанция в метрах от начала движения.
    ///
    /// Первый leg строится как виртуальный («текущая позиция игрока → точка 1») и
    /// имеет <see cref="RouteLeg.StartWaypointIndex"/> = −1. Его начала НЕТ в
    /// массиве накопленных длин, поэтому расстояние внутри виртуального leg
    /// считается от нуля: иначе подпись под маркером показывала бы «0.0 км» до
    /// первой путевой точки, то есть всю первую часть пути — ровно то, что
    /// выглядело как «одометр не работает».
    /// </summary>
    public static double RouteDistanceMeters(
        RoutePlan plan,
        RouteCursor cursor,
        int? stoppedWaypointIndex,
        IReadOnlyList<double> cumulativeMeters)
    {
        if (plan is null || cumulativeMeters is null || cumulativeMeters.Count == 0)
            return 0d;

        // Остановка: игрок стоит на самой точке, значит пройдено ровно до неё.
        if (!cursor.Initialized &&
            stoppedWaypointIndex is int stopped &&
            stopped >= 0 &&
            stopped < cumulativeMeters.Count)
        {
            return cumulativeMeters[stopped];
        }

        if (!cursor.Initialized ||
            plan.Legs.Count == 0 ||
            cursor.LegIndex < 0 ||
            cursor.LegIndex >= plan.Legs.Count)
        {
            return 0d;
        }

        var leg = plan.Legs[cursor.LegIndex];
        var legStart = leg.StartWaypointIndex;

        // Виртуальный leg начинается позицией игрока (индекс −1): его стартовая
        // точка — начало маршрута, а не путевая точка, поэтому база нулевая.
        var baseMeters = legStart >= 0 && legStart < cumulativeMeters.Count
            ? cumulativeMeters[legStart]
            : 0d;

        return Math.Max(0d, baseMeters + DistanceAlongLeg(leg, cursor));
    }

    private static double DistanceAlongLeg(RouteLeg leg, RouteCursor cursor)
    {
        var points = leg.Polyline;
        if (points is null || points.Count < 2)
            return 0d;

        var segment = Math.Clamp(cursor.SegmentIndex, 0, points.Count - 2);
        var distance = 0d;

        for (var index = 0; index < segment; index++)
        {
            var a = points[index];
            var b = points[index + 1];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            distance += Math.Sqrt(dx * dx + dz * dz);
        }

        var from = points[segment];
        var to = points[segment + 1];
        var segmentDx = to.X - from.X;
        var segmentDz = to.Z - from.Z;
        var segmentLength = Math.Sqrt(segmentDx * segmentDx + segmentDz * segmentDz);

        return distance + Math.Clamp(
            cursor.SegmentProgressMeters,
            0d,
            segmentLength);
    }

    private static double HeadingDegrees(double x, double z)
    {
        var heading = Math.Atan2(z, x) * 180d / Math.PI;
        return heading < 0d ? heading + 360d : heading;
    }
}
