using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Правило журнала событий Симулятора.
///
/// Проверка появилась после того, как журнал заполнился техническими строками
/// «ChannelChanged [Состояние игрока] Изменены потребности игрока» — по одной
/// на каждый тик накопления усталости (4 раза в секунду игровых). Журнал — это
/// список событий мира, а не поток внутренних значений.
/// </summary>
public sealed class SimulatorJournalPolicyTests
{
    [Theory]
    [InlineData("player")]
    [InlineData("player-vitals")]
    [InlineData("player-conditions")]
    [InlineData("telemetry")]
    [InlineData("sim-time")]
    public void InternalProcessChannelsAreNotJournalled(string channel)
    {
        Assert.False(SimulatorJournalPolicy.ShouldJournal("ChannelChanged", channel));
    }

    [Theory]
    [InlineData("facts")]
    [InlineData("states")]
    [InlineData("inventory")]
    [InlineData("reputation")]
    [InlineData("quest-statuses")]
    [InlineData("interfaces")]
    [InlineData("dynamic-events")]
    public void MeaningfulChannelChangesAreJournalled(string channel)
    {
        Assert.True(SimulatorJournalPolicy.ShouldJournal("ChannelChanged", channel));
    }

    [Theory]
    [InlineData("RouteWaypointPassed")]
    [InlineData("RouteMovementStopped")]
    [InlineData("RouteMovementCompleted")]
    [InlineData("RouteSpeedChanged")]
    [InlineData("RouteOffRoadStarted")]
    [InlineData("BurnoutApplied")]
    [InlineData("FieldSleep")]
    [InlineData("FullSleep")]
    [InlineData("InventoryChanged")]
    public void TypedEventsAreAlwaysJournalled(string eventType)
    {
        Assert.True(SimulatorJournalPolicy.ShouldJournal(eventType, channel: null));
        // Тип события важнее канала: даже если в payload случайно окажется
        // «тихий» канал, настоящее событие терять нельзя.
        Assert.True(SimulatorJournalPolicy.ShouldJournal(eventType, "player-vitals"));
    }

    [Fact]
    public void ChannelChangedWithoutChannelIsKept()
    {
        // Без имени канала событие нельзя отнести к внутренним процессам,
        // поэтому оно сохраняется: молча терять записи хуже, чем показать лишнюю.
        Assert.True(SimulatorJournalPolicy.ShouldJournal("ChannelChanged", channel: null));
    }

    [Fact]
    public void ChannelNamesAreCaseInsensitive()
    {
        Assert.False(SimulatorJournalPolicy.ShouldJournal("channelchanged", "PLAYER-VITALS"));
    }

    [Fact]
    public void ShouldJournalReadsPayloadDictionary()
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["channel"] = "player-vitals",
            ["description"] = "Изменены потребности игрока"
        };

        Assert.False(SimulatorJournalPolicy.ShouldJournal("ChannelChanged", payload));
        Assert.Equal("player-vitals", SimulatorJournalPolicy.ChannelOf(payload));
    }

    [Fact]
    public void EveryChannelPublishedByTheHubHasADecision()
    {
        // Регрессия: новый канал в хранилище не должен молча попадать в журнал
        // с потоком изменений несколько раз в секунду. Проверяем, что список
        // тихих каналов покрывает именно те, что действительно частые.
        var hub = new SimulatorDataChannelHub();
        var frequent = new[] { "player", "player-vitals", "player-conditions", "telemetry", "sim-time" };

        foreach (var key in frequent)
        {
            Assert.Contains(key, hub.Describe().Select(descriptor => descriptor.Key));
            Assert.False(SimulatorJournalPolicy.ShouldJournal("ChannelChanged", key),
                $"канал «{key}» меняется часто и не должен журналироваться");
        }
    }
}
