namespace AssistQuestEditor.Domain;

public sealed record GraphConnectionResult(bool Added, QuestConnection? Connection, string? Error)
{
    public static GraphConnectionResult Success(QuestConnection connection) => new(true, connection, null);
    public static GraphConnectionResult Failure(string error) => new(false, null, error);
}

public sealed class QuestGraphStore
{
    private QuestGraph _value;
    private string _description = string.Empty;
    private IReadOnlyList<string> _sceneIds = Array.Empty<string>();
    private readonly Stack<QuestGraph> _undo = new();
    private readonly Stack<QuestGraph> _redo = new();

    public QuestGraphStore(QuestGraph initial) =>
        _value = initial ?? throw new ArgumentNullException(nameof(initial));

    public QuestGraphStore(QuestDefinition initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.Graph is null)
            throw new ArgumentException("Quest Definition должен содержать Graph.", nameof(initial));

        _value = initial.Graph;
        _description = initial.Description ?? string.Empty;
        _sceneIds = initial.SceneIds ?? Array.Empty<string>();
    }

    public QuestGraph Value => _value;

    /// <summary>
    /// Полный Quest Definition вместе с метаданными документа (Description, SceneIds).
    ///
    /// Хранится именно здесь, а не в UI: иначе сохранение собирало бы новый
    /// Quest Definition из одного графа и затирало метаданные, которые есть в
    /// файле. По той же причине SceneGraphStore хранит весь SceneDefinition.
    /// </summary>
    public QuestDefinition Definition =>
        new(_value.Id, _value.Name, _description, _value, _sceneIds);

    public string Description => _description;
    public IReadOnlyList<string> SceneIds => _sceneIds;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;

    public QuestNode? FindNode(string nodeId) =>
        _value.Nodes.FirstOrDefault(x => x.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));

    public QuestNode AddNode(string nodeType, string title, double x, double y)
    {
        var type = string.IsNullOrWhiteSpace(nodeType) ? "Phase" : nodeType.Trim();
        var nodeId = "node-" + Guid.NewGuid().ToString("N")[..8];
        var parameters = QuestNodeCatalog.CreateDefaultParameters(type);
        var node = new QuestNode(
            nodeId,
            type,
            string.IsNullOrWhiteSpace(title) ? type : title.Trim(),
            x,
            y,
            QuestNodeCatalog.CreateSockets(type, nodeId, parameters))
        {
            Parameters = parameters
        };

        var graph = _value with { Nodes = _value.Nodes.Append(node).ToArray() };
        var positions = QuestGraphLayout.Compute(graph);
        var nodes = graph.Nodes
            .Select(item => positions.TryGetValue(item.NodeId, out var position)
                ? item with { X = position.X, Y = position.Y }
                : item)
            .ToArray();

        var laidOutNode = nodes.Single(item => item.NodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase));
        Apply(graph with { Nodes = nodes });
        return laidOutNode;
    }

    public QuestNode? UpdateNode(
        string nodeId,
        string? title = null,
        double? x = null,
        double? y = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var nodes = _value.Nodes.ToArray();
        var index = Array.FindIndex(nodes, node => node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var current = nodes[index];
        var nextParameters = parameters is null
            ? current.Parameters
            : NormalizeParameters(parameters);

        var nextSockets = parameters is null
            ? current.Sockets
            : QuestNodeCatalog.CreateSockets(current.NodeType, current.NodeId, nextParameters);

        nodes[index] = current with
        {
            Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
            X = x ?? current.X,
            Y = y ?? current.Y,
            Parameters = nextParameters,
            Sockets = nextSockets
        };

        var socketIdsByNode = nodes.ToDictionary(
            node => node.NodeId,
            node => node.Sockets.Select(socket => socket.SocketId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var connections = _value.Connections
            .Where(connection =>
                socketIdsByNode.TryGetValue(connection.FromNodeId, out var fromSockets) &&
                fromSockets.Contains(connection.FromSocketId) &&
                socketIdsByNode.TryGetValue(connection.ToNodeId, out var toSockets) &&
                toSockets.Contains(connection.ToSocketId))
            .ToArray();

        Apply(_value with { Nodes = nodes, Connections = connections });
        return nodes[index];
    }

    public bool RemoveNode(string nodeId)
    {
        var nodes = _value.Nodes
            .Where(node => !node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (nodes.Length == _value.Nodes.Count) return false;

        var connections = _value.Connections
            .Where(connection =>
                !connection.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) &&
                !connection.ToNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Apply(_value with { Nodes = nodes, Connections = connections });
        return true;
    }

    /// <summary>
    /// Перестраивает координаты всех нод иерархической раскладкой.
    /// Изменение применяется одной операцией, поэтому Undo возвращает прежнюю
    /// раскладку целиком, а не по ноде.
    /// </summary>
    /// <returns>Количество нод, у которых координаты изменились.</returns>
    public int ApplyLayout()
    {
        var positions = QuestGraphLayout.Compute(_value);
        if (positions.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        var nodes = _value.Nodes
            .Select(node =>
            {
                if (!positions.TryGetValue(node.NodeId, out var position))
                {
                    return node;
                }

                if (Math.Abs(node.X - position.X) < 0.001 &&
                    Math.Abs(node.Y - position.Y) < 0.001)
                {
                    return node;
                }

                changed++;
                return node with { X = position.X, Y = position.Y };
            })
            .ToArray();

        if (changed == 0)
        {
            return 0;
        }

        Apply(_value with { Nodes = nodes });
        return changed;
    }

    public GraphConnectionResult Connect(string fromNodeId, string fromSocketId, string toNodeId, string toSocketId)
    {
        var fromNode = FindNode(fromNodeId);
        var toNode = FindNode(toNodeId);
        if (fromNode is null || toNode is null)
            return GraphConnectionResult.Failure("Для связи нужны две существующие ноды.");

        var fromSocket = fromNode.Sockets.FirstOrDefault(x =>
            x.SocketId.Equals(fromSocketId, StringComparison.OrdinalIgnoreCase));
        var toSocket = toNode.Sockets.FirstOrDefault(x =>
            x.SocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase));

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

        var connection = new QuestConnection(
            fromNode.NodeId,
            fromSocket.SocketId,
            toNode.NodeId,
            toSocket.SocketId);

        Apply(_value with { Connections = _value.Connections.Append(connection).ToArray() });
        return GraphConnectionResult.Success(connection);
    }

    public bool Disconnect(QuestConnection connection)
    {
        var connections = _value.Connections.Where(x => x != connection).ToArray();
        if (connections.Length == _value.Connections.Count) return false;

        Apply(_value with { Connections = connections });
        return true;
    }

    /// <summary>
    /// Заменяет граф целиком, как новый документ: метаданные Description/SceneIds
    /// сбрасываются, потому что при замене только графа о них ничего не известно.
    ///
    /// Это fail-safe выбор: унаследованные метаданные прежнего документа
    /// записались бы в файл молча. Загрузка документа идёт через
    /// Replace(QuestDefinition), а правки графа (AddNode/UpdateNode) метаданные
    /// сохраняют — их меняет только Apply.
    /// </summary>
    public void Replace(QuestGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _value = graph;
        _description = string.Empty;
        _sceneIds = Array.Empty<string>();
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Загружает документ целиком: граф вместе с метаданными Description/SceneIds.
    /// Загрузка через Replace(QuestGraph) оставила бы метаданные от предыдущего
    /// документа, и следующее сохранение записало бы их вместо прочитанных из файла.
    /// </summary>
    public void Replace(QuestDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Graph is null)
            throw new ArgumentException("Quest Definition должен содержать Graph.", nameof(definition));

        _value = definition.Graph;
        _description = definition.Description ?? string.Empty;
        _sceneIds = definition.SceneIds ?? Array.Empty<string>();
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        _redo.Push(_value);
        _value = _undo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        _undo.Push(_value);
        _value = _redo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static IReadOnlyDictionary<string, string> NormalizeParameters(
        IReadOnlyDictionary<string, string> parameters) =>
        new Dictionary<string, string>(
            parameters.Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(
                    pair => pair.Key.Trim(),
                    pair => pair.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private void Apply(QuestGraph next)
    {
        _undo.Push(_value);
        _value = next;
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
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
                CreateNode("start", "Start", "Начало квеста", 80, 250),
                CreateNode(
                    "condition",
                    "Condition",
                    "Проверить этап",
                    360,
                    250,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["operator"] = "QuestStepIs",
                        ["left"] = "step",
                        ["comparison"] = "==",
                        ["right"] = "return_to_ruslan"
                    }),
                CreateNode(
                    "dialogue",
                    "DialogueScene",
                    "Разговор с Русланом",
                    680,
                    180,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["sceneId"] = "ruslan_start"
                    }),
                CreateNode("end", "End", "Завершение", 1000, 180)
            },
            new[]
            {
                new QuestConnection("start", "start.out", "condition", "condition.in"),
                new QuestConnection("condition", "condition.true", "dialogue", "dialogue.in"),
                new QuestConnection("dialogue", "dialogue.out", "end", "end.in")
            });

    private static QuestNode CreateNode(
        string nodeId,
        string nodeType,
        string title,
        double x,
        double y,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var actualParameters = parameters ?? QuestNodeCatalog.CreateDefaultParameters(nodeType);
        return new QuestNode(
            nodeId,
            nodeType,
            title,
            x,
            y,
            QuestNodeCatalog.CreateSockets(nodeType, nodeId, actualParameters))
        {
            Parameters = actualParameters
        };
    }
}

public static class QuestNodeCatalog
{
    private static readonly string[] FixedBranching = ["Condition", "And", "Or", "Not"];
    private static readonly string[] DynamicBranching = ["Switch", "Random", "Choice"];

    public static IReadOnlyDictionary<string, string> CreateDefaultParameters(string nodeType)
    {
        return nodeType.ToLowerInvariant() switch
        {
            "interaction" => Parameters(
                ("worldPointId", ""),
                ("triggerRadius", "35")),
            "condition" => Parameters(
                ("operator", "QuestStepIs"),
                ("left", "step"),
                ("comparison", "=="),
                ("right", "return_to_ruslan")),
            "wait" => Parameters(("seconds", "1")),
            "waitforcondition" => Parameters(("conditionId", "")),
            "waitforevent" => Parameters(("eventType", "")),
            "setstatus" => Parameters(("status", "Active")),
            "setstep" => Parameters(("step", "")),
            "setflag" => Parameters(("key", ""), ("value", "true")),
            "setvariable" => Parameters(("key", ""), ("value", "")),
            "dialoguescene" => Parameters(("sceneId", "")),
            "reward" => Parameters(("rewardId", "")),
            "giveitem" => Parameters(("itemId", ""), ("count", "1")),
            "removeitem" => Parameters(("itemId", ""), ("count", "1")),
            "sethealth" => Parameters(("value", "100")),
            "setenergy" => Parameters(("value", "100")),
            "sethydration" => Parameters(("value", "100")),
            "setfatigue" => Parameters(("value", "0")),
            "addexperience" => Parameters(("amount", "1")),
            "addmoney" => Parameters(("amount", "1")),
            "removemoney" => Parameters(("amount", "1")),
            "setreserve" => Parameters(("value", "0")),
            "setcharacterstat" => Parameters(("stat", "strength"), ("value", "5")),
            "addreputation" => Parameters(("faction", ""), ("amount", "1")),
            "removereputation" => Parameters(("faction", ""), ("amount", "1")),
            "switch" or "random" or "choice" => Parameters(("outputCount", "2")),
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static IReadOnlyList<SocketDefinition> CreateSockets(
        string nodeType,
        string nodeId,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        if (nodeType.Equals("Start", StringComparison.OrdinalIgnoreCase))
            return [new SocketDefinition($"{nodeId}.out", "Далее", SocketDirection.Output)];

        if (nodeType.Equals("End", StringComparison.OrdinalIgnoreCase))
            return [new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input)];

        if (FixedBranching.Contains(nodeType, StringComparer.OrdinalIgnoreCase))
            return
            [
                new SocketDefinition($"{nodeId}.in", "Вход", SocketDirection.Input),
                new SocketDefinition($"{nodeId}.true", "Да", SocketDirection.Output),
                new SocketDefinition($"{nodeId}.false", "Нет", SocketDirection.Output, FlowKind.Cut)
            ];

        if (DynamicBranching.Contains(nodeType, StringComparer.OrdinalIgnoreCase))
        {
            var count = GetOutputCount(parameters);
            var suffix = nodeType.ToLowerInvariant() switch
            {
                "switch" => "case",
                "random" => "branch",
                _ => "choice"
            };
            var label = nodeType.Equals("Switch", StringComparison.OrdinalIgnoreCase)
                ? "Вариант"
                : nodeType.Equals("Random", StringComparison.OrdinalIgnoreCase)
                    ? "Ветка"
                    : "Выбор";

            var outputs = Enumerable.Range(1, count)
                .Select(index => new SocketDefinition(
                    $"{nodeId}.{suffix}{index}",
                    $"{label} {index}",
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

    private static int GetOutputCount(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is not null &&
            parameters.TryGetValue("outputCount", out var value) &&
            int.TryParse(value, out var parsed))
        {
            return Math.Clamp(parsed, 2, 16);
        }

        return 2;
    }

    private static Dictionary<string, string> Parameters(params (string Key, string Value)[] values) =>
        values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
}
