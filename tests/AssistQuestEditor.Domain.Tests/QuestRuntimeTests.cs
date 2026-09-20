using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class QuestRuntimeTests
{
    [Fact]
    public void RuntimeTraversesEffectsAndCompletesSimpleQuest()
    {
        var start = Node("start", "Start");
        var setStep = Node("step", "SetStep", ("step", "done"));
        var give = Node("give", "GiveItem", ("itemId", "meat"), ("count", "2"));
        var end = Node("end", "End");

        var graph = Graph(
            new[] { start, setStep, give, end },
            new[]
            {
                C("start", start, "out", setStep, "in"),
                C("step", setStep, "out", give, "in"),
                C("give", give, "out", end, "in")
            });

        var hub = new SimulatorDataChannelHub(Array.Empty<WorldPoint>());
        var store = new QuestGraphStore(graph);
        var runtime = new QuestRuntime(store, hub);

        runtime.Start();

        Assert.Equal(QuestRuntimeStatus.Completed, runtime.State.Status);
        Assert.Equal("done", hub.Get<QuestStatusesState>("quest-statuses").Value.Quests.Single().Step);
        Assert.Equal(2, hub.Get<InventoryState>("inventory").Value.Items["meat"]);
    }

    [Fact]
    public void ConditionWaitsUntilPlayerReachesWorldPoint()
    {
        var point = new WorldPoint("target", "Цель", "Тест", new WorldCoordinate(100, 0, 0));
        var hub = new SimulatorDataSourceAdapter(new[] { point }).Channels;
        var start = Node("start", "Start");
        var interaction = Node("interaction", "Interaction",
            ("worldPointId", "target"), ("triggerRadius", "10"));
        var end = Node("end", "End");

        var graph = Graph(
            new[] { start, interaction, end },
            new[]
            {
                C("start", start, "out", interaction, "in"),
                C("interaction", interaction, "out", end, "in")
            });

        var store = new QuestGraphStore(graph);
        var runtime = new QuestRuntime(store, hub);

        runtime.Start();

        Assert.Equal(QuestRuntimeStatus.Waiting, runtime.State.Status);
        Assert.Equal("Interaction", runtime.State.WaitingFor);

        hub.Get<PlayerState>("player").Set(
            new PlayerState(new WorldCoordinate(100, 0, 0), 0, 0, false, true),
            "Тест");

        runtime.Tick();

        Assert.Equal(QuestRuntimeStatus.Completed, runtime.State.Status);
    }

    [Fact]
    public void WaitForEventResumesOnMatchingSimulatorEvent()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var start = Node("start", "Start");
        var wait = Node("wait", "WaitForEvent", ("eventType", "HornPressed"));
        var end = Node("end", "End");

        var graph = Graph(
            new[] { start, wait, end },
            new[]
            {
                C("start", start, "out", wait, "in"),
                C("wait", wait, "out", end, "in")
            });

        var store = new QuestGraphStore(graph);
        var runtime = new QuestRuntime(store, hub);

        runtime.Start();
        Assert.Equal("Event:HornPressed", runtime.State.WaitingFor);

        hub.Events.Publish(new SimulatorEvent(
            "HornPressed",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string>()));

        Assert.Equal(QuestRuntimeStatus.Completed, runtime.State.Status);
    }

    private static QuestNode Node(string id, string type, params (string Key, string Value)[] parameters)
    {
        var map = parameters.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return new QuestNode(id, type, type, 0, 0, QuestNodeCatalog.CreateSockets(type, id, map))
        {
            Parameters = map
        };
    }

    private static QuestGraph Graph(IReadOnlyList<QuestNode> nodes, IReadOnlyList<QuestConnection> connections) =>
        new("runtime-test", "Runtime Test", nodes, connections);

    private static QuestConnection C(string id, QuestNode from, string fromSocket, QuestNode to, string toSocket) =>
        new(from.NodeId, fromSocket == "out" ? $"{from.NodeId}.out" : fromSocket, to.NodeId, toSocket == "in" ? $"{to.NodeId}.in" : toSocket);
}
