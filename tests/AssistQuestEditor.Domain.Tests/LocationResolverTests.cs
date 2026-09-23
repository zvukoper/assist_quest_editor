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

        var result = new LocationResolver(1).Test(location, points, 1, history);

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
