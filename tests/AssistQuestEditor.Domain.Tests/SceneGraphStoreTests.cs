using AssistQuestEditor.Domain;
using System.Linq;
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
        var before = store.Value;
        var node = store.AddNode("Dialogue", "Новый диалог", 320, 220);

        Assert.StartsWith("scene-node-", node.NodeId);
        AssertNoOverlaps(store.Value.Graph);
        Assert.True(store.Undo());
        Assert.Equal(before, store.Value);
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


    [Fact]
    public void CreateDialogueForNodeCreatesAndLinksStableResource()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var graph = scene.Graph with
        {
            Nodes = scene.Graph.Nodes.Select(node =>
                node.NodeId == "dialogue"
                    ? node with
                    {
                        Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    }
                    : node).ToArray()
        };
        var store = new SceneGraphStore(scene with { Graph = graph });

        var dialogue = store.CreateDialogueForNode("dialogue");

        Assert.StartsWith("dialogue.", dialogue.Id);
        Assert.Equal(dialogue.Id, store.FindNode("dialogue")!.Parameters["dialogueId"]);
        Assert.Equal(dialogue, store.FindDialogue(dialogue.Id));
    }

    [Fact]
    public void AddDialogueCreatesUnlinkedResourceAndUndoRestores()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);
        var before = store.Value.Dialogues.Count;

        var dialogue = store.AddDialogue();

        Assert.Equal(before + 1, store.Value.Dialogues.Count);
        Assert.StartsWith("dialogue.", dialogue.Id);
        Assert.Null(store.FindNode("dialogue-empty"));

        Assert.True(store.Undo());
        Assert.Equal(before, store.Value.Dialogues.Count);
    }

    [Fact]
    public void ReferencedDialogueCannotBeRemoved()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);

        Assert.False(store.RemoveDialogue("ruslan.greeting", out var error));
        Assert.Contains("используется", error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(store.FindDialogue("ruslan.greeting"));
    }

    [Fact]
    public void UnreferencedDialogueCanBeRemoved()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);
        var dialogue = store.AddDialogue();

        Assert.True(store.RemoveDialogue(dialogue.Id, out var error), error);
        Assert.Null(store.FindDialogue(dialogue.Id));
        Assert.True(store.Undo());
        Assert.NotNull(store.FindDialogue(dialogue.Id));
    }

    [Fact]
    public void UpdateDialogueIsUndoable()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);
        var original = store.FindDialogue("ruslan.greeting")!;

        Assert.True(store.UpdateDialogue("ruslan.greeting", "Новый Руслан", "Новый текст."));

        var updated = store.FindDialogue("ruslan.greeting")!;
        Assert.Equal("Новый Руслан", updated.Speaker);
        Assert.Equal("Новый текст.", updated.Text);

        Assert.True(store.Undo());
        Assert.Equal(original, store.FindDialogue("ruslan.greeting"));
    }

    [Fact]
    public void CreateChoiceForNodeUsesExistingOutputSockets()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var graph = scene.Graph with
        {
            Nodes = scene.Graph.Nodes.Select(node =>
                node.NodeId == "choice"
                    ? node with
                    {
                        Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    }
                    : node).ToArray()
        };
        var store = new SceneGraphStore(scene with { Graph = graph });

        var choice = store.CreateChoiceForNode("choice");
        var node = store.FindNode("choice")!;

        Assert.StartsWith("choice.", choice.Id);
        Assert.Equal(2, choice.Options.Count);
        Assert.All(choice.Options, option =>
            Assert.Contains(node.Sockets, socket =>
                socket.Direction == SocketDirection.Output &&
                socket.SocketId == option.OutputSocketId));
        Assert.Equal(choice.Id, node.Parameters["choiceId"]);
    }

    [Fact]
    public void ChoiceOptionAddCreatesStableSocketAndUndoRestores()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);
        var before = store.FindChoice("ruslan.offer")!.Options.Count;

        var option = store.AddChoiceOption("choice", "ruslan.offer");
        var updated = store.FindChoice("ruslan.offer")!;

        Assert.Equal(before + 1, updated.Options.Count);
        Assert.StartsWith("ruslan.offer.option.", option.Id);
        Assert.StartsWith("choice.option.", option.OutputSocketId);
        Assert.Contains(store.FindNode("choice")!.Sockets, socket =>
            socket.SocketId == option.OutputSocketId &&
            socket.Direction == SocketDirection.Output);

        Assert.True(store.Undo());
        Assert.Equal(before, store.FindChoice("ruslan.offer")!.Options.Count);
    }

    [Fact]
    public void ConnectedChoiceOptionCannotBeRemoved()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);

        var removed = store.RemoveChoiceOption(
            "choice",
            "ruslan.offer",
            "ruslan.offer.accept",
            out var error);

        Assert.False(removed);
        Assert.Contains("подключён", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnconnectedChoiceOptionCanBeRemovedWithoutBreakingOtherSockets()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);

        // Decline-ветка подключена в starter-сцене (choice.decline -> decline.in),
        // а удалять подключённый Output запрещено контрактом. Поэтому сначала
        // разрываем связь, и только потом проверяем удаление и сохранность
        // остальных sockets.
        var declineConnection = store.Value.Graph.Connections.Single(connection =>
            connection.FromNodeId.Equals("choice", StringComparison.OrdinalIgnoreCase) &&
            connection.FromSocketId.Equals("choice.decline", StringComparison.OrdinalIgnoreCase));
        Assert.True(store.Disconnect(declineConnection));

        Assert.True(store.RemoveChoiceOption(
            "choice",
            "ruslan.offer",
            "ruslan.offer.decline",
            out var error), error);

        Assert.DoesNotContain(
            store.FindChoice("ruslan.offer")!.Options,
            option => option.Id.Equals("ruslan.offer.decline", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            store.FindNode("choice")!.Sockets,
            socket => socket.SocketId.Equals("choice.decline", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            store.FindNode("choice")!.Sockets,
            socket => socket.SocketId.Equals("choice.accept", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdatingChoicePreservesOptionAndSocketIds()
    {
        var scene = SceneCatalogFactory.CreateStarter().Scenes.Single();
        var store = new SceneGraphStore(scene);
        var before = store.FindChoice("ruslan.offer")!;

        var optionTexts = before.Options.ToDictionary(
            option => option.Id,
            option => option.Text + " ✱",
            StringComparer.OrdinalIgnoreCase);

        Assert.True(store.UpdateChoice(
            before.Id,
            "Новое название",
            "Новый Speaker",
            "Новый вопрос",
            optionTexts,
            out var error), error);

        var after = store.FindChoice(before.Id)!;
        Assert.Equal(before.Options.Select(option => option.Id), after.Options.Select(option => option.Id));
        Assert.Equal(before.Options.Select(option => option.OutputSocketId), after.Options.Select(option => option.OutputSocketId));
        Assert.Equal("Новый вопрос", after.Text);
        Assert.Contains(
            store.FindNode("choice")!.Sockets,
            socket => socket.SocketId == "choice.accept" && socket.Name.Contains("Да, берусь.", StringComparison.Ordinal));
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
