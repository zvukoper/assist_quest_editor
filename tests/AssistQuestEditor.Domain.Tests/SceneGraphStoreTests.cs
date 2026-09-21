using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class SceneGraphStoreTests
{
    [Fact]
    public void StarterSceneHasCanonicalFlow()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var errors = SceneGraphValidator.Validate(scene)
            .Where(item => item.Severity == GraphDiagnosticSeverity.Error)
            .ToArray();

        Assert.Equal("ruslan_start", scene.Id);
        Assert.Equal(5, scene.Graph.Nodes.Count);
        Assert.Equal(4, scene.Graph.Connections.Count);
        Assert.Empty(errors);
    }

    [Fact]
    public void AddsNodeWithRegistrySockets()
    {
        var store = new SceneGraphStore(SceneCatalogFactory.CreateStarter().Scenes.Single());
        var node = store.AddNode("Dialogue", "Новый диалог", 320, 220);

        Assert.StartsWith("scene-node-", node.NodeId);
        Assert.Contains(node.Sockets, socket => socket.Direction == SocketDirection.Input);
        Assert.Contains(node.Sockets, socket => socket.Direction == SocketDirection.Output);
        Assert.Contains(node.Parameters, pair => pair.Key.Equals("dialogueId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConnectRejectsSecondConnectionToSameInput()
    {
        var store = new SceneGraphStore(SceneCatalogFactory.CreateStarter().Scenes.Single());
        var output = store.FindNode("start")!.Sockets.Single(socket => socket.Direction == SocketDirection.Output);
        var input = store.FindNode("dialogue")!.Sockets.Single(socket => socket.Direction == SocketDirection.Input);

        var duplicate = store.Connect("start", output.SocketId, "dialogue", input.SocketId);

        Assert.False(duplicate.Added);
        Assert.Contains("уже подключён", duplicate.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveNodeAlsoRemovesConnections()
    {
        var store = new SceneGraphStore(SceneCatalogFactory.CreateStarter().Scenes.Single());

        Assert.True(store.RemoveNode("choice"));
        Assert.DoesNotContain(store.Value.Graph.Connections, item =>
            item.FromNodeId.Equals("choice", StringComparison.OrdinalIgnoreCase) ||
            item.ToNodeId.Equals("choice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LayoutSeparatesBlindlyCreatedNodesAndIsOneUndoStep()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var graph = scene.Graph with
        {
            Nodes = scene.Graph.Nodes.Select(node => node with { X = 0, Y = 0 }).ToArray()
        };
        var store = new SceneGraphStore(scene with { Graph = graph });

        Assert.Equal(5, store.ApplyLayout());
        AssertNoOverlaps(store.Value.Graph);

        Assert.True(store.Undo());
        Assert.All(store.Value.Graph.Nodes, node =>
        {
            Assert.Equal(0, node.X);
            Assert.Equal(0, node.Y);
        });
    }

    [Fact]
    public void UndoRedoRestoresSceneDefinition()
    {
        var store = new SceneGraphStore(SceneCatalogFactory.CreateStarter().Scenes.Single());
        var original = store.Value;

        store.AddNode("SceneWait", "Пауза", 300, 300);
        var changed = store.Value;

        Assert.True(store.Undo());
        Assert.Equal(original, store.Value);
        Assert.True(store.Redo());
        Assert.Equal(changed, store.Value);
    }

    [Fact]
    public void LoadedChoiceSocketsSurviveParameterEdit()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);
        var originalSockets = store.FindNode("choice")!.Sockets;

        var updated = store.UpdateNode(
            "choice",
            parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["choiceId"] = "ruslan.offer"
            });

        Assert.NotNull(updated);
        Assert.Equal(originalSockets, updated!.Sockets);
        Assert.DoesNotContain(
            SceneGraphValidator.Validate(store.Value),
            item => item.Severity == GraphDiagnosticSeverity.Error);
    }

    private static void AssertNoOverlaps(SceneGraph graph)
    {
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var a = graph.Nodes[i];
            var aRight = a.X + SceneGraphLayout.NodeWidth;
            var aBottom = a.Y + SceneGraphLayout.NodeHeight(a);

            for (var j = i + 1; j < graph.Nodes.Count; j++)
            {
                var b = graph.Nodes[j];
                var bRight = b.X + SceneGraphLayout.NodeWidth;
                var bBottom = b.Y + SceneGraphLayout.NodeHeight(b);

                var overlaps = a.X < bRight &&
                    aRight > b.X &&
                    a.Y < bBottom &&
                    aBottom > b.Y;

                Assert.False(overlaps, "Overlap: " + a.NodeId + " and " + b.NodeId);
            }
        }
    }
}
