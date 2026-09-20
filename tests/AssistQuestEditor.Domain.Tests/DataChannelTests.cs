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
    public void SnapshotContainsDemoWorldAndPlayer()
    {
        var hub = new SimulatorDataChannelHub();
        var snapshot = hub.GetSnapshot();

        Assert.Equal("ETS2 X/Y/Z • вид сверху использует X/Z", snapshot.World.CoordinateSystem);
        Assert.Equal(4, snapshot.World.Points.Count);
        Assert.Equal("ruslan", snapshot.World.Points[0].Id);
        Assert.True(snapshot.Player.InCab);
    }
}
