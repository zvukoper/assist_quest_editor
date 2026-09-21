namespace AssistQuestEditor.Domain;

public sealed record SceneGraphConnectionResult(bool Added, SceneConnection? Connection, string? Error)
{
    public static SceneGraphConnectionResult Success(SceneConnection connection) => new(true, connection, null);
    public static SceneGraphConnectionResult Failure(string error) => new(false, null, error);
}

public sealed class SceneGraphStore
{
    private SceneDefinition _value;
    private readonly Stack<SceneDefinition> _undo = new();
    private readonly Stack<SceneDefinition> _redo = new();

    public SceneGraphStore(SceneDefinition initial) =>
        _value = initial ?? throw new ArgumentNullException(nameof(initial));

    public SceneDefinition Value => _value;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;

    public SceneNode? FindNode(string nodeId) =>
        _value.Graph.Nodes.FirstOrDefault(node =>
            node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));

    public SceneNode AddNode(string nodeType, string title, double x, double y)
    {
        var type = string.IsNullOrWhiteSpace(nodeType) ? "Dialogue" : nodeType.Trim();
        var nodeId = "scene-node-" + Guid.NewGuid().ToString("N")[..8];
        var parameters = SceneNodeCatalog.CreateDefaultParameters(type);
        var node = new SceneNode(
            nodeId,
            type,
            string.IsNullOrWhiteSpace(title) ? type : title.Trim(),
            x,
            y,
            SceneNodeCatalog.CreateSockets(type, nodeId, parameters))
        {
            Parameters = parameters
        };

        Apply(_value with { Graph = _value.Graph with { Nodes = _value.Graph.Nodes.Append(node).ToArray() } });
        return node;
    }

    public SceneNode? UpdateNode(
        string nodeId,
        string? title = null,
        double? x = null,
        double? y = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var nodes = _value.Graph.Nodes.ToArray();
        var index = Array.FindIndex(nodes, node =>
            node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var current = nodes[index];
        var nextParameters = parameters is null ? current.Parameters : NormalizeParameters(parameters);
        var nextSockets = parameters is null
            ? current.Sockets
            : SceneNodeCatalog.CreateSockets(current.NodeType, current.NodeId, nextParameters);

        nodes[index] = current with
        {
            Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
            X = x ?? current.X,
            Y = y ?? current.Y,
            Parameters = nextParameters,
            Sockets = nextSockets
        };

        var socketIds = nodes.ToDictionary(
            node => node.NodeId,
            node => node.Sockets.Select(socket => socket.SocketId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var connections = _value.Graph.Connections
            .Where(connection =>
                socketIds.TryGetValue(connection.FromNodeId, out var fromSockets) &&
                fromSockets.Contains(connection.FromSocketId) &&
                socketIds.TryGetValue(connection.ToNodeId, out var toSockets) &&
                toSockets.Contains(connection.ToSocketId))
            .ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes, Connections = connections }
        });

        return nodes[index];
    }

    public bool RemoveNode(string nodeId)
    {
        var nodes = _value.Graph.Nodes
            .Where(node => !node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nodes.Length == _value.Graph.Nodes.Count) return false;

        var connections = _value.Graph.Connections
            .Where(connection =>
                !connection.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) &&
                !connection.ToNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes, Connections = connections }
        });
        return true;
    }

    public SceneGraphConnectionResult Connect(
        string fromNodeId,
        string fromSocketId,
        string toNodeId,
        string toSocketId)
    {
        var fromNode = FindNode(fromNodeId);
        var toNode = FindNode(toNodeId);
        if (fromNode is null || toNode is null)
            return SceneGraphConnectionResult.Failure("Для связи нужны две существующие ноды.");

        var fromSocket = fromNode.Sockets.FirstOrDefault(socket =>
            socket.SocketId.Equals(fromSocketId, StringComparison.OrdinalIgnoreCase));
        var toSocket = toNode.Sockets.FirstOrDefault(socket =>
            socket.SocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase));

        if (fromSocket is null || toSocket is null)
            return SceneGraphConnectionResult.Failure("Указанный socket не найден.");

        if (fromSocket.Direction != SocketDirection.Output || toSocket.Direction != SocketDirection.Input)
            return SceneGraphConnectionResult.Failure("Связь должна идти из Output в Input.");

        if (_value.Graph.Connections.Any(connection =>
            connection.FromNodeId.Equals(fromNodeId, StringComparison.OrdinalIgnoreCase) &&
            connection.FromSocketId.Equals(fromSocketId, StringComparison.OrdinalIgnoreCase) &&
            connection.ToNodeId.Equals(toNodeId, StringComparison.OrdinalIgnoreCase) &&
            connection.ToSocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase)))
            return SceneGraphConnectionResult.Failure("Такая связь уже существует.");

        if (_value.Graph.Connections.Any(connection =>
            connection.ToNodeId.Equals(toNodeId, StringComparison.OrdinalIgnoreCase) &&
            connection.ToSocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase)))
            return SceneGraphConnectionResult.Failure("Входной socket уже подключён.");

        var connectionResult = new SceneConnection(
            fromNode.NodeId,
            fromSocket.SocketId,
            toNode.NodeId,
            toSocket.SocketId);

        Apply(_value with
        {
            Graph = _value.Graph with
            {
                Connections = _value.Graph.Connections.Append(connectionResult).ToArray()
            }
        });
        return SceneGraphConnectionResult.Success(connectionResult);
    }

    public bool Disconnect(SceneConnection connection)
    {
        var connections = _value.Graph.Connections.Where(item => item != connection).ToArray();
        if (connections.Length == _value.Graph.Connections.Count) return false;

        Apply(_value with { Graph = _value.Graph with { Connections = connections } });
        return true;
    }

    public void Replace(SceneDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _value = definition;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int ApplyLayout()
    {
        var positions = SceneGraphLayout.Compute(_value.Graph);
        if (positions.Count == 0) return 0;

        var changed = 0;
        var nodes = _value.Graph.Nodes.Select(node =>
        {
            if (!positions.TryGetValue(node.NodeId, out var position) ||
                (Math.Abs(node.X - position.X) < 0.001 && Math.Abs(node.Y - position.Y) < 0.001))
                return node;

            changed++;
            return node with { X = position.X, Y = position.Y };
        }).ToArray();

        if (changed == 0) return 0;

        Apply(_value with { Graph = _value.Graph with { Nodes = nodes } });
        return changed;
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
            parameters
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(
                    pair => pair.Key.Trim(),
                    pair => pair.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private void Apply(SceneDefinition next)
    {
        _undo.Push(_value);
        _value = next;
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}