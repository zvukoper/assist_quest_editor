using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class QuestGraphStoreTests
{
    [Fact]
    public void AddsNodeWithStableIdAndRegistrySockets()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var before = store.Value;

        var node = store.AddNode("SetStep", "Установить этап", 320, 220);

        Assert.False(string.IsNullOrWhiteSpace(node.NodeId));
        Assert.Equal("SetStep", node.NodeType);
        Assert.Equal("Установить этап", node.Title);
        // Нода должна быть найдена до Undo: после отката её в графе уже нет.
        Assert.Equal(node.NodeId, store.FindNode(node.NodeId)!.NodeId);
        AssertNoOverlaps(store.Value);
        Assert.True(store.Undo());
        Assert.Equal(before, store.Value);
        Assert.Contains(node.Sockets, socket =>
            socket.SocketId.EndsWith(".in", StringComparison.OrdinalIgnoreCase) &&
            socket.Direction == SocketDirection.Input);
        Assert.Contains(node.Sockets, socket =>
            socket.SocketId.EndsWith(".out", StringComparison.OrdinalIgnoreCase) &&
            socket.Direction == SocketDirection.Output);
    }

    [Fact]
    public void ReplaceAutomaticallyLayoutsObviouslyPoorImportedGraph()
    {
        var nodes = new[]
        {
            new QuestNode("a", "Phase", "A", 0, 0, QuestNodeCatalog.CreateSockets("Phase", "a", new Dictionary<string,string>())),
            new QuestNode("b", "Phase", "B", 0, 0, QuestNodeCatalog.CreateSockets("Phase", "b", new Dictionary<string,string>())),
            new QuestNode("c", "Phase", "C", 0, 0, QuestNodeCatalog.CreateSockets("Phase", "c", new Dictionary<string,string>()))
        };
        var graph = new QuestGraph("poor", "Плохой layout", nodes, Array.Empty<QuestConnection>());

        // Импортированный документ загружается через Replace(QuestDefinition):
        // Auto Layout Guard подключён именно там. Конструктор раскладку не
        // применяет (это простая замена значения), поэтому проверять guard
        // через new QuestGraphStore(graph) нельзя — тест ничего не измерял.
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        store.Replace(new QuestDefinition(
            graph.Id,
            graph.Name,
            "Импортированный документ.",
            graph,
            Array.Empty<string>()));

        Assert.False(QuestGraphLayout.IsObviouslyPoor(store.Value));
        Assert.NotEqual((0d, 0d), (store.FindNode("b")!.X, store.FindNode("b")!.Y));
    }

    [Fact]
    public void AddingNodeDoesNotRearrangeGoodExistingLayout()
    {
        var graph = new QuestGraph(
            "good",
            "Хороший layout",
            new[]
            {
                new QuestNode("a", "Phase", "A", 0, 0, QuestNodeCatalog.CreateSockets("Phase", "a", new Dictionary<string,string>())),
                new QuestNode("b", "Phase", "B", 500, 300, QuestNodeCatalog.CreateSockets("Phase", "b", new Dictionary<string,string>()))
            },
            Array.Empty<QuestConnection>());
        var store = new QuestGraphStore(graph);
        var before = store.Value.Nodes.ToDictionary(node => node.NodeId, node => (node.X, node.Y));

        store.AddNode("Phase", "C", 900, 500);

        Assert.Equal(before["a"], (store.FindNode("a")!.X, store.FindNode("a")!.Y));
        Assert.Equal(before["b"], (store.FindNode("b")!.X, store.FindNode("b")!.Y));
    }

    [Fact]
    public void ConnectsExistingOutputToExistingInputAndRejectsDuplicate()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var start = store.FindNode("start")!;
        var dialogue = store.FindNode("dialogue")!;

        var result = store.Connect(
            start.NodeId,
            start.Sockets.Single(x => x.Direction == SocketDirection.Output).SocketId,
            dialogue.NodeId,
            dialogue.Sockets.Single(x => x.Direction == SocketDirection.Input).SocketId);

        Assert.True(result.Added);
        Assert.Collection(
            store.Value.Connections,
            _ => { },
            _ => { },
            _ => { },
            _ => { });

        var duplicate = store.Connect(
            start.NodeId,
            start.Sockets.Single(x => x.Direction == SocketDirection.Output).SocketId,
            dialogue.NodeId,
            dialogue.Sockets.Single(x => x.Direction == SocketDirection.Input).SocketId);

        Assert.False(duplicate.Added);
        Assert.Collection(
            store.Value.Connections,
            _ => { },
            _ => { },
            _ => { },
            _ => { });
    }

    [Fact]
    public void RemovingNodeAlsoRemovesIncidentConnections()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        Assert.Collection(
            store.Value.Connections,
            _ => { },
            _ => { },
            _ => { });

        Assert.True(store.RemoveNode("condition"));

        Assert.Null(store.FindNode("condition"));
        Assert.Single(store.Value.Connections);
        Assert.All(store.Value.Connections, connection =>
        {
            Assert.NotEqual("condition", connection.FromNodeId);
            Assert.NotEqual("condition", connection.ToNodeId);
        });
    }

    [Fact]
    public void UpdatesNodeWithoutChangingItsIdOrSockets()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var original = store.FindNode("dialogue")!;

        var updated = store.UpdateNode(
            original.NodeId,
            title: "Новый диалог",
            x: 720,
            y: 420);

        Assert.NotNull(updated);
        Assert.Equal(original.NodeId, updated!.NodeId);
        Assert.Equal("Новый диалог", updated.Title);
        Assert.Equal(720, updated.X);
        Assert.Equal(420, updated.Y);
        Assert.Equal(original.Sockets, updated.Sockets);
        Assert.Equal(original.Parameters, updated.Parameters);
    }

    [Fact]
    public void UpdatesDynamicSocketsFromNodeParameters()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        var node = store.AddNode("Choice", "Выбор", 420, 120);
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
    public void ReplaceLoadsNewGraphAndClearsHistory()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        store.AddNode("Phase", "Изменение", 420, 120);

        var replacement = new QuestGraph(
            "loaded",
            "Загруженный квест",
            Array.Empty<QuestNode>(),
            Array.Empty<QuestConnection>());

        store.Replace(replacement);

        Assert.Equal(replacement, store.Value);
        Assert.False(store.CanUndo);
        Assert.False(store.CanRedo);
    }

    [Fact]
    public void DefinitionKeepsDocumentMetadataAcrossGraphEdits()
    {
        var graph = QuestGraphFactory.CreateStarter();
        var store = new QuestGraphStore(new QuestDefinition(
            graph.Id,
            graph.Name,
            "Описание квеста.",
            graph,
            new[] { "ruslan_start", "gosha_meat" }));

        Assert.Equal("Описание квеста.", store.Definition.Description);
        Assert.Equal(new[] { "ruslan_start", "gosha_meat" }, store.Definition.SceneIds);

        // Правка графа не должна терять метаданные документа: именно на этом
        // сценарии сохранение затирало Description и SceneIds.
        store.AddNode("Phase", "Новая фаза", 420, 120);

        Assert.Equal("Описание квеста.", store.Definition.Description);
        Assert.Equal(new[] { "ruslan_start", "gosha_meat" }, store.Definition.SceneIds);
        Assert.Equal(store.Value, store.Definition.Graph);
    }

    [Fact]
    public void ReplaceWithDefinitionRestoresMetadataFromLoadedDocument()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        var loaded = new QuestGraph("loaded", "Загруженный квест", Array.Empty<QuestNode>(), Array.Empty<QuestConnection>());
        store.Replace(new QuestDefinition(
            loaded.Id,
            loaded.Name,
            "Загруженное описание.",
            loaded,
            new[] { "scene_a" }));

        Assert.Equal("Загруженное описание.", store.Definition.Description);
        Assert.Equal(new[] { "scene_a" }, store.Definition.SceneIds);
        Assert.Equal(loaded, store.Value);
        Assert.False(store.CanUndo);
        Assert.False(store.CanRedo);
    }

    [Fact]
    public void ReplaceWithGraphOnlyClearsMetadataInsteadOfLeakingIt()
    {
        var graph = QuestGraphFactory.CreateStarter();
        var store = new QuestGraphStore(new QuestDefinition(
            graph.Id,
            graph.Name,
            "Описание первого документа.",
            graph,
            new[] { "scene_a" }));

        var replacement = new QuestGraph("other", "Другой квест", Array.Empty<QuestNode>(), Array.Empty<QuestConnection>());
        store.Replace(replacement);

        // Метаданные прежнего документа не должны «протечь» в новый.
        Assert.Equal(string.Empty, store.Definition.Description);
        Assert.Empty(store.Definition.SceneIds);
    }

    [Fact]
    public void DefinitionKeepsActivationAcrossGraphEdits()
    {
        var graph = QuestGraphFactory.CreateStarter();
        // Квест с Proximity-активацией: именно его точка рисуется на карте и
        // запускает Runtime. Раньше стор не хранил Activation, поэтому первое
        // же сохранение из Нодового редактора стирало блок из файла, и квест
        // исчезал с карты Симулятора.
        var activation = new QuestActivation(
            QuestStartMode.Proximity,
            "sdo:camping:0x3f8ed5e434c00000",
            150);

        var store = new QuestGraphStore(new QuestDefinition(
            graph.Id,
            graph.Name,
            "Описание квеста.",
            graph,
            Array.Empty<string>(),
            activation));

        Assert.Equal(activation, store.Definition.Activation);

        // Правка графа не должна терять политику запуска: без неё квест
        // становится невидимым на карте после сохранения.
        store.AddNode("Phase", "Новая фаза", 420, 120);

        Assert.Equal(activation, store.Definition.Activation);
    }

    [Fact]
    public void ReplaceWithDefinitionRestoresActivationFromLoadedDocument()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        var loaded = new QuestGraph("loaded", "Загруженный квест", Array.Empty<QuestNode>(), Array.Empty<QuestConnection>());
        var activation = new QuestActivation(QuestStartMode.Proximity, "city:chelyabinsk", 750);
        store.Replace(new QuestDefinition(
            loaded.Id,
            loaded.Name,
            "Загруженное описание.",
            loaded,
            new[] { "scene_a" },
            activation));

        Assert.Equal(activation, store.Definition.Activation);
    }

    [Fact]
    public void UpdateActivationPersistsAndReplaceWithGraphOnlyClearsIt()
    {
        var graph = QuestGraphFactory.CreateStarter();
        var store = new QuestGraphStore(new QuestDefinition(
            graph.Id,
            graph.Name,
            "Описание.",
            graph,
            Array.Empty<string>()));

        Assert.Null(store.Definition.Activation);

        store.UpdateActivation(new QuestActivation(QuestStartMode.Proximity, "sdo:5ka:0x1", 200));
        Assert.Equal("sdo:5ka:0x1", store.Definition.Activation!.WorldPointId);

        // Новый документ как замена графа не должен наследовать чужой Activation.
        store.Replace(new QuestGraph("other", "Другой", Array.Empty<QuestNode>(), Array.Empty<QuestConnection>()));
        Assert.Null(store.Definition.Activation);
    }

    [Fact]
    public void UndoAndRedoRestoreGraphSnapshots()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var original = store.Value;

        store.AddNode("Phase", "Новая фаза", 420, 120);
        var afterAdd = store.Value;

        Assert.True(store.CanUndo);
        Assert.False(store.CanRedo);

        Assert.True(store.Undo());
        Assert.Equal(original, store.Value);
        Assert.False(store.CanUndo);
        Assert.True(store.CanRedo);

        Assert.True(store.Redo());
        Assert.Equal(afterAdd, store.Value);
        Assert.True(store.CanUndo);
        Assert.False(store.CanRedo);
    }

    [Fact]
    public void ApplyLayoutSeparatesNodesThatOverlapWhenCreatedBlind()
    {
        // Агент может создать все ноды в одной точке: раскладка обязана развести их.
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        foreach (var node in store.Value.Nodes)
        {
            store.UpdateNode(node.NodeId, x: 0, y: 0);
        }

        var moved = store.ApplyLayout();

        Assert.Equal(store.Value.Nodes.Count, moved);
        AssertNoOverlaps(store.Value);
    }

    [Fact]
    public void ApplyLayoutOrdersLayersAlongConnections()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        store.ApplyLayout();

        var start = store.FindNode("start")!;
        var condition = store.FindNode("condition")!;
        var dialogue = store.FindNode("dialogue")!;
        var end = store.FindNode("end")!;

        // Слои идут слева направо вдоль связей, с одинаковым шагом.
        Assert.True(start.X < condition.X);
        Assert.True(condition.X < dialogue.X);
        Assert.True(dialogue.X < end.X);

        var step = condition.X - start.X;
        Assert.Equal(step, dialogue.X - condition.X, 3);
        Assert.Equal(step, end.X - dialogue.X, 3);
    }

    [Fact]
    public void ApplyLayoutIsIdempotent()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        store.ApplyLayout();
        var first = store.Value.Nodes.ToDictionary(node => node.NodeId, node => (node.X, node.Y));

        // Второй прогон не должен ничего двигать: иначе кнопка «Перестроить»
        // каждый раз помечала бы документ изменённым.
        Assert.Equal(0, store.ApplyLayout());

        foreach (var node in store.Value.Nodes)
        {
            Assert.Equal(first[node.NodeId].X, node.X, 3);
            Assert.Equal(first[node.NodeId].Y, node.Y, 3);
        }
    }

    [Fact]
    public void ApplyLayoutKeepsDisconnectedNodesApart()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var loose = store.AddNode("Phase", "Оторванная нода", 500, 500);

        store.ApplyLayout();

        AssertNoOverlaps(store.Value);
        Assert.Contains(store.Value.Nodes, node => node.NodeId == loose.NodeId);
    }

    [Fact]
    public void ApplyLayoutIsOneUndoStep()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());
        var before = store.Value.Nodes.ToDictionary(node => node.NodeId, node => (node.X, node.Y));

        store.ApplyLayout();

        // Вся раскладка откатывается одним Undo, а не по ноде.
        Assert.True(store.Undo());
        foreach (var node in store.Value.Nodes)
        {
            Assert.Equal(before[node.NodeId].X, node.X, 3);
            Assert.Equal(before[node.NodeId].Y, node.Y, 3);
        }
    }

    [Fact]
    public void ApplyLayoutHandlesCycleWithoutHanging()
    {
        var graph = new QuestGraph(
            "cyclic",
            "Циклический граф",
            new[]
            {
                Node("a", "Phase", 500, 500),
                Node("b", "Phase", 500, 500),
                Node("c", "Phase", 500, 500)
            },
            new[]
            {
                new QuestConnection("a", "a.out", "b", "b.in"),
                new QuestConnection("b", "b.out", "c", "c.in"),
                new QuestConnection("c", "c.out", "a", "a.in")
            });

        var store = new QuestGraphStore(graph);
        var moved = store.ApplyLayout();

        Assert.Equal(3, moved);
        AssertNoOverlaps(store.Value);
    }

    [Fact]
    public void ApplyLayoutHandlesSelfLoop()
    {
        var graph = new QuestGraph(
            "self",
            "Самосвязь",
            new[] { Node("a", "Phase", 500, 500) },
            new[] { new QuestConnection("a", "a.out", "a", "a.in") });

        var store = new QuestGraphStore(graph);

        Assert.Equal(1, store.ApplyLayout());
        AssertNoOverlaps(store.Value);
    }

    [Fact]
    public void ApplyLayoutHandlesEmptyGraph()
    {
        var store = new QuestGraphStore(new QuestGraph(
            "empty",
            "Пустой квест",
            Array.Empty<QuestNode>(),
            Array.Empty<QuestConnection>()));

        Assert.Equal(0, store.ApplyLayout());
    }

    private static QuestNode Node(string nodeId, string nodeType, double x, double y)
    {
        var parameters = QuestNodeCatalog.CreateDefaultParameters(nodeType);
        return new QuestNode(
            nodeId,
            nodeType,
            nodeType,
            x,
            y,
            QuestNodeCatalog.CreateSockets(nodeType, nodeId, parameters))
        {
            Parameters = parameters
        };
    }

    /// <summary>
    /// Ноды не должны пересекаться: проверяем прямоугольники с учётом реальной
    /// высоты ноды по количеству сокетов.
    /// </summary>
    private static void AssertNoOverlaps(QuestGraph graph)
    {
        for (var left = 0; left < graph.Nodes.Count; left++)
        {
            for (var right = left + 1; right < graph.Nodes.Count; right++)
            {
                var a = graph.Nodes[left];
                var b = graph.Nodes[right];

                var aRight = a.X + QuestGraphLayout.NodeWidth;
                var aBottom = a.Y + QuestGraphLayout.NodeHeight(a);
                var bRight = b.X + QuestGraphLayout.NodeWidth;
                var bBottom = b.Y + QuestGraphLayout.NodeHeight(b);

                var separated =
                    aRight <= b.X + 0.001 ||
                    bRight <= a.X + 0.001 ||
                    aBottom <= b.Y + 0.001 ||
                    bBottom <= a.Y + 0.001;

                Assert.True(
                    separated,
                    $"Ноды «{a.NodeId}» и «{b.NodeId}» пересекаются: " +
                    $"({a.X},{a.Y}) и ({b.X},{b.Y}).");
            }
        }
    }

    [Fact]
    public void NewChangeAfterUndoClearsRedoHistory()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        store.AddNode("Phase", "Первая фаза", 420, 120);
        Assert.True(store.Undo());
        Assert.True(store.CanRedo);

        var updated = store.UpdateNode("dialogue", title: "Изменённый диалог");

        Assert.NotNull(updated);
        Assert.False(store.CanRedo);
        Assert.True(store.CanUndo);
    }
    [Fact]
    public void StarterGraphPassesValidation()
    {
        var diagnostics = QuestGraphValidator.Validate(QuestGraphFactory.CreateStarter());

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ValidatorReportsStructuralAndConnectionProblems()
    {
        var start = new QuestNode(
            "start",
            "Start",
            "Начало",
            0,
            0,
            QuestNodeCatalog.CreateSockets("Start", "start"));

        var end = new QuestNode(
            "end",
            "End",
            "Конец",
            240,
            0,
            QuestNodeCatalog.CreateSockets("End", "end"));

        var graph = new QuestGraph(
            "",
            "",
            new[] { start, end },
            new[]
            {
                new QuestConnection("start", "missing", "end", "end.in"),
                new QuestConnection("end", "end.in", "start", "start.out"),
                new QuestConnection("start", "start.out", "end", "end.in"),
                new QuestConnection("start", "start.out", "end", "end.in")
            });

        var diagnostics = QuestGraphValidator.Validate(graph);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GRAPH001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "GRAPH002");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "CONNECTION003");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "CONNECTION005");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "CONNECTION006");
    }

    [Fact]
    public void ValidatorReportsUnreachableNode()
    {
        var start = new QuestNode(
            "start",
            "Start",
            "Начало",
            0,
            0,
            QuestNodeCatalog.CreateSockets("Start", "start"));

        var end = new QuestNode(
            "end",
            "End",
            "Конец",
            240,
            0,
            QuestNodeCatalog.CreateSockets("End", "end"));

        var orphan = new QuestNode(
            "orphan",
            "Phase",
            "Недостижимая фаза",
            120,
            180,
            QuestNodeCatalog.CreateSockets("Phase", "orphan"));

        var graph = new QuestGraph(
            "test",
            "Тест",
            new[] { start, end, orphan },
            new[]
            {
                new QuestConnection("start", "start.out", "end", "end.in")
            });

        var diagnostics = QuestGraphValidator.Validate(graph);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "FLOW004" &&
            diagnostic.NodeId == "orphan" &&
            diagnostic.Severity == GraphDiagnosticSeverity.Warning);
    }

}
