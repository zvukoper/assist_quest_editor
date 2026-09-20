namespace AssistQuestEditor.Domain;

public sealed record GraphConnectionResult(bool Added, QuestConnection? Connection, string? Error)
{
    public static GraphConnectionResult Success(QuestConnection connection) => new(true, connection, null);
    public static GraphConnectionResult Failure(string error) => new(false, null, error);
}

public sealed class QuestGraphStore
{
    private QuestGraph _value;
    public QuestGraphStore(QuestGraph initial) => _value = initial ?? throw new ArgumentNullException(nameof(initial));
    public QuestGraph Value => _value;
    public event EventHandler? Changed;

    public QuestNode? FindNode(string nodeId) =>
        _value.Nodes.FirstOrDefault(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));

    public QuestNode AddNode(string nodeType, string title, double x, double y)
    {
        var type = string.IsNullOrWhiteSpace(nodeType) ? "Phase" : nodeType.Trim();
        var nodeId = "node-" + Guid.NewGuid().ToString("N")[..8];
        var node = new QuestNode(
            nodeId,
            type,
            string.IsNullOrWhiteSpace(title) ? type : title.Trim(),
            x,
            y,
            QuestNodeCatalog.CreateSockets(type, nodeId));

        _value = _value with { Nodes = _value.Nodes.Append(node).ToArray() };
        Changed?.Invoke(this, EventArgs.Empty);
        return node;
    }

    public QuestNode? UpdateNode(string nodeId, string? title = null, double? x = null, double? y = null)
    {
        var nodes = _value.Nodes.ToArray();
        var index = Array.FindIndex(nodes, node => node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var current = nodes[index];
        nodes[index] = current with
        {
            Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
            X = x ?? current.X,
            Y = y ?? current.Y
        };

        _value = _value with { Nodes = nodes };
        Changed?.Invoke(this, EventArgs.Empty);
        return nodes[index];
    }

    public bool RemoveNode(string nodeId)
    {
        var nodes = _value.Nodes.Where(node => !node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nodes.Length == _value.Nodes.Count) return false;

        var connections = _value.Connections.Where(connection =>
            !connection.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) &&
            !connection.ToNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)).ToArray();

        _value = _value with { Nodes = nodes, Connections = connections };
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public GraphConnectionResult Connect(string fromNodeId, string fromSocketId, string toNodeId, string toSocketId)
    {
        var fromNode = FindNode(fromNodeId);
        var toNode = FindNode(toNodeId);
        if (fromNode is null || toNode is null)
            return GraphConnectionResult.Failure("Для связи нужны две существующие ноды.");

        var fromSocket = fromNode.Sockets.FirstOrDefault(x => x.SocketId.Equals(fromSocketId, StringComparison.OrdinalIgnoreCase));
        var toSocket = toNode.Sockets.FirstOrDefault(x => x.SocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase));
        if (fromSocket is null || toSocket is null)
            return GraphConnectionResult.Failure("Указанный socket не найден.");

        if (fromSocket.Direction != SocketDirection.Output || toSocket.Direction != SocketDirection.Input)
            return GraphConnectionResult.Failure("Связь должна идти из Output в Input.");

        if (_value.Connections.Any(x =>
            x.FromNodeId.Equals(fromNodeId, StringComparison.OrdinalIgnoreCase) &&
            x.FromSocketId.Equals(fromSocketId, StringComparison.OrdinalIgnoreCase) &&
            x.ToNodeId.Equals(toNodeId, StringComparison.OrdinalIgnoreCase) &&
            x.ToSocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase)))
            return GraphConnectionResult.Failure("Такая связь уже существует.");

        var connection = new QuestConnection(fromNode.NodeId, fromSocket.SocketId, toNode.NodeId, toSocket.SocketId);
        _value = _value with { Connections = _value.Connections.Append(connection).ToArray() };
        Changed?.Invoke(this, EventArgs.Empty);
        return GraphConnectionResult.Success(connection);
    }

    public bool Disconnect(QuestConnection connection)
    {
        var connections = _value.Connections.Where(x => x != connection).ToArray();
        if (connections.Length == _value.Connections.Count) return false;
        _value = _value with { Connections = connections };
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

public static class QuestGraphFactory
{
    public static QuestGraph CreateStarter() =>
        new(
            "special_marinated_shashlik",
            "Спецмаринад для Руслана",
            new[]
            {
                new QuestNode("start", "Start", "Начало квеста", 80, 250, QuestNodeCatalog.CreateSockets("Start", "start")),
                new QuestNode("condition", "Condition", "Проверить этап", 360, 250, QuestNodeCatalog.CreateSockets("Condition", "condition")),
                new QuestNode("dialogue", "DialogueScene", "Разговор с Русланом", 680, 180, QuestNodeCatalog.CreateSockets("DialogueScene", "dialogue")),
                new QuestNode("end", "End", "Завершение", 1000, 180, QuestNodeCatalog.CreateSockets("End", "end"))
            },
            new[]
            {
                new QuestConnection("start", "start.out", "condition", "condition.in"),
                new QuestConnection("condition", "condition.true", "dialogue", "dialogue.in"),
                new QuestConnection("dialogue", "dialogue.out", "end", "end.in")
            });
}

public static class QuestNodeCatalog
{
    private static readonly string[] Branching = ["Condition", "And", "Or", "Not", "Switch", "Random", "Choice"];

    public static IReadOnlyList<SocketDefinition> CreateSockets(string nodeType, string nodeId)
    {
        if (nodeType.Equals("Start", StringComparison.OrdinalIgnoreCase))
            return [new SocketDefinition($"{nodeId}.out", "Далее", SocketDirection.Output)];

        if (nodeType.Equals("End", StringComparison.OrdinalIgnoreCase))
            return [new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input)];

        if (Branching.Contains(nodeType, StringComparer.OrdinalIgnoreCase))
            return
            [
                new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input),
                new SocketDefinition($"{nodeId}.true", "Да", SocketDirection.Output),
                new SocketDefinition($"{nodeId}.false", "Нет", SocketDirection.Output, FlowKind.Cut)
            ];

        return
        [
            new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input),
            new SocketDefinition($"{nodeId}.out", "Далее", SocketDirection.Output)
        ];
    }
}
