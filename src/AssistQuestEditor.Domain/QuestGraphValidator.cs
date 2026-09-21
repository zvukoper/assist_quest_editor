namespace AssistQuestEditor.Domain;

public enum GraphDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record GraphDiagnostic(
    string Code,
    GraphDiagnosticSeverity Severity,
    string Message,
    string? NodeId = null,
    string? SocketId = null);

public static class QuestGraphValidator
{
    public static IReadOnlyList<GraphDiagnostic> Validate(QuestGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var diagnostics = new List<GraphDiagnostic>();

        if (string.IsNullOrWhiteSpace(graph.Id))
        {
            diagnostics.Add(new GraphDiagnostic(
                "GRAPH001",
                GraphDiagnosticSeverity.Error,
                "У графа не задан Id."));
        }

        if (string.IsNullOrWhiteSpace(graph.Name))
        {
            diagnostics.Add(new GraphDiagnostic(
                "GRAPH002",
                GraphDiagnosticSeverity.Warning,
                "У графа не задано отображаемое имя."));
        }

        var nodesById = new Dictionary<string, QuestNode>(StringComparer.OrdinalIgnoreCase);
        var socketsByNode = new Dictionary<string, Dictionary<string, SocketDefinition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "NODE001",
                    GraphDiagnosticSeverity.Error,
                    "Нода имеет пустой NodeId."));
                continue;
            }

            if (!nodesById.TryAdd(node.NodeId, node))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "NODE002",
                    GraphDiagnosticSeverity.Error,
                    "NodeId не уникален.",
                    node.NodeId));
            }

            if (string.IsNullOrWhiteSpace(node.NodeType))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "NODE003",
                    GraphDiagnosticSeverity.Error,
                    "У ноды не задан NodeType.",
                    node.NodeId));
            }

            var socketMap = new Dictionary<string, SocketDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var socket in node.Sockets)
            {
                if (string.IsNullOrWhiteSpace(socket.SocketId))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "SOCKET001",
                        GraphDiagnosticSeverity.Error,
                        "У ноды есть socket с пустым SocketId.",
                        node.NodeId));
                    continue;
                }

                if (!socketMap.TryAdd(socket.SocketId, socket))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "SOCKET002",
                        GraphDiagnosticSeverity.Error,
                        "SocketId не уникален внутри ноды.",
                        node.NodeId,
                        socket.SocketId));
                }
            }

            socketsByNode[node.NodeId] = socketMap;
        }

        var connectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adjacency = nodesById.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var starts = new List<string>();
        var ends = new List<string>();

        foreach (var node in nodesById.Values)
        {
            if (node.NodeType.Equals("Start", StringComparison.OrdinalIgnoreCase))
            {
                starts.Add(node.NodeId);
            }

            if (node.NodeType.Equals("End", StringComparison.OrdinalIgnoreCase))
            {
                ends.Add(node.NodeId);
            }
        }

        foreach (var connection in graph.Connections)
        {
            var key = string.Join(
                "\u001f",
                connection.FromNodeId,
                connection.FromSocketId,
                connection.ToNodeId,
                connection.ToSocketId);

            if (!connectionKeys.Add(key))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "CONNECTION006",
                    GraphDiagnosticSeverity.Error,
                    "Связь полностью дублируется.",
                    connection.FromNodeId,
                    connection.FromSocketId));
            }

            if (!nodesById.TryGetValue(connection.FromNodeId, out var fromNode))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "CONNECTION001",
                    GraphDiagnosticSeverity.Error,
                    "Исходная нода связи не найдена.",
                    connection.FromNodeId,
                    connection.FromSocketId));
                continue;
            }

            if (!nodesById.TryGetValue(connection.ToNodeId, out var toNode))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "CONNECTION002",
                    GraphDiagnosticSeverity.Error,
                    "Целевая нода связи не найдена.",
                    connection.ToNodeId,
                    connection.ToSocketId));
                continue;
            }

            if (!socketsByNode[fromNode.NodeId].TryGetValue(connection.FromSocketId, out var fromSocket))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "CONNECTION003",
                    GraphDiagnosticSeverity.Error,
                    "Исходный socket связи не найден.",
                    fromNode.NodeId,
                    connection.FromSocketId));
                continue;
            }

            if (!socketsByNode[toNode.NodeId].TryGetValue(connection.ToSocketId, out var toSocket))
            {
                diagnostics.Add(new GraphDiagnostic(
                    "CONNECTION004",
                    GraphDiagnosticSeverity.Error,
                    "Целевой socket связи не найден.",
                    toNode.NodeId,
                    connection.ToSocketId));
                continue;
            }

            if (fromSocket.Direction != SocketDirection.Output || toSocket.Direction != SocketDirection.Input)
            {
                diagnostics.Add(new GraphDiagnostic(
                    "CONNECTION005",
                    GraphDiagnosticSeverity.Error,
                    "Связь должна идти из Output в Input.",
                    fromNode.NodeId,
                    fromSocket.SocketId));
                continue;
            }

            adjacency[fromNode.NodeId].Add(toNode.NodeId);
        }

        if (starts.Count == 0)
        {
            diagnostics.Add(new GraphDiagnostic(
                "FLOW001",
                GraphDiagnosticSeverity.Warning,
                "В графе нет ноды Start."));
        }
        else if (starts.Count > 1)
        {
            diagnostics.Add(new GraphDiagnostic(
                "FLOW002",
                GraphDiagnosticSeverity.Warning,
                "В графе несколько нод Start."));
        }

        if (ends.Count == 0)
        {
            diagnostics.Add(new GraphDiagnostic(
                "FLOW003",
                GraphDiagnosticSeverity.Warning,
                "В графе нет ноды End."));
        }

        if (starts.Count > 0)
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(starts);

            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();
                if (!reachable.Add(nodeId))
                {
                    continue;
                }

                foreach (var nextNodeId in adjacency[nodeId])
                {
                    if (!reachable.Contains(nextNodeId))
                    {
                        queue.Enqueue(nextNodeId);
                    }
                }
            }

            foreach (var node in nodesById.Values)
            {
                if (!reachable.Contains(node.NodeId))
                {
                    diagnostics.Add(new GraphDiagnostic(
                        "FLOW004",
                        GraphDiagnosticSeverity.Warning,
                        "Нода недостижима из Start.",
                        node.NodeId));
                }
            }
        }

        return diagnostics;
    }
}
