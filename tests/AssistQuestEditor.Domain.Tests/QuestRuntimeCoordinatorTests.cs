using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class QuestRuntimeCoordinatorTests
{
    [Fact]
    public void ProximityQuestStartsIndependentlyAndCompletionFreesRuntime()
    {
        var point = new WorldPoint("gosha", "Гоша", "Test", new WorldCoordinate(100, 0, 0));
        var main = Definition("main", point.Id, null, null, false, GraphWithEvent("main", "main.event"));
        var side = Definition("side", point.Id, "gosha", 350, false, GraphWithEvent("side", "side.event"));

        var hub = new SimulatorDataSourceAdapter(new[] { point }).Channels;
        var sceneRuntime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);
        using var coordinator = new QuestRuntimeCoordinator(hub, sceneRuntime, () => new[] { main, side }, "main");
        coordinator.SetSimulationRunning(true);

        hub.Get<PlayerState>("player").Set(
            new PlayerState(point.Position, 0, 0, false, true),
            "Test");

        Assert.Equal("main", coordinator.State.QuestId);
        Assert.Equal(QuestRuntimeStatus.Waiting, coordinator.State.Status);
        Assert.Equal("Event:main.event", coordinator.State.WaitingFor);

        coordinator.Stop();
        hub.Get<ReputationState>("reputation").Set(
            hub.Get<ReputationState>("reputation").Value.WithValue("gosha", 350),
            "Test");

        Assert.Equal("side", coordinator.State.QuestId);
        Assert.Equal(QuestRuntimeStatus.Waiting, coordinator.State.Status);

        hub.Events.Publish(new SimulatorEvent(
            "side.event",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string>()));

        Assert.Equal(QuestRuntimeStatus.Completed, coordinator.State.Status);
        Assert.Null(coordinator.ActiveGraph);
    }

    [Fact]
    public void RepeatableQuestRestartsOnNextPlayerActivation()
    {
        var point = new WorldPoint("gosha", "Гоша", "Test", new WorldCoordinate(100, 0, 0));
        var side = Definition("side", point.Id, "gosha", 350, true, GraphWithEnd("side"));

        var hub = new SimulatorDataSourceAdapter(new[] { point }).Channels;
        var sceneRuntime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);
        using var coordinator = new QuestRuntimeCoordinator(hub, sceneRuntime, () => new[] { side }, "side");
        coordinator.SetSimulationRunning(true);

        var starts = 0;
        coordinator.Published += (_, e) =>
        {
            if (e.EventType.Equals("RuntimeStarted", StringComparison.OrdinalIgnoreCase))
                starts++;
        };

        hub.Get<ReputationState>("reputation").Set(
            hub.Get<ReputationState>("reputation").Value.WithValue("gosha", 350),
            "Test");
        hub.Get<PlayerState>("player").Set(
            new PlayerState(point.Position, 0, 0, false, true),
            "Visit 1");

        Assert.Equal(QuestRuntimeStatus.Completed, coordinator.State.Status);
        Assert.Equal(1, starts);

        hub.Get<PlayerState>("player").Set(
            new PlayerState(new WorldCoordinate(0, 0, 0), 0, 0, false, true),
            "Leave");
        hub.Get<PlayerState>("player").Set(
            new PlayerState(point.Position, 0, 0, false, true),
            "Visit 2");

        Assert.Equal(QuestRuntimeStatus.Completed, coordinator.State.Status);
        Assert.Equal(2, starts);
    }

    [Fact]
    public void DisabledQuestAndStoppedSimulationDoNotReactToProximity()
    {
        var point = new WorldPoint("cache", "Тайник", "Test", new WorldCoordinate(100, 0, 0));
        var quest = Definition("quest", point.Id, null, null, false, GraphWithEnd("quest"));

        var hub = new SimulatorDataSourceAdapter(new[] { point }).Channels;
        var sceneRuntime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);
        using var coordinator = new QuestRuntimeCoordinator(hub, sceneRuntime, () => new[] { quest }, "quest");

        hub.Get<PlayerState>("player").Set(
            new PlayerState(point.Position, 0, 0, false, true),
            "Stopped simulation");

        Assert.Equal(QuestRuntimeStatus.Stopped, coordinator.State.Status);

        coordinator.SetQuestEnabled("quest", false);
        coordinator.SetSimulationRunning(true);
        hub.Get<PlayerState>("player").Set(
            new PlayerState(new WorldCoordinate(0, 0, 0), 0, 0, false, true),
            "Outside");
        hub.Get<PlayerState>("player").Set(
            new PlayerState(point.Position, 0, 0, false, true),
            "Disabled quest");

        Assert.Equal(QuestRuntimeStatus.Stopped, coordinator.State.Status);
    }

    /// <summary>
    /// Ускорение времени — часть РЕЖИМА, а не мира: и «Стоп», и «Сбросить»
    /// обязаны возвращать его к ×1. Без этого оранжевые часы с кратностью
    /// оставались после остановки, и мир выглядел всё ещё ускоренным.
    ///
    /// Ускорение НЕ входит ни в мир, ни в сохранения: проверяется и это — значение
    /// живёт только в памяти координатора. Если бы оно попало в
    /// <c>SimulationSaveState</c>, это сломало бы формат сохранений, и тест на
    /// сброс не поймал бы такой дефект.
    /// </summary>
    [Fact]
    public void SimulationSpeedResetsOnStopAndResetAndIsNotPartOfSaveState()
    {
        var point = new WorldPoint("gosha", "Гоша", "Test", new WorldCoordinate(100, 0, 0));
        var quest = Definition("main", point.Id, null, null, false, GraphWithEnd("main"));

        var hub = new SimulatorDataSourceAdapter(new[] { point }).Channels;
        var sceneRuntime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);
        using var coordinator = new QuestRuntimeCoordinator(hub, sceneRuntime, () => new[] { quest }, "main");

        Assert.Equal(1d, coordinator.SimulationSpeed);

        coordinator.SetSimulationRunning(true);
        coordinator.SetSimulationSpeed(20d);
        Assert.Equal(20d, coordinator.SimulationSpeed);

        // Стоп — ускорение сбрасывается, но состояние часов сохраняется как есть:
        // сброс касается только кратности.
        coordinator.SetSimulationRunning(false);
        Assert.Equal(1d, coordinator.SimulationSpeed);

        coordinator.SetSimulationRunning(true);
        coordinator.SetSimulationSpeed(60d);
        coordinator.Reset();
        Assert.Equal(1d, coordinator.SimulationSpeed);

        // Кратность не сериализуется: в состоянии сохранения нет ни одного поля
        // с подстрокой «Speed», кроме скорости транспорта игрока (SpeedKmh).
        var savedFields = typeof(SimulationSaveState).GetProperties().Select(item => item.Name).ToArray();
        Assert.DoesNotContain(savedFields, name =>
            name.Contains("Speed", StringComparison.OrdinalIgnoreCase));
    }

    private static QuestDefinition Definition(
        string id,
        string pointId,
        string? npcId,
        int? reputation,
        bool repeatable,
        QuestGraph graph) =>
        new(
            id,
            id,
            string.Empty,
            graph,
            Array.Empty<string>(),
            new QuestActivation(
                QuestStartMode.Proximity,
                pointId,
                20,
                npcId,
                reputation,
                repeatable));

    private static QuestGraph GraphWithEvent(string id, string eventType)
    {
        var start = Node("start", "Start");
        var wait = Node("wait", "WaitForEvent", ("eventType", eventType));
        var end = Node("end", "End");
        return Graph(
            id,
            new[] { start, wait, end },
            new[]
            {
                Connection("start", "out", "wait", "in"),
                Connection("wait", "out", "end", "in")
            });
    }

    private static QuestGraph GraphWithEnd(string id)
    {
        var start = Node("start", "Start");
        var end = Node("end", "End");
        return Graph(id, new[] { start, end }, new[] { Connection("start", "out", "end", "in") });
    }

    private static QuestGraph Graph(string id, IReadOnlyList<QuestNode> nodes, IReadOnlyList<QuestConnection> connections) =>
        new(id, id, nodes, connections);

    private static QuestNode Node(string id, string type, params (string key, string value)[] parameters)
    {
        var sockets = type switch
        {
            "Start" => new[] { new SocketDefinition(id + ".out", "Next", SocketDirection.Output) },
            "End" => new[] { new SocketDefinition(id + ".in", "Input", SocketDirection.Input) },
            _ => new[]
            {
                new SocketDefinition(id + ".in", "Input", SocketDirection.Input),
                new SocketDefinition(id + ".out", "Next", SocketDirection.Output)
            }
        };

        return new QuestNode(id, type, id, 0, 0, sockets)
        {
            Parameters = parameters.ToDictionary(pair => pair.key, pair => pair.value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static QuestConnection Connection(string fromNode, string fromSocket, string toNode, string toSocket) =>
        new(fromNode, fromNode + "." + fromSocket, toNode, toNode + "." + toSocket);
}
