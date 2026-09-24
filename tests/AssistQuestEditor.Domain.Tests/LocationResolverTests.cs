using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class LocationResolverTests
{
    [Fact]
    public void FixedLocationResolvesConfiguredWorldPoint()
    {
        var points = new[]
        {
            Point("a", "Дом A", "house-5", 0),
            Point("b", "Дрова", "firewood", 100)
        };
        var location = new LocationDefinition("fixed", "Фиксированная")
        {
            Mode = LocationMode.Fixed,
            WorldPointId = "b"
        };

        var result = new LocationResolver(123).Resolve(location, points);

        Assert.True(result.Success);
        Assert.Equal("b", result.Point!.Id);
    }

    [Fact]
    public void DynamicLocationFiltersByCategoryAndProducesRequestedRounds()
    {
        var points = new[]
        {
            Point("a", "Дом A", "house-5", 0),
            Point("b", "Дом B", "house-6", 100),
            Point("c", "Полиция", "police", 200)
        };
        var location = new LocationDefinition("houses", "Дома")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion(
                        "CategoryContains",
                        new Dictionary<string, string> { ["value"] = "house-" })
                }
            }
        };

        var result = new LocationResolver(42).Test(location, points, 8);

        Assert.True(result.Supported);
        Assert.Equal(8, result.Candidates.Count);
        Assert.All(result.Candidates, candidate =>
            Assert.Contains(candidate.CandidateId, new[] { "a", "b" }));
        Assert.Equal(2, result.Candidates.Select(candidate => candidate.CandidateId).Distinct().Count());
    }

    [Fact]
    public void UnsupportedProviderCriterionDoesNotPretendToMatch()
    {
        var points = new[] { Point("a", "Дом A", "house-5", 0) };
        var location = new LocationDefinition("provider", "Provider")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion(
                        "HouseTypeIs",
                        new Dictionary<string, string> { ["types"] = "[5,6]" })
                }
            }
        };

        var result = new LocationResolver(7).Test(location, points, 3);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, message => message.Contains("HouseTypeIs", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryLimitsTreatNeverUsedCandidateAsZero()
    {
        var points = new[]
        {
            Point("a", "A", "house", 0),
            Point("b", "B", "house", 100)
        };
        var location = new LocationDefinition("history", "История")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                History = new LocationHistoryConstraints
                {
                    MaxVisitCount = 0,
                    MaxSelectionCount = 0
                }
            }
        };

        var history = new FakeHistory(
            new LocationUsageRecord("history", "a")
            {
                SelectionCount = 2,
                VisitCount = 1
            });

        var result = new LocationResolver(1).Test(location, points, 1, history: history);

        Assert.True(result.Supported);
        Assert.Equal("b", result.Candidates.Single().CandidateId);
    }

    [Fact]
    public void DynamicDistanceCriterionUsesWorldCoordinates()
    {
        var points = new[]
        {
            Point("near", "Рядом", "house", 40),
            Point("far", "Далеко", "house", 150)
        };
        var location = new LocationDefinition("far", "Дальняя")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion(
                        "FartherThanPoint",
                        new Dictionary<string, string>
                        {
                            ["pointId"] = "near",
                            ["meters"] = "50"
                        })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1);

        Assert.True(result.Supported);
        Assert.Equal("far", result.Candidates.Single().CandidateId);
    }

    /// <summary>
    /// «Рядом есть категория»: мужчина рядом с котом.
    ///
    /// Проверяются обе стороны условия: точка с соседом проходит, точка без
    /// соседа — нет. Односторонний тест прошёл бы даже при инвертированной логике.
    /// </summary>
    [Fact]
    public void NearbyCategoryRequiresNeighbourWithinRadius()
    {
        var points = new[]
        {
            // Мужик с котом рядом (5 м) — подходит.
            Man("man-with-cat", 0),
            Cat("cat-near", 5),
            // Мужик, у которого кот в 300 м — не подходит при радиусе 50 м.
            Man("man-alone", 1000),
            Cat("cat-far", 1300)
        };
        var location = new LocationDefinition("cat-man", "Мужчина с котом")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("CategoryIs", new Dictionary<string, string> { ["value"] = "man" }),
                    new LocationCriterion("NearbyCategory", new Dictionary<string, string>
                    {
                        ["value"] = "cat",
                        ["meters"] = "50"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1);

        Assert.True(result.Supported);
        Assert.Equal("man-with-cat", result.Candidates.Single().CandidateId);
    }

    /// <summary>
    /// Сосед не должен считаться сам собой: точка категории «man» не может быть
    /// соседом самой себе только потому, что её категория совпала с искомой.
    /// </summary>
    [Fact]
    public void NearbyCategoryDoesNotMatchCandidateItself()
    {
        var points = new[] { Man("only-man", 0) };
        var location = new LocationDefinition("self", "Сам себе сосед")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("NearbyCategory", new Dictionary<string, string>
                    {
                        ["value"] = "man",
                        ["meters"] = "100"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1);

        Assert.True(result.Supported);
        Assert.Empty(result.Candidates);
    }

    /// <summary>
    /// «В радиусе нет категории»: отдельно стоящий человек.
    ///
    /// Условие НЕ эквивалентно «НЕ (рядом есть категория)»: здесь требуется
    /// отсутствие ВСЕХ соседей, поэтому проверяются три случая — один сосед в
    /// радиусе (не подходит), сосед вне радиуса и полное отсутствие соседей
    /// (подходят).
    /// </summary>
    [Fact]
    public void NoNearbyCategoryRejectsAnyNeighbourInRadius()
    {
        var points = new[]
        {
            // Одинокий: рядом никого — подходит.
            Man("lonely", 0),
            // С соседом в 10 м — не подходит при радиусе 100 м.
            Man("crowded", 1000),
            Cat("cat-close", 1010),
            // Сосед есть, но далеко (500 м) — подходит.
            Man("distant", 5000),
            Cat("cat-distant", 5500)
        };
        var location = new LocationDefinition("alone", "Одинокий человек")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("CategoryIs", new Dictionary<string, string> { ["value"] = "man" }),
                    new LocationCriterion("NoNearbyCategory", new Dictionary<string, string>
                    {
                        ["value"] = "cat",
                        ["meters"] = "100"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 2);

        Assert.True(result.Supported);
        var chosen = result.Candidates.Select(candidate => candidate.CandidateId).Distinct().ToArray();
        Assert.Contains("lonely", chosen);
        Assert.Contains("distant", chosen);
        Assert.DoesNotContain("crowded", chosen);
    }

    /// <summary>
    /// Критерий соседства без радиуса не должен молча «срабатывать»: иначе
    /// результат зависел бы от невидимой подстановки нуля.
    /// </summary>
    [Fact]
    public void NearbyCategoryWithoutRadiusIsReportedAsUnsupported()
    {
        var points = new[] { Man("a", 0), Cat("b", 1) };
        var location = new LocationDefinition("no-radius", "Без радиуса")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("NearbyCategory", new Dictionary<string, string> { ["value"] = "cat" })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
    }

    /// <summary>
    /// «Минимальная дистанция между точками» разводит раунды по площади.
    ///
    /// Критерий не отбраковывает кандидатов (все три точки категории «camping»
    /// остаются допустимыми), а влияет на ВЫБОР: при требовании 1000 м никакие
    /// две выбранные точки не могут оказаться ближе. Ближняя пара (10 м) при
    /// этом физически не может быть выбрана вместе.
    /// </summary>
    [Fact]
    public void MinDistanceBetweenCandidatesSpreadsSelectedPoints()
    {
        var points = new[]
        {
            Point("a", "Кемпинг A", "camping", 0),
            Point("b", "Кемпинг B", "camping", 10),
            Point("c", "Кемпинг C", "camping", 100000)
        };
        var location = new LocationDefinition("spread", "Разброс")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("CategoryIs", new Dictionary<string, string> { ["value"] = "camping" }),
                    new LocationCriterion("MinDistanceBetweenCandidates", new Dictionary<string, string>
                    {
                        ["meters"] = "1000"
                    })
                }
            }
        };

        var result = new LocationResolver(3).Test(location, points, 2);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Candidates.Count);

        var chosen = result.Candidates.Select(candidate => candidate.CandidateId).ToArray();
        Assert.Contains("c", chosen);
        // A и B находятся в 10 м друг от друга, поэтому вместе попасть не могут.
        Assert.False(chosen.Contains("a") && chosen.Contains("b"),
            "Точки в 10 м не должны попасть в выбор при требовании 1000 м: " + string.Join(", ", chosen));
    }

    /// <summary>
    /// Если соблюсти дистанцию невозможно, раунды всё равно выдаются, но
    /// пользователь получает объяснение: молча вернуть пустой результат хуже,
    /// чем показать точки и сказать, что дистанция не выдержана.
    /// </summary>
    [Fact]
    public void MinDistanceBetweenCandidatesReportsRelaxationWhenImpossible()
    {
        var points = new[]
        {
            Point("a", "Кемпинг A", "camping", 0),
            Point("b", "Кемпинг B", "camping", 10)
        };
        var location = new LocationDefinition("tight", "Тесно")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("CategoryIs", new Dictionary<string, string> { ["value"] = "camping" }),
                    new LocationCriterion("MinDistanceBetweenCandidates", new Dictionary<string, string>
                    {
                        ["meters"] = "1000000"
                    })
                }
            }
        };

        var result = new LocationResolver(3).Test(location, points, 2);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("не хватает", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Стратегический критерий не попадает в фильтрацию.
    ///
    /// Симптом был такой: критерий минимальной дистанции есть, дистанция между
    /// выбранными точками явно не соблюдается, а в диагностике — по строке
    /// «критерий не поддерживается» НА КАЖДУЮ точку мира. Сама стратегия отбора
    /// работала, но сообщения об ошибке забивали список, и предупреждение о
    /// несоблюдённой дистанции терялось среди них.
    /// </summary>
    [Fact]
    public void StrategyCriterionDoesNotProducePerPointDiagnostics()
    {
        var points = new[]
        {
            Point("a", "Кемпинг A", "camping", 0),
            Point("b", "Кемпинг B", "camping", 10),
            Point("c", "Кемпинг C", "camping", 100000)
        };
        var location = new LocationDefinition("quiet", "Без спама")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("CategoryIs", new Dictionary<string, string> { ["value"] = "camping" }),
                    new LocationCriterion("MinDistanceBetweenCandidates", new Dictionary<string, string>
                    {
                        ["meters"] = "1000"
                    })
                }
            }
        };

        var result = new LocationResolver(3).Test(location, points, 2);

        Assert.True(result.Supported);
        // Ни одного сообщения про неподдерживаемый критерий: стратегия обязана
        // быть отфильтрована до Matches, а не падать в его default-ветку.
        Assert.DoesNotContain(result.Diagnostics, message =>
            message.Contains("не поддерживается", StringComparison.OrdinalIgnoreCase));
        // Диагностика вообще пуста: дистанцию соблюсти можно (a и c), поэтому
        // предупреждать не о чем.
        Assert.Empty(result.Diagnostics);
    }

    /// <summary>
    /// «Радиус от игрока»: диапазон ограничивает выбор с двух сторон, а одно
    /// число задаёт только минимум.
    ///
    /// Проверяются обе формы и обе стороны: односторонний тест прошёл бы и при
    /// полностью проигнорированном критерии.
    /// </summary>
    [Fact]
    public void PlayerDistanceRangeLimitsBothSides()
    {
        var points = new[]
        {
            Point("near", "Рядом", "man", 50),
            Point("mid", "Середина", "man", 500),
            Point("far", "Далеко", "man", 5000)
        };
        var player = new WorldCoordinate(0, 0, 0);

        var ranged = new LocationDefinition("ranged", "Диапазон")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("DistanceFromPlayer", new Dictionary<string, string>
                    {
                        ["meters"] = "100-1000"
                    })
                }
            }
        };

        var rangedResult = new LocationResolver(1).Test(ranged, points, 1, player);

        Assert.True(rangedResult.Supported);
        Assert.Equal("mid", rangedResult.Candidates.Single().CandidateId);

        // Одно число = МИНИМАЛЬНАЯ дистанция: «mid» и «far» подходят, «near» — нет.
        var minimum = new LocationDefinition("minimum", "Минимум")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("DistanceFromPlayer", new Dictionary<string, string>
                    {
                        ["meters"] = "150"
                    })
                }
            }
        };

        var minimumResult = new LocationResolver(1).Test(minimum, points, 3, player);

        Assert.True(minimumResult.Supported);
        var picked = minimumResult.Candidates.Select(item => item.CandidateId).Distinct().ToArray();
        Assert.Contains("mid", picked);
        Assert.Contains("far", picked);
        Assert.DoesNotContain("near", picked);
    }

    /// <summary>
    /// Отсутствие позиции игрока — ошибка окружения, а не свойство точек.
    ///
    /// Поэтому должно быть ОДНО внятное сообщение. Если проверять позицию внутри
    /// фильтра, сообщение размножится на каждую точку мира (у реального мира это
    /// тысячи строк), и внятного объяснения в списке не будет видно.
    /// </summary>
    [Fact]
    public void PlayerDistanceWithoutPlayerPositionReportsSingleDiagnostic()
    {
        var points = new[]
        {
            Point("a", "Точка A", "man", 50),
            Point("b", "Точка B", "man", 500),
            Point("c", "Точка C", "man", 5000)
        };
        var location = new LocationDefinition("noplayer", "Без игрока")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("DistanceFromPlayer", new Dictionary<string, string>
                    {
                        ["meters"] = "100-1000"
                    })
                }
            }
        };

        // Позиция не передана намеренно.
        var result = new LocationResolver(1).Test(location, points, 1);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Single(result.Diagnostics);
        Assert.Contains("позиция игрока неизвестна", result.Diagnostics.Single(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Перепутанные границы диапазона — ошибка настройки с ТОЧНЫМ сообщением.
    ///
    /// «1000-100» синтаксически корректно, поэтому общее «задайте число или
    /// диапазон» вводило бы в заблуждение: пользователь написал ровно то, что
    /// просили. Сообщение обязано называть настоящую проблему — порядок границ.
    /// </summary>
    [Fact]
    public void PlayerDistanceSwappedRangeReportsMinAboveMax()
    {
        var points = new[] { Point("a", "Точка A", "man", 50) };
        var location = new LocationDefinition("swapped", "Наоборот")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("DistanceFromPlayer", new Dictionary<string, string>
                    {
                        ["meters"] = "1000-100"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1, new WorldCoordinate(0, 0, 0));

        Assert.False(result.Supported);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("больше максимума", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// «Рядом с дорогой»: кандидат не дальше указанного расстояния от дороги.
    ///
    /// Проверяются обе стороны: точка у дороги проходит, далёкая — нет.
    /// Односторонний тест прошёл бы и при полностью проигнорированном критерии.
    ///
    /// Геометрия: вертикальная дорога вдоль X = 0. Точка «near» отстоит на 20 м,
    /// «far» — на 5000 м.
    /// </summary>
    [Fact]
    public void NearbyRoadKeepsOnlyPointsCloseToRoad()
    {
        var roads = new RoadIndex(new[]
        {
            new RoadSegment(0, -10000, 0, 10000)
        });
        var points = new[]
        {
            Point("near", "У дороги", "camping", 20),
            Point("far", "Далеко", "camping", 5000)
        };
        var location = new LocationDefinition("roadside", "У дороги")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("NearbyRoad", new Dictionary<string, string>
                    {
                        ["meters"] = "100"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 2, roads: roads);

        Assert.True(result.Supported);
        var picked = result.Candidates.Select(item => item.CandidateId).Distinct().ToArray();
        Assert.Contains("near", picked);
        Assert.DoesNotContain("far", picked);
    }

    /// <summary>
    /// Расстояние считается до ОТРЕЗКА, а не до его концов.
    ///
    /// Точка стоит напротив середины длинной дороги: до обоих концов тысячи
    /// метров, но до самой дороги — метры. Проверка «до концов» отбраковала бы
    /// корректный кандидат, и критерий работал бы только вблизи узлов.
    /// </summary>
    [Fact]
    public void NearbyRoadMeasuresDistanceToSegmentNotToEndpoints()
    {
        var roads = new RoadIndex(new[]
        {
            new RoadSegment(-10000, 0, 10000, 0)
        });
        var points = new[]
        {
            // Напротив середины: до концов 10 км, до дороги — 15 м.
            Point("mid", "Напротив середины", "camping", 0) with
            {
                Position = new WorldCoordinate(0, 0, 15)
            }
        };
        var location = new LocationDefinition("segment", "Отрезок")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("NearbyRoad", new Dictionary<string, string>
                    {
                        ["meters"] = "50"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1, roads: roads);

        Assert.True(result.Supported);
        Assert.Single(result.Candidates);
    }

    /// <summary>
    /// Без дорожной геометрии критерий — ошибка окружения с ОДНИМ сообщением.
    ///
    /// Дороги грузятся отдельным файлом и могут отсутствовать. Проверка обязана
    /// быть до фильтрации: иначе на каждую из 5000 точек мира появилось бы по
    /// диагностике, и причина в списке не читалась бы.
    /// </summary>
    [Fact]
    public void NearbyRoadWithoutGeometryReportsSingleDiagnostic()
    {
        var points = new[]
        {
            Point("a", "Точка A", "camping", 0),
            Point("b", "Точка B", "camping", 100),
            Point("c", "Точка C", "camping", 200)
        };
        var location = new LocationDefinition("noroards", "Без дорог")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("NearbyRoad", new Dictionary<string, string>
                    {
                        ["meters"] = "100"
                    })
                }
            }
        };

        // Геометрия не передана намеренно.
        var result = new LocationResolver(1).Test(location, points, 1);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Single(result.Diagnostics);
        Assert.Contains("дорожная геометрия не загружена", result.Diagnostics.Single(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Нулевое расстояние до дороги — ошибка настройки, а не «ничего не найдено».
    ///
    /// Точка ровно на линии дороги практически не встречается, поэтому при нуле
    /// критерий выглядел бы всегда ложным. Автор обязан узнать настоящую причину.
    /// </summary>
    [Fact]
    public void NearbyRoadWithZeroDistanceReportsConfigurationProblem()
    {
        var roads = new RoadIndex(new[] { new RoadSegment(0, 0, 100, 0) });
        var points = new[] { Point("a", "Точка A", "camping", 0) };
        var location = new LocationDefinition("zero", "Ноль")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion("NearbyRoad", new Dictionary<string, string>
                    {
                        ["meters"] = "0"
                    })
                }
            }
        };

        var result = new LocationResolver(1).Test(location, points, 1, roads: roads);

        Assert.False(result.Supported);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("больше нуля", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Индекс дорожной геометрии возвращает корректное расстояние.
    ///
    /// Прямая проверка <see cref="RoadIndex"/>: критерий опирается на него, и
    /// ошибка в пространственной сетке (например, отрезок не попал в ячейку)
    /// дала бы «бесконечно далеко» там, где дорога рядом.
    /// </summary>
    [Fact]
    public void RoadIndexFindsNearestSegmentAcrossCells()
    {
        // Отрезки разнесены далеко друг от друга, чтобы сетка точно разложила их
        // по разным ячейкам (размер ячейки — 500 м).
        var index = new RoadIndex(new[]
        {
            new RoadSegment(0, 0, 100, 0),
            new RoadSegment(10000, 0, 10100, 0),
            new RoadSegment(0, 20000, 100, 20000)
        });

        Assert.Equal(3, index.SegmentCount);

        // Точка в 10 м над первым отрезком.
        Assert.Equal(10, index.DistanceToNearest(50, 10, 100), 3);
        // Точка в 10 м над вторым (дальним) отрезком.
        Assert.Equal(10, index.DistanceToNearest(10050, 10, 100), 3);
        // Точка в 15 м над третьим отрезком.
        Assert.Equal(15, index.DistanceToNearest(50, 20015, 100), 3);

        // Пустой индекс — «бесконечно далеко», а не исключение.
        Assert.True(double.IsPositiveInfinity(
            new RoadIndex(Array.Empty<RoadSegment>()).DistanceToNearest(0, 0, 100)));
    }

    // --- Критерий «В радиусе от перекрёстка» ---

    /// <summary>
    /// Критерий оставляет только точки в заданном диапазоне расстояний до узла.
    ///
    /// Проверяются ОБА конца диапазона: слишком близкие точки (ближе минимума) и
    /// слишком далёкие (дальше максимума) должны отбраковываться. Без минимума
    /// критерий не отличал бы «рядом с перекрёстком» от «ровно на перекрёстке».
    /// </summary>
    [Fact]
    public void JunctionRadiusKeepsOnlyPointsWithinRange()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("near", "Рядом", "man", 0),      // 50 м до узла
            Point("middle", "Середина", "man", 300), // 300 м
            Point("far", "Далеко", "man", 2000)    // 2000 м
        };

        var junctions = new JunctionIndex(new[] { new JunctionPoint(0, 0) });

        var location = DynamicLocation(("NearbyJunction", new Dictionary<string, string>
        {
            ["meters"] = "100-500"
        }));

        var result = resolver.Test(location, world, 8, junctions: junctions);

        Assert.True(result.Supported);
        Assert.All(result.Candidates, candidate =>
            Assert.Equal("middle", candidate.CandidateId));
    }

    /// <summary>
    /// Одно число — это МИНИМУМ, а не точное расстояние.
    ///
    /// Тот же смысл, что у «Радиуса от игрока»: автор, написав «300», имеет в виду
    /// «не ближе 300 м от перекрёстка», а не «ровно 300». Если бы значение
    /// трактовалось как максимум, критерий молча означал бы противоположное.
    /// </summary>
    [Fact]
    public void JunctionRadiusTreatsSingleNumberAsMinimum()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("atJunction", "На перекрёстке", "man", 0),
            Point("faraway", "Далёкая", "man", 5000)
        };

        var junctions = new JunctionIndex(new[] { new JunctionPoint(0, 0) });

        var location = DynamicLocation(("NearbyJunction", new Dictionary<string, string>
        {
            ["meters"] = "300"
        }));

        var result = resolver.Test(location, world, 4, junctions: junctions);

        Assert.True(result.Supported);
        Assert.All(result.Candidates, candidate =>
            Assert.Equal("faraway", candidate.CandidateId));
    }

    /// <summary>
    /// Без списка перекрёстков критерий даёт ОДНУ диагностику и Supported = false.
    ///
    /// Проверка выполняется до фильтрации: внутри неё сообщение размножилось бы
    /// на каждую точку мира (тысячи одинаковых строк), и причины было бы не найти.
    /// </summary>
    [Fact]
    public void JunctionRadiusWithoutGeometryReportsSingleDiagnostic()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("a", "Точка", "man", 0), Point("b", "Точка-2", "man", 100) };

        var location = DynamicLocation(("NearbyJunction", new Dictionary<string, string>
        {
            ["meters"] = "100-500"
        }));

        var result = resolver.Test(location, world, 4);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Single(result.Diagnostics);
        Assert.Contains("перекрёстк", result.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Перепутанные границы диапазона — ошибка настройки, а не «ничего не найдено».
    ///
    /// «500-100» синтаксически корректный диапазон, но бессмысленный. Молчаливое
    /// «подходящих кандидатов нет» заставило бы автора искать проблему в данных.
    /// </summary>
    [Fact]
    public void JunctionRadiusWithReversedBoundsReportsConfigurationProblem()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("a", "Точка", "man", 0) };

        var location = DynamicLocation(("NearbyJunction", new Dictionary<string, string>
        {
            ["meters"] = "500-100"
        }));

        var result = resolver.Test(
            location, world, 4,
            junctions: new JunctionIndex(new[] { new JunctionPoint(0, 0) }));

        Assert.False(result.Supported);
        Assert.Contains(result.Diagnostics, item => item.Contains("больше максимума", StringComparison.Ordinal));
    }

    /// <summary>
    /// Индекс перекрёстков возвращает корректное расстояние через границы ячеек.
    ///
    /// Узлы разнесены так, чтобы сетка точно разложила их по разным ячейкам
    /// (размер ячейки — 250 м). Ошибка в раскладке дала бы «бесконечно далеко»
    /// там, где узел рядом, то есть критерий молча ничего не находил бы.
    /// </summary>
    [Fact]
    public void JunctionIndexFindsNearestPointAcrossCells()
    {
        var index = new JunctionIndex(new[]
        {
            new JunctionPoint(0, 0),
            new JunctionPoint(10000, 0),
            new JunctionPoint(0, 20000)
        });

        Assert.Equal(3, index.JunctionCount);

        Assert.Equal(10, index.DistanceToNearest(0, 10, 100), 3);
        Assert.Equal(10, index.DistanceToNearest(10000, 10, 100), 3);
        Assert.Equal(15, index.DistanceToNearest(0, 20015, 100), 3);

        Assert.True(double.IsPositiveInfinity(
            new JunctionIndex(Array.Empty<JunctionPoint>()).DistanceToNearest(0, 0, 100)));
    }

    /// <summary>
    /// Порядок узлов сохраняется при загрузке.
    ///
    /// Кнопки «следующий/предыдущий» в панели ручной проверки обязаны идти в том
    /// порядке, в котором перекрёстки нашлись: иначе автор проходил бы их в
    /// случайной последовательности и не мог бы продолжить проверку с того места,
    /// где остановился.
    /// </summary>
    [Fact]
    public void JunctionIndexKeepsSourceOrder()
    {
        var source = new[]
        {
            new JunctionPoint(300, 0),
            new JunctionPoint(100, 0),
            new JunctionPoint(200, 0)
        };

        var index = new JunctionIndex(source);

        Assert.Equal(source, index.ToPoints());
    }

    // --- Черты городов ---

    [Fact]
    public void InAnyCityKeepsOnlyPointsInsideBoundary()
    {
        var resolver = new LocationResolver(seed: 1);

        // Точки на оси X: 0 — центр квадрата, 250 — за его границей (100 м).
        var world = new[]
        {
            Point("inside", "В городе", "man", 0),
            Point("edge", "Почти на грани", "man", 99),
            Point("outside", "За городом", "man", 250)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });

        var location = DynamicLocation(("InAnyCity", new Dictionary<string, string>()));
        var result = resolver.Test(location, world, 8, cities: cities);

        Assert.True(result.Supported);
        // Раундов 8, а подходящих точек две: список кандидатов — выбранные
        // раунды, а не отфильтрованный мир, поэтому считаются РАЗЛИЧНЫЕ id.
        Assert.Equal(
            new[] { "edge", "inside" },
            result.Candidates.Select(candidate => candidate.CandidateId).Distinct().OrderBy(id => id));
        Assert.DoesNotContain(result.Candidates, candidate => candidate.CandidateId == "outside");
    }

    [Fact]
    public void InAnyCityWithoutBoundariesReportsSingleDiagnostic()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("a", "Точка", "man", 0) };

        var location = DynamicLocation(("InAnyCity", new Dictionary<string, string>()));
        var result = resolver.Test(location, world, 4);

        // Черты рисует автор вручную, поэтому их отсутствие — не поломка данных,
        // но поиск обязан сказать об этом внятно, а не вернуть пустой список.
        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Single(result.Diagnostics);
        Assert.Contains("черт", result.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InCityBoundaryMatchesByNameAndById()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("inside", "В городе", "man", 0),
            Point("far", "Далеко", "man", 5000)
        };

        var cities = new CityBoundaryIndex(new[]
        {
            Square("city:kazan", "Казань"),
            Square("city:arsk", "Арск", centerX: 10000)
        });

        // Имя: автор выбирает город из списка и видит имя, а не идентификатор.
        var byName = resolver.Test(
            DynamicLocation(("InCityBoundary", new Dictionary<string, string> { ["city"] = "Казань" })),
            world, 4, cities: cities);

        Assert.True(byName.Supported);
        Assert.All(byName.Candidates, candidate => Assert.Equal("inside", candidate.CandidateId));

        // Идентификатор: так критерий выглядит в сохранённом файле.
        var byId = resolver.Test(
            DynamicLocation(("InCityBoundary", new Dictionary<string, string> { ["city"] = "city:kazan" })),
            world, 4, cities: cities);

        Assert.True(byId.Supported);
        Assert.All(byId.Candidates, candidate => Assert.Equal("inside", candidate.CandidateId));

        // Арск стоит в 10 км: точка центра Казани внутрь его черты не попадает.
        var arsk = resolver.Test(
            DynamicLocation(("InCityBoundary", new Dictionary<string, string> { ["city"] = "Арск" })),
            world, 4, cities: cities);

        Assert.True(arsk.Supported);
        Assert.Empty(arsk.Candidates);
    }

    [Fact]
    public void NegatedCityCriteriaSelectSuburbs()
    {
        var resolver = new LocationResolver(seed: 1);

        // «Пригород» — это точка вне черты: ровно то, что даёт галочка «Нет».
        var world = new[]
        {
            Point("inside", "В городе", "man", 0),
            Point("suburbA", "Пригород-1", "man", 300),
            Point("suburbB", "Пригород-2", "man", -800)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });

        var anyCity = resolver.Test(
            NegatedDynamicLocation(("InAnyCity", new Dictionary<string, string>())),
            world, 8, cities: cities);

        Assert.True(anyCity.Supported);
        Assert.Equal(
            new[] { "suburbA", "suburbB" },
            anyCity.Candidates.Select(candidate => candidate.CandidateId).Distinct().OrderBy(id => id));
        Assert.DoesNotContain(anyCity.Candidates, candidate => candidate.CandidateId == "inside");

        var outsideKazan = resolver.Test(
            NegatedDynamicLocation(("InCityBoundary", new Dictionary<string, string> { ["city"] = "Казань" })),
            world, 8, cities: cities);

        Assert.True(outsideKazan.Supported);
        Assert.Equal(
            new[] { "suburbA", "suburbB" },
            outsideKazan.Candidates.Select(candidate => candidate.CandidateId).Distinct().OrderBy(id => id));
        Assert.DoesNotContain(outsideKazan.Candidates, candidate => candidate.CandidateId == "inside");
    }

    [Fact]
    public void InCityBoundaryWithoutThatCityBoundaryReportsTheCity()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("a", "Точка", "man", 0) };

        // Черта Казани нарисована, черты Арска нет: автор должен узнать, что
        // рисовать надо именно Арск, а не получить «кандидатов нет».
        var cities = new CityBoundaryIndex(new[] { Square("city:kazan", "Казань") });

        var location = DynamicLocation(("InCityBoundary", new Dictionary<string, string> { ["city"] = "Арск" }));
        var result = resolver.Test(location, world, 4, cities: cities);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("Арск", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InCityBoundaryWithoutCityParameterIsConfigurationError()
    {
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("a", "Точка", "man", 0) };
        var cities = new CityBoundaryIndex(new[] { Square() });

        var location = DynamicLocation(("InCityBoundary", new Dictionary<string, string>()));

        var result = resolver.Test(location, world, 4, cities: cities);

        // Без города критерий невыполним в принципе, и молчаливый пустой результат
        // увёл бы автора искать причину в данных мира.
        Assert.False(result.Supported);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("город", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CityBoundaryGeometryHandlesConcaveAndDegenerateShapes()
    {
        // Вогнутый «П»-образный контур: точка в вырезе снаружи, хотя её
        // прямоугольник накрывает. Проверка только по рамке дала бы ложное «внутри».
        var horseshoe = new CityBoundary("city:test", "Тест", new[]
        {
            new CityBoundaryPoint(0, 0),
            new CityBoundaryPoint(100, 0),
            new CityBoundaryPoint(100, 100),
            new CityBoundaryPoint(70, 100),
            new CityBoundaryPoint(70, 30),
            new CityBoundaryPoint(30, 30),
            new CityBoundaryPoint(30, 100),
            new CityBoundaryPoint(0, 100)
        });

        Assert.True(horseshoe.Contains(15, 50));
        Assert.True(horseshoe.Contains(85, 50));
        Assert.False(horseshoe.Contains(50, 80));

        // Вырожденные области не содержат ничего: у них нет внутренности, и
        // «угадывать» её значило бы считать перекрёстком любую пару вершин.
        Assert.False(new CityBoundary("c", "Т", new[]
        {
            new CityBoundaryPoint(0, 0),
            new CityBoundaryPoint(10, 0)
        }).Contains(5, 0));

        Assert.False(new CityBoundary("c", "Т", Array.Empty<CityBoundaryPoint>()).Contains(0, 0));

        // Нечисловая вершина делает область негодной ЦЕЛИКОМ, а не «чинится»
        // выбрасыванием: чинить чужой контур молча — значит менять замысел автора.
        var broken = new CityBoundary("c", "Т", new[]
        {
            new CityBoundaryPoint(0, 0),
            new CityBoundaryPoint(double.NaN, 0),
            new CityBoundaryPoint(0, 100)
        });

        Assert.False(broken.IsValid);
        Assert.False(broken.Contains(0, 50));
    }

    [Fact]
    public void MissingCitiesListsEveryCityWithoutBoundary()
    {
        var index = new CityBoundaryIndex(new[]
        {
            Square("city:kazan", "Казань"),
            Square("city:arsk", "Арск", centerX: 10000)
        });

        var missing = index.MissingCities(new[]
        {
            new CityReference("city:kazan", "Казань"),
            new CityReference("city:arsk", "Арск"),
            new CityReference("city:bugulma", "Бугульма"),
            new CityReference("city:ufa", "Уфа")
        });

        // Именно это и есть «проверка, которая уведомляет»: мир расширяется,
        // города добавляются, и узнать об этом надо явным списком.
        Assert.Equal(2, missing.Count);
        Assert.Equal(new[] { "Бугульма", "Уфа" }, missing.Select(city => city.Name));

        Assert.True(index.HasBoundaryFor("Казань"));
        Assert.True(index.HasBoundaryFor("city:kazan"));
        Assert.False(index.HasBoundaryFor("Уфа"));

        // Пустой индекс: без черты все города, и это не ошибка, а состояние
        // первого запуска.
        Assert.Equal(4, CityBoundaryIndex.Empty.MissingCities(new[]
        {
            new CityReference("city:kazan", "Казань"),
            new CityReference("city:arsk", "Арск"),
            new CityReference("city:bugulma", "Бугульма"),
            new CityReference("city:ufa", "Уфа")
        }).Count);
    }

    [Fact]
    public void CityBoundaryTitleIsBuiltAsAuthorRequested()
    {
        // Название собирается в домене, потому что его видят и окно рисования,
        // и панель критериев, и журнал — расходиться они не должны.
        Assert.Equal("Черта города Казань", Square(cityName: "Казань").Title);
        Assert.Equal("Черта города (без названия)", Square(cityName: "").Title);
    }

    // --- Объяснение пустого результата ---

    [Fact]
    public void CategoryThatNoPointHasIsReportedAsTheCulprit()
    {
        // Симптом из жизни: автор вводит «охрана» (русское имя объекта) вместо
        // категории «ohrana», черта города нарисована верно, точки внутри есть —
        // и всё равно «Подходящих кандидатов нет». Без объяснения причину искать
        // негде: подозревается и геометрия, и данные мира, и сам поиск.
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("guard", "Охранник", "ohrana", 0),
            Point("shop", "Продукты", "producti", 10)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });
        var location = DynamicLocation(
            ("InAnyCity", new Dictionary<string, string>()),
            ("CategoryIs", new Dictionary<string, string> { ["value"] = "охрана" }));

        var result = resolver.Test(location, world, 4, cities: cities);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("охрана", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("CategoryIs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CorrectCategoryInsideBoundaryFindsTheGuard()
    {
        // Обратная сторона предыдущей проверки: геометрия и поиск исправны, и с
        // ПРАВИЛЬНОЙ категорией охранник в черте города находится. Без этой пары
        // тестов «диагностика сработала» ничего не говорит о том, находятся ли
        // точки на самом деле.
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("guard", "Охранник", "ohrana", 0),
            Point("shop", "Продукты", "producti", 10),
            Point("far", "Охранник", "ohrana", 5000)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });
        var location = DynamicLocation(
            ("InAnyCity", new Dictionary<string, string>()),
            ("CategoryIs", new Dictionary<string, string> { ["value"] = "ohrana" }));

        var result = resolver.Test(location, world, 1, cities: cities);

        Assert.True(result.Supported);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, candidate => Assert.Equal("guard", candidate.CandidateId));
    }

    [Fact]
    public void CombinedCriteriaThatEachKeepPointsDoNotBlameAnyCriterion()
    {
        // Точки отсеивает СОЧЕТАНИЕ критериев, а не один из них. Обвинять первый
        // попавшийся нельзя: это отправило бы автора править исправный критерий.
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("insideCat", "Кот", "cat", 0),
            Point("outsideMan", "Мужчина", "man", 5000)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });
        var location = DynamicLocation(
            ("InAnyCity", new Dictionary<string, string>()),
            ("CategoryIs", new Dictionary<string, string> { ["value"] = "man" }));

        var result = resolver.Test(location, world, 4, cities: cities);

        Assert.Empty(result.Candidates);
        Assert.DoesNotContain(result.Diagnostics, message =>
            message.Contains("отбраковал все точки мира", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("Подходящих кандидатов нет.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyWorldDoesNotBlameCriteria()
    {
        // Нет ни одной точки мира: это состояние данных, а не свойство критерия.
        var resolver = new LocationResolver(seed: 1);
        var location = DynamicLocation(("CategoryIs", new Dictionary<string, string> { ["value"] = "ohrana" }));

        var result = resolver.Test(location, Array.Empty<WorldPoint>(), 4);

        Assert.Contains(result.Diagnostics, message =>
            message.Contains("Подходящих кандидатов нет.", StringComparison.OrdinalIgnoreCase));
    }

    // --- Пошаговый отчёт о поиске ---

    [Fact]
    public void TraceShowsEachCriterionReachSeparately()
    {
        // Ради этого отчёт и заведён: критерии применяются ВМЕСТЕ, и по пустому
        // результату нельзя понять, какой из них отсеял точки. Здесь у каждого
        // критерия виден СВОЙ охват на всём мире, поэтому виновник называется
        // точно, а не угадывается.
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("guard", "Охранник", "ohrana", 0),
            Point("shop", "Продукты", "producti", 10),
            Point("far", "Охранник", "ohrana", 5000)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });
        var location = DynamicLocation(
            ("InAnyCity", new Dictionary<string, string>()),
            ("CategoryIs", new Dictionary<string, string> { ["value"] = "ohrana" }));

        var trace = new LocationSearchTrace();
        var result = resolver.Test(location, world, 1, cities: cities, trace: trace);

        Assert.NotEmpty(result.Candidates);

        // Шаги разбираются по НОМЕРУ критерия: имя есть ещё и в общем шаге
        // «Критерии», поэтому поиск по имени выбрал бы его.
        var cityStep = trace.Steps.First(step => step.Stage == "Критерий 1");
        Assert.Equal(2, cityStep.Remaining);
        Assert.Contains("InAnyCity", cityStep.Detail);

        // Категория «ohrana» есть у двух точек мира.
        var categoryStep = trace.Steps.First(step => step.Stage == "Критерий 2");
        Assert.Equal(2, categoryStep.Remaining);

        // Значение параметра обязано попасть в текст: самая частая причина пустого
        // результата — написание, и без значения её в отчёте не видно.
        Assert.Contains("ohrana", categoryStep.Detail);
    }

    [Fact]
    public void TraceNamesTheCriterionThatRejectedEverything()
    {
        // Отчёт обязан назвать виновника и по цифрам: у критерия с опечаткой охват
        // НОЛЬ, у исправного — больше нуля. Это и есть пошаговая диагностика.
        var resolver = new LocationResolver(seed: 1);
        var world = new[]
        {
            Point("guard", "Охранник", "ohrana", 0),
            Point("shop", "Продукты", "producti", 10)
        };

        var cities = new CityBoundaryIndex(new[] { Square() });
        var location = DynamicLocation(
            ("InAnyCity", new Dictionary<string, string>()),
            ("CategoryIs", new Dictionary<string, string> { ["value"] = "охрана" }));

        var trace = new LocationSearchTrace();
        var result = resolver.Test(location, world, 4, cities: cities, trace: trace);

        Assert.Empty(result.Candidates);

        var cityStep = trace.Steps.First(step => step.Stage == "Критерий 1");
        Assert.Equal(2, cityStep.Remaining);
        Assert.Contains("InAnyCity", cityStep.Detail);

        var categoryStep = trace.Steps.First(step => step.Stage == "Критерий 2");
        Assert.Equal(0, categoryStep.Remaining);
        Assert.Contains("охрана", categoryStep.Detail);

        // Последний шаг перед пустым результатом тоже называет виновника.
        Assert.Contains(trace.Steps, step =>
            step.Detail.Contains("отбраковал все точки мира", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TraceReportsEnvironmentBeforeFiltering()
    {
        // Отчёт начинается с окружения: «черт нет» — это первое, что должно быть
        // исключено, до любых догадок про критерии и данные мира.
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("guard", "Охранник", "ohrana", 0) };
        var location = DynamicLocation(("InAnyCity", new Dictionary<string, string>()));

        var trace = new LocationSearchTrace();
        resolver.Test(location, world, 1, trace: trace);

        var start = trace.Steps.First();
        Assert.Equal("Старт", start.Stage);
        Assert.Equal(1, start.Remaining);
        Assert.Contains("точек мира: 1", start.Detail);

        // Критериев указано столько, сколько задано.
        Assert.Contains(trace.Steps, step => step.Stage == "Критерии" && step.Detail.Contains("InAnyCity"));
    }

    [Fact]
    public void TraceIsNotBuiltWhenNotRequested()
    {
        // Отчёт требует отдельного прохода по всем точкам на каждый критерий, а
        // Resolve идёт по этому коду постоянно во время симуляции. Значит без
        // запроса никакой работы выполняться не должно.
        var resolver = new LocationResolver(seed: 1);
        var world = new[] { Point("guard", "Охранник", "ohrana", 0) };
        var location = DynamicLocation(("CategoryIs", new Dictionary<string, string> { ["value"] = "ohrana" }));

        var result = resolver.Test(location, world, 1);

        // Результат прежний, отчёта нет — параметр необязательный.
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public void CategoryIsAnyMatchesConfiguredCacheCategories()
    {
        var points = new[]
        {
            Point("ruin", "Разрушенное здание", "ruined_civ", 0),
            Point("crashed", "Разбитая машина", "crashed_car", 100),
            Point("shop", "Магазин", "shop", 200)
        };

        var location = DynamicLocation(("CategoryIsAny", new Dictionary<string, string>
        {
            ["value"] = "ruined_civ|crashed_car|dead_car"
        }));

        var result = new LocationResolver(1).Test(location, points, 3);

        Assert.True(result.Supported);
        var ids = result.Candidates.Select(item => item.CandidateId).Distinct().ToArray();
        Assert.Contains("ruin", ids);
        Assert.Contains("crashed", ids);
        Assert.DoesNotContain("shop", ids);
    }

    [Fact]
    public void DistanceFromNearestCityEnforcesMinimumDistance()
    {
        var points = new[]
        {
            Point("near-city", "Рядом с городом", "ruined_civ", 900),
            Point("far-city", "Далеко от города", "ruined_civ", 1500)
        };
        points = points.Select(point => point with
        {
            IsCity = false
        }).ToArray();

        var cities = new[]
        {
            Point("city", "Город", "Города", 0) with { IsCity = true }
        };

        var world = points.Concat(cities).ToArray();
        var location = DynamicLocation(("DistanceFromNearestCity", new Dictionary<string, string>
        {
            ["meters"] = "1000"
        }));

        var result = new LocationResolver(1).Test(location, world, 2);

        Assert.True(result.Supported);
        Assert.Equal("far-city", result.Candidates.Single().CandidateId);
    }

    private static LocationDefinition DynamicLocation(
        params (string Type, Dictionary<string, string> Parameters)[] criteria) =>
        new("location_test", "Тестовая локация")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = criteria
                    .Select(item => new LocationCriterion(item.Type, item.Parameters))
                    .ToArray()
            }
        };

    /// <summary>
    /// Локация с ГАЛОЧКОЙ «Нет» у каждого критерия: то, ради чего черты городов и
    /// заводились — пригороды («не в черте города»).
    /// </summary>
    private static LocationDefinition NegatedDynamicLocation(
        params (string Type, Dictionary<string, string> Parameters)[] criteria) =>
        new("location_test", "Тестовая локация")
        {
            Mode = LocationMode.Dynamic,
            Query = new LocationQueryDefinition
            {
                Criteria = criteria
                    .Select(item => new LocationCriterion(item.Type, item.Parameters, Negate: true))
                    .ToArray()
            }
        };

    /** Квадрат вокруг (0,0) со стороной 200 м: простая проверяемая область. */
    private static CityBoundary Square(
        string cityId = "city:kazan",
        string cityName = "Казань",
        double centerX = 0,
        double centerZ = 0,
        double halfSize = 100) =>
        new(cityId, cityName, new[]
        {
            new CityBoundaryPoint(centerX - halfSize, centerZ - halfSize),
            new CityBoundaryPoint(centerX + halfSize, centerZ - halfSize),
            new CityBoundaryPoint(centerX + halfSize, centerZ + halfSize),
            new CityBoundaryPoint(centerX - halfSize, centerZ + halfSize)
        });

    private static WorldPoint Man(string id, double x) => Point(id, "Мужчина", "man", x);
    private static WorldPoint Cat(string id, double x) => Point(id, "Кот", "cat", x);

    private static WorldPoint Point(string id, string name, string category, double x) =>
        new(id, name, category, new WorldCoordinate(x, 0, 0));

    private sealed class FakeHistory : ILocationUsageHistory
    {
        private readonly LocationUsageRecord _record;

        public FakeHistory(LocationUsageRecord record) => _record = record;

        public bool TryGet(string locationId, string candidateId, out LocationUsageRecord record)
        {
            if (_record.LocationId.Equals(locationId, StringComparison.OrdinalIgnoreCase) &&
                _record.CandidateId.Equals(candidateId, StringComparison.OrdinalIgnoreCase))
            {
                record = _record;
                return true;
            }

            record = null!;
            return false;
        }
    }
}
