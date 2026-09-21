using System.Text.Json;
using System.Text.Json.Serialization;
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
        var questStatus = hub.Get<QuestStatusesState>("quest-statuses").Value.Quests
            .Single(x => x.QuestId == graph.Id);
        Assert.Equal("done", questStatus.Step);
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

        hub.Get<PlayerState>("player").Set(
            new PlayerState(new WorldCoordinate(-1000, 0, -1000), 0, 0, false, true),
            "Тест начальной позиции");

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
    public void WaitTimerResumesThroughOutputAndDoesNotReenterWait()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var start = Node("start", "Start");
        var wait = Node("wait", "Wait", ("seconds", "0.1"));
        var setStep = Node("step", "SetStep", ("step", "after_wait"));
        var end = Node("end", "End");

        var graph = Graph(
            new[] { start, wait, setStep, end },
            new[]
            {
                C("start", start, "out", wait, "in"),
                C("wait", wait, "out", setStep, "in"),
                C("step", setStep, "out", end, "in")
            });

        var runtime = new QuestRuntime(new QuestGraphStore(graph), hub);

        runtime.Start();

        Assert.Equal(QuestRuntimeStatus.Waiting, runtime.State.Status);
        Assert.Equal("Time", runtime.State.WaitingFor);
        Assert.Equal("wait", runtime.State.CurrentNodeId);

        Thread.Sleep(250);
        runtime.Tick();

        Assert.Equal(QuestRuntimeStatus.Completed, runtime.State.Status);
        Assert.Equal("end", runtime.State.CurrentNodeId);
        Assert.Null(runtime.State.WaitingFor);

        var questStatus = hub.Get<QuestStatusesState>("quest-statuses").Value.Quests
            .Single(x => x.QuestId == graph.Id);
        Assert.Equal("after_wait", questStatus.Step);
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

    [Fact]
    public void Scenario01_Graph()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        Assert.Empty(QuestGraphValidator.Validate(store.Value));
        var node = store.AddNode("Phase", "Сценарная фаза", 420, 120);
        Assert.False(string.IsNullOrWhiteSpace(node.NodeId));
        Assert.Equal(node.NodeId, store.FindNode(node.NodeId)!.NodeId);
        Assert.True(store.Undo());
        Assert.True(store.Redo());
    }

    [Fact]
    public void Scenario02_Parameters()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var node = store.AddNode("Choice", "Сценарный выбор", 420, 120);

        Assert.Collection(
            node.Sockets.Where(socket => socket.Direction == SocketDirection.Output),
            _ => { },
            _ => { });

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["outputCount"] = "4"
        };

        var updated = store.UpdateNode(node.NodeId, parameters: parameters);

        Assert.NotNull(updated);
        Assert.Collection(
            updated!.Sockets.Where(socket => socket.Direction == SocketDirection.Output),
            _ => { },
            _ => { },
            _ => { },
            _ => { });
        Assert.Contains(updated.Sockets, socket => socket.SocketId == $"{node.NodeId}.choice4");
    }

    [Fact]
    public void Scenario03_SimpleRuntime()
    {
        RuntimeTraversesEffectsAndCompletesSimpleQuest();
    }

    [Fact]
    public void Scenario04_Interaction()
    {
        ConditionWaitsUntilPlayerReachesWorldPoint();
    }

    [Fact]
    public void Scenario05_Event()
    {
        WaitForEventResumesOnMatchingSimulatorEvent();
    }

    [Fact]
    public void Scenario06_Choice()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var start = Node("start", "Start");
        var choice = Node("choice", "Choice");
        var left = Node("left", "End");
        var right = Node("right", "End");

        var graph = Graph(
            new[] { start, choice, left, right },
            new[]
            {
                C("start", start, "out", choice, "in"),
                new QuestConnection("choice", "choice.choice1", "left", "left.in"),
                new QuestConnection("choice", "choice.choice2", "right", "right.in")
            });

        var runtime = new QuestRuntime(new QuestGraphStore(graph), hub);
        runtime.Start();

        Assert.Equal(QuestRuntimeStatus.Waiting, runtime.State.Status);
        Assert.Equal("Choice", runtime.State.WaitingFor);

        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialog);
        Assert.StartsWith("runtime-test:choice:", dialog!.RequestId);
        Assert.Equal("Квест", dialog.Speaker);
        Assert.Equal(2, dialog.Options.Count);
        Assert.Equal("Выбор 1", dialog.Options[0].Text);
        Assert.Equal("Выбор 2", dialog.Options[1].Text);

        hub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string> { ["index"] = "2" }));

        Assert.Equal(QuestRuntimeStatus.Completed, runtime.State.Status);
        Assert.Equal("right", runtime.State.CurrentNodeId);
        Assert.Null(hub.Get<InterfaceState>("interfaces").Value.ActiveDialog);
    }

    [Fact]
    public void ChoiceInterfaceSupportsBothBranchesAcrossRuns()
    {
        var graphStart = Node("start", "Start");
        var choice = Node("choice", "Choice");
        var left = Node("left", "End");
        var right = Node("right", "End");

        var graph = Graph(
            new[] { graphStart, choice, left, right },
            new[]
            {
                C("start", graphStart, "out", choice, "in"),
                new QuestConnection("choice", "choice.choice1", "left", "left.in"),
                new QuestConnection("choice", "choice.choice2", "right", "right.in")
            });

        var firstHub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var firstRuntime = new QuestRuntime(new QuestGraphStore(graph), firstHub);
        firstRuntime.Start();

        var firstDialog = firstHub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(firstDialog);

        firstHub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>
            {
                ["requestId"] = firstDialog!.RequestId,
                ["index"] = "1"
            }));

        Assert.Equal(QuestRuntimeStatus.Completed, firstRuntime.State.Status);
        Assert.Equal("left", firstRuntime.State.CurrentNodeId);
        Assert.Null(firstHub.Get<InterfaceState>("interfaces").Value.ActiveDialog);

        var secondHub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var secondRuntime = new QuestRuntime(new QuestGraphStore(graph), secondHub);
        secondRuntime.Start();

        var secondDialog = secondHub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(secondDialog);
        Assert.NotEqual(firstDialog.RequestId, secondDialog!.RequestId);

        secondHub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>
            {
                ["requestId"] = secondDialog.RequestId,
                ["index"] = "2"
            }));

        Assert.Equal(QuestRuntimeStatus.Completed, secondRuntime.State.Status);
        Assert.Equal("right", secondRuntime.State.CurrentNodeId);
        Assert.Null(secondHub.Get<InterfaceState>("interfaces").Value.ActiveDialog);
    }

    [Fact]
    public void SceneRuntimeUsesCanonicalChoiceResource()
    {
        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var sceneRuntime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);

        Assert.True(sceneRuntime.Start("ruslan_start"));
        Assert.Equal(SceneRuntimeStatus.Waiting, sceneRuntime.State.Status);
        Assert.Equal("Choice", sceneRuntime.State.WaitingFor);

        var dialog = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialog);
        Assert.Equal("Руслан", dialog!.Speaker);
        Assert.Equal("Нужно найти особое мясо. Возьмёшься?", dialog.Text);
        Assert.Equal(
            new[] { "Да, берусь.", "Нет, сейчас не могу." },
            dialog.Options.Select(option => option.Text).ToArray());

        hub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>
            {
                ["requestId"] = dialog.RequestId,
                ["index"] = "1",
                ["optionId"] = "ruslan.offer.accept"
            }));

        Assert.Equal(SceneRuntimeStatus.Completed, sceneRuntime.State.Status);
        Assert.Equal("ruslan.offer.accept", sceneRuntime.State.LastChoiceId);
        Assert.Equal("accept", sceneRuntime.State.CurrentNodeId);
        Assert.Null(hub.Get<InterfaceState>("interfaces").Value.ActiveDialog);
    }

    [Fact]
    public void DialogueSceneOrchestratesSceneRuntimeAndStoresChoice()
    {
        var start = Node("start", "Start");
        var dialogue = Node("dialogue", "DialogueScene", ("sceneId", "ruslan_start"));
        var end = Node("end", "End");

        var graph = Graph(
            new[] { start, dialogue, end },
            new[]
            {
                C("start", start, "out", dialogue, "in"),
                C("dialogue", dialogue, "out", end, "in")
            });

        var hub = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>()).Channels;
        var sceneRuntime = new SceneRuntime(SceneCatalogFactory.CreateStarter(), hub);
        var questRuntime = new QuestRuntime(
            new QuestGraphStore(graph),
            hub,
            sceneRuntime);

        questRuntime.Start();

        Assert.Equal(QuestRuntimeStatus.Waiting, questRuntime.State.Status);
        Assert.Equal("Scene", questRuntime.State.WaitingFor);

        var dialogRequest = hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        Assert.NotNull(dialogRequest);
        hub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>
            {
                ["requestId"] = dialogRequest!.RequestId,
                ["index"] = "2",
                ["optionId"] = "ruslan.offer.decline"
            }));

        Assert.Equal(QuestRuntimeStatus.Completed, questRuntime.State.Status);
        Assert.Equal("end", questRuntime.State.CurrentNodeId);
        Assert.Equal(
            "ruslan.offer.decline",
            hub.Get<RuntimeStatesState>("states").Value.Variables["scene.ruslan_start.lastChoiceId"]);
    }

    [Fact]
    public void VariableEqualsConditionReadsRuntimeState()
    {
        var hub = new SimulatorDataAdapter(Array.Empty<WorldPoint>()).Channels;
        var start = Node("start", "Start");
        var condition = Node(
            "condition",
            "Condition",
            ("operator", "VariableEquals"),
            ("left", "scene.ruslan_start.lastChoiceId"),
            ("comparison", "=="),
            ("right", "ruslan.offer.accept"));
        var yes = Node("yes", "End");
        var no = Node("no", "End");

        var states = hub.Get<RuntimeStatesState>("states").Value;
        hub.Get<RuntimeStatesState>("states").Set(
            states with
            {
                Variables = new Dictionary<string, string>
                {
                    ["scene.ruslan_start.lastChoiceId"] = "ruslan.offer.accept"
                }
            },
            "Тест");

        var graph = Graph(
            new[] { start, condition, yes, no },
            new[]
            {
                C("start", start, "out", condition, "in"),
                C("condition", condition, "true", yes, "in"),
                C("condition", condition, "false", no, "in")
            });

        var runtime = new QuestRuntime(new QuestGraphStore(graph), hub);
        runtime.Start();

        Assert.Equal(QuestRuntimeStatus.Completed, runtime.State.Status);
        Assert.Equal("yes", runtime.State.CurrentNodeId);
    }

    [Fact]
    public void Scenario07_SaveLoad()
    {
        var graph = QuestGraphFactory.CreateStarter();
        var choice = new QuestNode(
            "choice",
            "Choice",
            "Выбор",
            720,
            160,
            QuestNodeCatalog.CreateSockets(
                "Choice",
                "choice",
                new Dictionary<string, string> { ["outputCount"] = "3" }))
        {
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["outputCount"] = "3"
            }
        };

        graph = graph with
        {
            Nodes = graph.Nodes.Append(choice).ToArray(),
            Connections = graph.Connections.Append(
                new QuestConnection("choice", "choice.choice1", "end", "end.in")).ToArray()
        };

        var document = new QuestDefinitionDocument(
            1,
            new QuestDefinition(
                graph.Id,
                graph.Name,
                "Сценарный тест",
                graph,
                new[] { "ruslan_start" }));

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(document, options);
        var restored = JsonSerializer.Deserialize<QuestDefinitionDocument>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(1, restored!.SchemaVersion);
        Assert.Equal(document.Definition.Id, restored.Definition.Id);
        Assert.Equal(document.Definition.Graph.Id, restored.Definition.Graph.Id);
        Assert.Equal(document.Definition.Graph.Name, restored.Definition.Graph.Name);
        Assert.Equal("3", restored.Definition.Graph.Nodes.Single(x => x.NodeId == "choice").Parameters["outputCount"]);
        Assert.Contains(restored.Definition.Graph.Connections, x =>
            x.FromNodeId == "choice" && x.FromSocketId == "choice.choice1" && x.ToNodeId == "end");
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
