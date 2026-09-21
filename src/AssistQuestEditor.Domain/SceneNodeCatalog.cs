namespace AssistQuestEditor.Domain;

public static class SceneNodeCatalog
{
    public static readonly IReadOnlyList<(string Type, string Title)> Types =
    [
        ("SceneStart", "Начало сцены"),
        ("Dialogue", "Диалог"),
        ("Choice", "Выбор"),
        ("SceneWait", "Ожидание"),
        ("SceneEvent", "Событие"),
        ("SceneEnd", "Конец сцены")
    ];

    public static IReadOnlyDictionary<string, string> CreateDefaultParameters(string nodeType) =>
        nodeType.ToLowerInvariant() switch
        {
            "dialogue" => Parameters(("dialogueId", "")),
            "choice" => Parameters(("choiceId", ""), ("outputCount", "2")),
            "scenewait" => Parameters(("seconds", "1")),
            "sceneevent" => Parameters(("eventType", "")),
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    public static IReadOnlyList<SocketDefinition> CreateSockets(
        string nodeType,
        string nodeId,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (nodeType.Equals("SceneStart", StringComparison.OrdinalIgnoreCase))
            return [new SocketDefinition($"{nodeId}.out", "Далее", SocketDirection.Output)];

        if (nodeType.Equals("SceneEnd", StringComparison.OrdinalIgnoreCase))
            return [new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input)];

        if (nodeType.Equals("Choice", StringComparison.OrdinalIgnoreCase))
        {
            var count = GetOutputCount(parameters);
            var outputs = Enumerable.Range(1, count)
                .Select(index => new SocketDefinition(
                    $"{nodeId}.option{index}",
                    $"Вариант {index}",
                    SocketDirection.Output))
                .ToArray();

            return [new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input), .. outputs];
        }

        return
        [
            new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input),
            new SocketDefinition($"{nodeId}.out", "Далее", SocketDirection.Output)
        ];
    }

    public static bool IsRegistered(string nodeType) =>
        Types.Any(item => item.Type.Equals(nodeType, StringComparison.OrdinalIgnoreCase));

    private static int GetOutputCount(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is not null &&
            parameters.TryGetValue("outputCount", out var value) &&
            int.TryParse(value, out var parsed))
            return Math.Clamp(parsed, 2, 16);

        return 2;
    }

    private static Dictionary<string, string> Parameters(params (string Key, string Value)[] values) =>
        values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
}