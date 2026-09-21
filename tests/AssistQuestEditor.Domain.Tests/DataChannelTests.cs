using Xunit;

using AssistQuestEditor.Domain;

namespace AssistQuestEditor.Domain.Tests;

public sealed class DataChannelTests
{
    [Fact]
    public void ChannelPublishesChangedValueAndSource()
    {
        var channel = new DataChannel<int>("demo", "Демо", 1);
        DataChannelChangedEventArgs<int>? received = null;

        channel.Changed += (_, args) => received = args;
        channel.Set(42, "Тест");

        Assert.Equal(42, channel.Value);
        Assert.NotNull(received);
        Assert.Equal(1, received!.PreviousValue);
        Assert.Equal(42, received.CurrentValue);
        Assert.Equal("Тест", received.Source);
    }

    [Fact]
    public void SimulatorHubContainsAllRequiredChannelGroups()
    {
        var hub = new SimulatorDataChannelHub();
        var keys = hub.Describe().Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("player", keys);
        Assert.Contains("world", keys);
        Assert.Contains("world-selection", keys);
        Assert.Contains("facts", keys);
        Assert.Contains("quest-statuses", keys);
        Assert.Contains("states", keys);
        Assert.Contains("inventory", keys);
        Assert.Contains("reputation", keys);
        Assert.Contains("telemetry", keys);
        Assert.Contains("environment", keys);
        Assert.Contains("system", keys);
    }

    [Fact]
    public void SnapshotUsesConfiguredWorldAndPlayerCenter()
    {
        var points = new[]
        {
            new WorldPoint("a", "Точка A", "test", new WorldCoordinate(-100, 10, -50)),
            new WorldPoint("b", "Точка B", "test", new WorldCoordinate(300, 30, 150))
        };

        var hub = new SimulatorDataChannelHub(points);
        var snapshot = hub.GetSnapshot();

        Assert.Equal("ETS2 X/Y/Z • вид сверху использует X/Z", snapshot.World.CoordinateSystem);
        Assert.Equal(2, snapshot.World.Points.Count);
        Assert.Equal("a", snapshot.World.Points[0].Id);
        Assert.Equal(100, snapshot.Player.Position.X);
        Assert.Equal(20, snapshot.Player.Position.Y);
        Assert.Equal(50, snapshot.Player.Position.Z);
        Assert.True(snapshot.Player.InCab);
    }

    [Fact]
    public void WorldSelectionStoresSelectedPoint()
    {
        var point = new WorldPoint(
            "sdo:test:1",
            "Категория",
            "test",
            new WorldCoordinate(1, 2, 3))
        {
            Editable = false
        };

        IDataChannelHub hub = new SimulatorDataChannelHub(new[] { point });
        hub.Get<WorldSelectionState>("world-selection").Set(
            new WorldSelectionState(point, "Тест"),
            "Тест");

        Assert.Equal(point, hub.Get<WorldSelectionState>("world-selection").Value.Point);
    }
}
