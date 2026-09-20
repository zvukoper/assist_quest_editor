using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class QuestGraphStoreTests
{
    [Fact]
    public void AddsNodeWithStableIdAndRegistrySockets()
    {
        var store = new QuestGraphStore(QuestGraphFactory.CreateStarter());

        var node = store.AddNode("SetStep", "Установить этап", 320, 220);

        Assert.False(string.IsNullOrWhiteSpace(node.NodeId));
        Assert.Equal("SetStep", node.NodeType);
        Assert.Equal("Установить этап", node.Title);
        Assert.Equal(320, node.X);
        Assert.Equal(220, node.Y);
        Assert.Contains(node.Sockets, socket =>
            socket.SocketId.EndsWith(".in", StringComparison.OrdinalIgnoreCase) &&
            socket.Direction == SocketDirection.Input);
        Assert.Contains(node.Sockets, socket =>
            socket.SocketId.EndsWith(".out", StringComparison.OrdinalIgnoreCase) &&
            socket.Direction == SocketDirection.Output);
        Assert.Equal(node.NodeId, store.FindNode(node.NodeId)!.NodeId);
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
        Assert.Equal(2, node.Sockets.Count(socket => socket.Direction == SocketDirection.Output));

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["outputCount"] = "4"
        };

        var updated = store.UpdateNode(node.NodeId, parameters: parameters);

        Assert.NotNull(updated);
        Assert.Equal(4, updated!.Sockets.Count(socket => socket.Direction == SocketDirection.Output));
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
