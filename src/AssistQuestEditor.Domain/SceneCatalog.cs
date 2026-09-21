namespace AssistQuestEditor.Domain;

public interface ISceneCatalog
{
    bool TryGetScene(string sceneId, out SceneDefinition scene);
}

public sealed class SceneCatalog : ISceneCatalog
{
    private readonly IReadOnlyDictionary<string, SceneDefinition> _scenes;

    public SceneCatalog(IEnumerable<SceneDefinition> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);

        _scenes = scenes
            .Where(scene => !string.IsNullOrWhiteSpace(scene.Id))
            .ToDictionary(scene => scene.Id, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetScene(string sceneId, out SceneDefinition scene) =>
        _scenes.TryGetValue(sceneId, out scene!);
}

public static class SceneCatalogFactory
{
    public static SceneCatalog CreateStarter() =>
        new(new[] { CreateRuslanStart() });

    private static SceneDefinition CreateRuslanStart()
    {
        const string sceneId = "ruslan_start";

        var start = Node(
            "start",
            "SceneStart",
            "Начало сцены",
            80,
            180,
            new SocketDefinition("start.out", "Далее", SocketDirection.Output));

        var dialogue = Node(
            "dialogue",
            "Dialogue",
            "Руслан говорит",
            360,
            180,
            new[]
            {
                new SocketDefinition("dialogue.in", "Вход", SocketDirection.Input),
                new SocketDefinition("dialogue.out", "Далее", SocketDirection.Output)
            },
            new Dictionary<string, string>
            {
                ["dialogueId"] = "ruslan.greeting"
            });

        var choice = Node(
            "choice",
            "Choice",
            "Предложение Руслана",
            660,
            180,
            new[]
            {
                new SocketDefinition("choice.in", "Вход", SocketDirection.Input),
                new SocketDefinition("choice.accept", "Принять", SocketDirection.Output),
                new SocketDefinition("choice.decline", "Отказаться", SocketDirection.Output)
            },
            new Dictionary<string, string>
            {
                ["choiceId"] = "ruslan.offer"
            });

        var accept = Node(
            "accept",
            "SceneEnd",
            "Сцена завершена: принято",
            960,
            120,
            new SocketDefinition("accept.in", "Вход", SocketDirection.Input));

        var decline = Node(
            "decline",
            "SceneEnd",
            "Сцена завершена: отказ",
            960,
            240,
            new SocketDefinition("decline.in", "Вход", SocketDirection.Input));

        var graph = new SceneGraph(
            sceneId,
            "Начальный разговор с Русланом",
            new[] { start, dialogue, choice, accept, decline },
            new[]
            {
                Connection("start", "start.out", "dialogue", "dialogue.in"),
                Connection("dialogue", "dialogue.out", "choice", "choice.in"),
                Connection("choice", "choice.accept", "accept", "accept.in"),
                Connection("choice", "choice.decline", "decline", "decline.in")
            });

        var dialogues = new[]
        {
            new SceneDialogue(
                "ruslan.greeting",
                "Руслан",
                "Есть для тебя особое предложение.")
        };

        var choices = new[]
        {
            new SceneChoice(
                "ruslan.offer",
                "Предложение",
                "Руслан",
                "Нужно найти особое мясо. Возьмёшься?",
                new[]
                {
                    new SceneChoiceOption(
                        "ruslan.offer.accept",
                        "Да, берусь.",
                        "choice.accept"),
                    new SceneChoiceOption(
                        "ruslan.offer.decline",
                        "Нет, сейчас не могу.",
                        "choice.decline")
                })
        };

        return new SceneDefinition(
            sceneId,
            "Начальный разговор с Русланом",
            "Минимальная canonical Scene для проверки Dialogue → Choice → SceneEnd.",
            graph,
            dialogues,
            choices);
    }

    private static SceneNode Node(
        string id,
        string type,
        string title,
        double x,
        double y,
        SocketDefinition socket,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        Node(id, type, title, x, y, new[] { socket }, parameters);

    private static SceneNode Node(
        string id,
        string type,
        string title,
        double x,
        double y,
        IReadOnlyList<SocketDefinition> sockets,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new(id, type, title, x, y, sockets)
        {
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    private static SceneConnection Connection(
        string fromNodeId,
        string fromSocketId,
        string toNodeId,
        string toSocketId) =>
        new(fromNodeId, fromSocketId, toNodeId, toSocketId);
}
