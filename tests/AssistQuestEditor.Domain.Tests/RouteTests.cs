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
    public void SingleWaypointRouteStartsFromCurrentPlayerPosition()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("target", new WorldCoordinate(90, 0, 0), 70)
        });

        var plan = planner.Build(route, new WorldCoordinate(10, 0, 0));

        Assert.True(plan.IsUsable);
        Assert.Single(plan.Legs);
        Assert.Equal(-1, plan.Legs[0].StartWaypointIndex);
        Assert.Equal(0, plan.Legs[0].EndWaypointIndex);
        Assert.Equal(new WorldCoordinate(10, 0, 0), plan.Legs[0].Polyline[0]);
        Assert.Equal(new WorldCoordinate(90, 0, 0), plan.Legs[0].Polyline[^1]);
    }

    [Fact]
    public void RouteMovementCanAdvanceToSingleWaypoint()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("target", new WorldCoordinate(100, 0, 0), 60)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            1);

        Assert.Equal(60, result.SpeedKmh, 6);
        Assert.Equal(50d / 3d, result.Position.X, 6);
        Assert.True(result.Enabled);
        Assert.False(result.Completed);
    }

    [Fact]
    public void ProjectCursorToWaypointStaysOnLogicalTargetAfterRouteEdit()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(0, 1,
                    new[]
                    {
                        new WorldCoordinate(0, 0, 0),
                        new WorldCoordinate(100, 0, 0)
                    },
                    100),
                new RouteLeg(1, 2,
                    new[]
                    {
                        new WorldCoordinate(100, 0, 0),
                        new WorldCoordinate(200, 0, 0)
                    },
                    100)
            },
            Array.Empty<string>());

        var previous = new RouteCursor(true, 1, 0, 40, false, null, 0);

        var cursor = RouteMovementEngine.ProjectCursorToWaypoint(
            plan,
            2,
            new WorldCoordinate(145, 0, 0),
            previous);

        Assert.True(cursor.Initialized);
        Assert.Equal(1, cursor.LegIndex);
        Assert.Equal(45, cursor.SegmentProgressMeters, 6);
        Assert.Equal(2, plan.Legs[cursor.LegIndex].EndWaypointIndex);
    }

    [Fact]
    public void ManualProjectionChoosesNextWaypointOnCurrentSegment()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(-1, 0,
                    new[]
                    {
                        new WorldCoordinate(0, 0, 0),
                        new WorldCoordinate(10, 0, 0)
                    }, 10),
                new RouteLeg(0, 1,
                    new[]
                    {
                        new WorldCoordinate(10, 0, 0),
                        new WorldCoordinate(100, 0, 0)
                    }, 90),
                new RouteLeg(1, 2,
                    new[]
                    {
                        new WorldCoordinate(100, 0, 0),
                        new WorldCoordinate(200, 0, 0)
                    }, 90),
                new RouteLeg(2, 3,
                    new[]
                    {
                        new WorldCoordinate(200, 0, 0),
                        new WorldCoordinate(300, 0, 0)
                    }, 90)
            },
            Array.Empty<string>());

        var cursor = RouteMovementEngine.ProjectForwardCursor(
            plan,
            new WorldCoordinate(145, 0, 0),
            new RouteCursor(true, 2, 0, 15, false, null, 0));

        Assert.Equal(2, cursor.LegIndex);
        Assert.Equal(45, cursor.SegmentProgressMeters, 6);

        var route = new RouteState(90, new[]
        {
            new RouteWaypoint("1", new WorldCoordinate(10, 0, 0), 90),
            new RouteWaypoint("2", new WorldCoordinate(100, 0, 0), 90),
            new RouteWaypoint("3", new WorldCoordinate(200, 0, 0), 90),
            new RouteWaypoint("4", new WorldCoordinate(300, 0, 0), 90)
        });

        var result = RouteMovementEngine.Advance(
            route,
            plan,
            cursor,
            new WorldCoordinate(145, 0, 0),
            1);

        Assert.Equal(170, result.Position.X, 6);
        Assert.Equal(90, result.SpeedKmh, 6);
    }

    [Fact]
    public void ManualProjectionPrefersFollowingLegAtSharedWaypoint()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(0, 1,
                    new[]
                    {
                        new WorldCoordinate(0, 0, 0),
                        new WorldCoordinate(100, 0, 0)
                    }, 100),
                new RouteLeg(1, 2,
                    new[]
                    {
                        new WorldCoordinate(100, 0, 0),
                        new WorldCoordinate(200, 0, 0)
                    }, 100)
            },
            Array.Empty<string>());

        var cursor = RouteMovementEngine.ProjectForwardCursor(
            plan,
            new WorldCoordinate(100, 0, 0),
            RouteCursor.Initial);

        Assert.Equal(1, cursor.LegIndex);
        Assert.Equal(0, cursor.SegmentProgressMeters, 6);
    }

    [Fact]
    public void RoutePlannerExplainsWhenWaypointIsTooFarFromRoad()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("target", new WorldCoordinate(1000, 0, 0), 60)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));

        Assert.False(plan.IsUsable);
        Assert.Contains(plan.Errors, error => error.Contains("1000.0") && error.Contains("допустимое расстояние привязки"));
    }

    [Fact]
    public void SingleWaypointWithZeroSpeedStopsAtWaypoint()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("target", new WorldCoordinate(80, 0, 0), 0)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            1);

        Assert.False(result.Enabled);
        Assert.True(result.StoppedAtWaypoint);
        Assert.Equal(0, result.Cursor.StoppedAtWaypointIndex);
        Assert.Equal(0, result.Position.X, 6);
    }

    [Fact]
    public void ResumeAfterZeroSpeedFirstWaypointUsesNextLegSpeed()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(100, 0, 200, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(80, 0, 0), 0),
            new RouteWaypoint("b", new WorldCoordinate(180, 0, 0), 90)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var cursor = RouteMovementEngine.CreateResumeCursor(plan, 0, 0);

        Assert.Equal(1, cursor.LegIndex);
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            cursor,
            new WorldCoordinate(80, 0, 0),
            1);

        Assert.True(result.Enabled);
        Assert.Equal(90, result.SpeedKmh, 6);
        Assert.Equal(105, result.Position.X, 6);
    }

    [Fact]
    public void StopsAtSecondZeroSpeedWaypointAndStoresNextLeg()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 250, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(50, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 0),
            new RouteWaypoint("c", new WorldCoordinate(200, 0, 0), 90)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            10);

        Assert.False(result.Enabled);
        Assert.True(result.StoppedAtWaypoint);
        Assert.Equal(1, result.Cursor.StoppedAtWaypointIndex);
        Assert.Equal(2, result.Cursor.LegIndex);
        Assert.Equal(100, result.Position.X, 6);
    }

    /// <summary>
    /// Последняя точка со скоростью 0 — это ОСТАНОВКА, а не завершение маршрута.
    ///
    /// Регрессия: завершение проверялось раньше остановки, поэтому финальная
    /// точка со скоростью 0 возвращала Completed. Пользовательский toggle
    /// оставался включённым, надпись «Остановка» не появлялась, и игрок
    /// «проезжал» точку без события.
    /// </summary>
    [Fact]
    public void LastWaypointWithZeroSpeedStopsInsteadOfCompleting()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 200, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(50, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 0)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            10);

        Assert.False(result.Enabled);
        Assert.True(result.StoppedAtWaypoint);
        Assert.False(result.Completed);
        Assert.Equal(1, result.Cursor.StoppedAtWaypointIndex);
        Assert.Equal(100, result.Position.X, 6);
        Assert.Equal(0, result.SpeedKmh, 6);
    }

    /// <summary>Последняя точка со скоростью &gt; 0 завершает маршрут, как и раньше.</summary>
    [Fact]
    public void LastWaypointWithPositiveSpeedCompletes()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 200, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(50, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 90)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            10);

        Assert.False(result.Enabled);
        Assert.False(result.StoppedAtWaypoint);
        Assert.True(result.Completed);
        Assert.Equal(100, result.Position.X, 6);
    }

    /// <summary>
    /// Единственная точка со скоростью 0 — это остановка, а не завершение.
    ///
    /// Игрок при этом НЕ трогается с места: скорость первого leg берётся у
    /// самой точки, и нулевая скорость означает «не ехать». Так устроен движок
    /// (см. SingleWaypointWithZeroSpeedStopsAtWaypoint), важно здесь другое —
    /// результат обязан быть остановкой, чтобы toggle выключился и появилась
    /// надпись, а не «маршрут пройден».
    /// </summary>
    [Fact]
    public void FirstAndOnlyWaypointWithZeroSpeedStopsInsteadOfCompleting()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 200, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(100, 0, 0), 0)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var result = RouteMovementEngine.Advance(
            route,
            plan,
            RouteCursor.Initial,
            new WorldCoordinate(0, 0, 0),
            10);

        Assert.False(result.Enabled);
        Assert.True(result.StoppedAtWaypoint);
        Assert.False(result.Completed);
        Assert.Equal(0, result.Cursor.StoppedAtWaypointIndex);
        Assert.Equal(0, result.Position.X, 6);
    }

    [Fact]
    public void ResumeAfterZeroSpeedKeepsUsingNextWaypointSpeedOnFollowingTick()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(-1, 0,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(10, 0, 0) },
                    10),
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(10, 0, 0), new WorldCoordinate(100, 0, 0) },
                    90),
                new RouteLeg(1, 2,
                    new[] { new WorldCoordinate(100, 0, 0), new WorldCoordinate(200, 0, 0) },
                    100)
            },
            Array.Empty<string>());

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 0),
            new RouteWaypoint("c", new WorldCoordinate(200, 0, 0), 90)
        });

        var cursor = RouteMovementEngine.CreateResumeCursor(plan, 1, 0);
        var first = RouteMovementEngine.Advance(
            route,
            plan,
            cursor,
            new WorldCoordinate(100, 0, 0),
            1);

        Assert.True(first.Enabled);
        Assert.Equal(90, first.SpeedKmh, 6);
        Assert.True(first.Cursor.ResumeAfterStop);

        var second = RouteMovementEngine.Advance(
            route,
            plan,
            first.Cursor,
            first.Position,
            1);

        Assert.True(second.Enabled);
        Assert.Equal(90, second.SpeedKmh, 6);
        Assert.True(second.Position.X > first.Position.X);
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
    public void RealWorldRouteGapBetweenNorthRoadFragmentsIsRecovered()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(158320.23, -77248.03, 158120.69, -77172.06),
            new RoadSegment(157680.12, -77072.67, 157837.48, -77138.06)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("p1", new WorldCoordinate(158308.6, 90, -77244.0), 60),
            new RouteWaypoint("p2", new WorldCoordinate(157722.6, 90, -77090.3), 60)
        });

        var plan = planner.Build(route);

        Assert.True(plan.IsUsable);
        Assert.Single(plan.Legs);
        Assert.True(plan.Legs[0].LengthMeters > 600);
    }

    [Fact]
    public void RoadGapUpToThreeHundredMetersIsRecovered()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 50, 0),
            new RoadSegment(350, 0, 400, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(390, 0, 0), 60)
        });

        Assert.True(planner.Build(route).IsUsable);
    }

    [Fact]
    public void RoadGapOverThreeHundredMetersIsRejected()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 50, 0),
            new RoadSegment(351, 0, 401, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(391, 0, 0), 60)
        });

        Assert.False(planner.Build(route).IsUsable);
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
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 10), 60),
            new RouteWaypoint("b", new WorldCoordinate(90, 0, 10), 60)
        });

        var plan = planner.Build(route);

        Assert.True(plan.IsUsable);
        Assert.Contains(
            plan.Legs[0].Polyline,
            point => Math.Abs(point.X - 50) < .01 && Math.Abs(point.Z - 50) < .01);
    }

    [Fact]
    public void OffRoadWaypointUsesDirectLeg()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 25, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("p1", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("p2", new WorldCoordinate(100, 0, 100), 60, true)
        });

        var plan = planner.Build(route, new WorldCoordinate(10, 0, 0));

        Assert.True(plan.IsUsable);
        var direct = Assert.Single(plan.Legs, item => item.StartWaypointIndex == 0);
        Assert.Equal(
            Math.Sqrt(90d * 90d + 100d * 100d),
            direct.LengthMeters,
            6);
        Assert.Equal(new WorldCoordinate(100, 0, 100), direct.Polyline[^1]);
    }

    /// <summary>
    /// Сдвиг нумерации точек сохраняет геометрию и перенумеровывает оба конца.
    /// Нужен, когда ведущие пройденные точки отброшены, а индексы обязаны
    /// остаться согласованными с полным списком маршрута.
    /// </summary>
    [Fact]
    public void ShiftWaypointIndicesRenumbersLegsWithoutChangingGeometry()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(100, 0, 200, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(0, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 60),
            new RouteWaypoint("c", new WorldCoordinate(200, 0, 0), 60)
        });

        var plan = planner.Build(route);
        var shifted = plan.ShiftWaypointIndices(4);

        Assert.Equal(plan.Legs.Count, shifted.Legs.Count);
        Assert.Equal(4, shifted.Legs[0].StartWaypointIndex);
        Assert.Equal(5, shifted.Legs[0].EndWaypointIndex);
        Assert.Equal(5, shifted.Legs[1].StartWaypointIndex);
        Assert.Equal(6, shifted.Legs[1].EndWaypointIndex);
        Assert.Equal(
            plan.Legs[0].Polyline.ToArray(),
            shifted.Legs[0].Polyline.ToArray());
        Assert.Equal(plan.Legs[0].LengthMeters, shifted.Legs[0].LengthMeters);
    }

    [Fact]
    public void ShiftWaypointIndicesKeepsVirtualPlayerLegAtMinusOne()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(100, 0, 0), 60)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        var shifted = plan.ShiftWaypointIndices(3);

        // Первый leg начинается от ИГРОКА и не является путевой точкой: его
        // начало обязано остаться -1, иначе движение стартовало бы не с игрока.
        Assert.Equal(-1, shifted.Legs[0].StartWaypointIndex);
        Assert.Equal(3, shifted.Legs[0].EndWaypointIndex);
    }

    [Fact]
    public void ShiftWaypointIndicesWithZeroReturnsSamePlan()
    {
        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 100, 0)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(100, 0, 0), 60)
        });

        var plan = planner.Build(route, new WorldCoordinate(0, 0, 0));
        Assert.Same(plan, plan.ShiftWaypointIndices(0));
    }

    [Fact]
    public void OffRoadWaypointCanStartNextRoadLeg()
    {        var planner = new RoadRoutePlanner(new[]
        {
            new RoadSegment(0, 0, 25, 0),
            new RoadSegment(100, 100, 150, 100)
        });

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("p1", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("p2", new WorldCoordinate(100, 0, 100), 60, true),
            new RouteWaypoint("p3", new WorldCoordinate(140, 0, 100), 60)
        });

        var plan = planner.Build(route, new WorldCoordinate(10, 0, 0));

        Assert.True(plan.IsUsable);
        Assert.Equal(3, plan.Legs.Count);
        Assert.True(plan.Legs[2].Polyline.Count >= 2);
        Assert.Equal(new WorldCoordinate(100, 0, 100), plan.Legs[2].Polyline[0]);
        Assert.Equal(new WorldCoordinate(140, 0, 100), plan.Legs[2].Polyline[^1]);
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
    public void VirtualPlayerPlanResumesAfterSecondZeroSpeedWaypointOnNextLeg()
    {
        var plan = new RoutePlan(
            new[]
            {
                new RouteLeg(-1, 0,
                    new[] { new WorldCoordinate(0, 0, 0), new WorldCoordinate(10, 0, 0) },
                    10),
                new RouteLeg(0, 1,
                    new[] { new WorldCoordinate(10, 0, 0), new WorldCoordinate(100, 0, 0) },
                    90),
                new RouteLeg(1, 2,
                    new[] { new WorldCoordinate(100, 0, 0), new WorldCoordinate(200, 0, 0) },
                    100)
            },
            Array.Empty<string>());

        var cursor = RouteMovementEngine.CreateResumeCursor(plan, 1, 0);

        Assert.Equal(2, cursor.LegIndex);
        Assert.True(cursor.ResumeAfterStop);

        var route = new RouteState(60, new[]
        {
            new RouteWaypoint("a", new WorldCoordinate(10, 0, 0), 60),
            new RouteWaypoint("b", new WorldCoordinate(100, 0, 0), 0),
            new RouteWaypoint("c", new WorldCoordinate(200, 0, 0), 90)
        });

        var result = RouteMovementEngine.Advance(
            route,
            plan,
            cursor,
            new WorldCoordinate(100, 0, 0),
            1);

        Assert.Equal(90, result.SpeedKmh, 6);
        Assert.Equal(125, result.Position.X, 6);
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
