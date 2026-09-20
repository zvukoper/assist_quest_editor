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
        Assert.Single(store.Value.Connections);

        var duplicate = store.Connect(
            start.NodeId,
            start.Sockets.Single(x => x.Direction == SocketDirection.Output).SocketId,
            dialogue.NodeId,
            dialogue.Sockets.Single(x => x.Direction == SocketDirection.Input).SocketId);

        Assert.False(duplicate.Added);
        Assert.Single(store.Value.Connections);
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
    }
}
