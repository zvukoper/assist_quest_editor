namespace AssistQuestEditor.Domain;

public static class SceneGraphValidator
{
    public static IReadOnlyList<GraphDiagnostic> Validate(SceneDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var diagnostics = new List<GraphDiagnostic>();
        var graph = definition.Graph;

        if (string.IsNullOrWhiteSpace(definition.Id))
            diagnostics.Add(new GraphDiagnostic("SCENE001", GraphDiagnosticSeverity.Error, "У Scene Definition не задан Id."));

        if (string.IsNullOrWhiteSpace(definition.Title))
            diagnostics.Add(new GraphDiagnostic("SCENE002", GraphDiagnosticSeverity.Warning, "У Scene Definition не задано название."));

        if (string.IsNullOrWhiteSpace(graph.Id))
            diagnostics.Add(new GraphDiagnostic("SCENE003", GraphDiagnosticSeverity.Error, "У Scene Graph не задан Id."));

        var nodesById = new Dictionary<string, SceneNode>(StringComparer.OrdinalIgnoreCase);
        var socketsByNode = new Dictionary<string, Dictionary<string, SocketDefinition>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId))
            {
                diagnostics.Add(new GraphDiagnostic("SCENE_NODE001", GraphDiagnosticSeverity.Error, "У Scene node пустой NodeId."));
                continue;
            }

            if (!nodesById.TryAdd(node.NodeId, node))
                diagnostics.Add(new GraphDiagnostic("SCENE_NODE002", GraphDiagnosticSeverity.Error, "NodeId не уникален.", node.NodeId));

            if (!SceneNodeCatalog.IsRegistered(node.NodeType))
                diagnostics.Add(new GraphDiagnostic("SCENE_NODE003", GraphDiagnosticSeverity.Error, "Неизвестный Scene NodeType «" + node.NodeType + "».", node.NodeId));

            var socketMap = new Dictionary<string, SocketDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var socket in node.Sockets)
            {
                if (string.IsNullOrWhiteSpace(socket.SocketId))
                {
                    diagnostics.Add(new GraphDiagnostic("SCENE_SOCKET001", GraphDiagnosticSeverity.Error, "У ноды есть socket с пустым SocketId.", node.NodeId));
                    continue;
                }

                if (!socketMap.TryAdd(socket.SocketId, socket))
                    diagnostics.Add(new GraphDiagnostic("SCENE_SOCKET002", GraphDiagnosticSeverity.Error, "SocketId не уникален внутри ноды.", node.NodeId, socket.SocketId));
            }

            socketsByNode[node.NodeId] = socketMap;
        }

        var starts = nodesById.Values
            .Where(node => node.NodeType.Equals("SceneStart", StringComparison.OrdinalIgnoreCase))
            .Select(node => node.NodeId)
            .ToArray();
        var ends = nodesById.Values
            .Where(node => node.NodeType.Equals("SceneEnd", StringComparison.OrdinalIgnoreCase))
            .Select(node => node.NodeId)
            .ToArray();

        if (starts.Length != 1)
            diagnostics.Add(new GraphDiagnostic("SCENE_FLOW001", GraphDiagnosticSeverity.Error, "Ожидается ровно одна SceneStart, найдено: " + starts.Length + "."));

        if (ends.Length == 0)
            diagnostics.Add(new GraphDiagnostic("SCENE_FLOW002", GraphDiagnosticSeverity.Error, "В Scene Graph отсутствует SceneEnd."));

        var adjacency = nodesById.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var connectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectedInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var connection in graph.Connections)
        {
            var key = string.Join("\u001f", connection.FromNodeId, connection.FromSocketId, connection.ToNodeId, connection.ToSocketId);
            if (!connectionKeys.Add(key))
                diagnostics.Add(new GraphDiagnostic("SCENE_CONN001", GraphDiagnosticSeverity.Error, "Связь полностью дублируется.", connection.FromNodeId, connection.FromSocketId));

            if (!nodesById.TryGetValue(connection.FromNodeId, out var fromNode) ||
                !nodesById.TryGetValue(connection.ToNodeId, out var toNode))
            {
                diagnostics.Add(new GraphDiagnostic("SCENE_CONN002", GraphDiagnosticSeverity.Error, "Нода связи не найдена.", connection.FromNodeId, connection.FromSocketId));
                continue;
            }

            if (!socketsByNode[fromNode.NodeId].TryGetValue(connection.FromSocketId, out var fromSocket) ||
                !socketsByNode[toNode.NodeId].TryGetValue(connection.ToSocketId, out var toSocket))
            {
                diagnostics.Add(new GraphDiagnostic("SCENE_CONN003", GraphDiagnosticSeverity.Error, "Socket связи не найден.", connection.FromNodeId, connection.FromSocketId));
                continue;
            }

            if (fromSocket.Direction != SocketDirection.Output || toSocket.Direction != SocketDirection.Input)
            {
                diagnostics.Add(new GraphDiagnostic("SCENE_CONN004", GraphDiagnosticSeverity.Error, "Связь должна идти из Output в Input.", fromNode.NodeId, fromSocket.SocketId));
                continue;
            }

            var inputKey = toNode.NodeId + "\u001f" + toSocket.SocketId;
            if (!connectedInputs.Add(inputKey))
                diagnostics.Add(new GraphDiagnostic("SCENE_CONN005", GraphDiagnosticSeverity.Error, "Входной socket подключён более одного раза.", toNode.NodeId, toSocket.SocketId));

            adjacency[fromNode.NodeId].Add(toNode.NodeId);
        }

        foreach (var node in nodesById.Values)
        {
            if (node.NodeType.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                var id = Parameter(node, "dialogueId");
                if (string.IsNullOrWhiteSpace(id) ||
                    !definition.Dialogues.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    diagnostics.Add(new GraphDiagnostic("SCENE_REF001", GraphDiagnosticSeverity.Error, "Dialogue resource «" + id + "» не найден.", node.NodeId));
            }

            if (node.NodeType.Equals("Choice", StringComparison.OrdinalIgnoreCase))
            {
                var id = Parameter(node, "choiceId");
                var choice = definition.Choices.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

                if (choice is null)
                {
                    diagnostics.Add(new GraphDiagnostic("SCENE_REF002", GraphDiagnosticSeverity.Error, "Choice resource «" + id + "» не найден.", node.NodeId));
                }
                else
                {
                    foreach (var option in choice.Options)
                    {
                        if (!node.Sockets.Any(socket =>
                            socket.SocketId.Equals(option.OutputSocketId, StringComparison.OrdinalIgnoreCase) &&
                            socket.Direction == SocketDirection.Output))
                            diagnostics.Add(new GraphDiagnostic("SCENE_REF003", GraphDiagnosticSeverity.Error, "У Choice отсутствует Output socket «" + option.OutputSocketId + "».", node.NodeId, option.OutputSocketId));
                    }
                }
            }
        }

        if (starts.Length == 1)
        {
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(new[] { starts[0] });

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!reachable.Add(id)) continue;

                foreach (var next in adjacency[id])
                    if (!reachable.Contains(next)) queue.Enqueue(next);
            }

            foreach (var node in nodesById.Values)
            {
                if (!reachable.Contains(node.NodeId))
                    diagnostics.Add(new GraphDiagnostic("SCENE_FLOW003", GraphDiagnosticSeverity.Warning, "Нода недостижима из SceneStart.", node.NodeId));
            }
        }

        return diagnostics;
    }

    private static string Parameter(SceneNode node, string key) =>
        node.Parameters.TryGetValue(key, out var value) ? value : string.Empty;
}