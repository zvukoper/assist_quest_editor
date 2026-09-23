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
