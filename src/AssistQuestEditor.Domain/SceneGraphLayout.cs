namespace AssistQuestEditor.Domain;

public static class SceneGraphLayout
{
    public const double NodeWidth = 280;
    public const double HorizontalGap = 150;
    public const double VerticalGap = 45;
    public const double ComponentGap = 140;

    public sealed record NodePosition(double X, double Y);

    public static double NodeHeight(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var inputs = node.Sockets.Count(socket => socket.Direction == SocketDirection.Input);
        var outputs = node.Sockets.Count(socket => socket.Direction == SocketDirection.Output);
        return Math.Max(116, 78 + Math.Max(inputs, outputs) * 24);
    }

    public static IReadOnlyDictionary<string, NodePosition> Compute(SceneGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var nodes = graph.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeId))
            .GroupBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var positions = new Dictionary<string, NodePosition>(StringComparer.OrdinalIgnoreCase);
        if (nodes.Length == 0) return positions;

        var ids = nodes.Select(node => node.NodeId).ToArray();
        var nodeById = nodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var incoming = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var edge in graph.Connections)
        {
            if (!outgoing.ContainsKey(edge.FromNodeId) ||
                !incoming.ContainsKey(edge.ToNodeId) ||
                edge.FromNodeId.Equals(edge.ToNodeId, StringComparison.OrdinalIgnoreCase) ||
                outgoing[edge.FromNodeId].Contains(edge.ToNodeId, StringComparer.OrdinalIgnoreCase))
                continue;

            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
            incoming[edge.ToNodeId].Add(edge.FromNodeId);
        }

        var components = FindComponents(ids, outgoing, incoming);
        var componentY = 0d;

        foreach (var component in components)
        {
            var backEdges = FindBackEdges(component, outgoing);
            var order = TopologicalOrder(component, outgoing, backEdges);
            var layerOf = component.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);

            foreach (var id in order)
            {
                foreach (var child in outgoing[id])
                {
                    if (backEdges.Contains((id, child))) continue;
                    layerOf[child] = Math.Max(layerOf[child], layerOf[id] + 1);
                }
            }

            var layers = layerOf
                .GroupBy(pair => pair.Value)
                .OrderBy(group => group.Key)
                .Select(group => group.Select(pair => pair.Key).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList())
                .ToList();

            var componentBottom = componentY;
            for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                var layer = layers[layerIndex];
                var totalHeight = layer.Sum(id => NodeHeight(nodeById[id])) +
                    Math.Max(0, layer.Count - 1) * VerticalGap;
                var y = componentY;

                foreach (var id in layer)
                {
                    positions[id] = new NodePosition(
                        layerIndex * (NodeWidth + HorizontalGap),
                        y);
                    y += NodeHeight(nodeById[id]) + VerticalGap;
                }

                componentBottom = Math.Max(componentBottom, y);
            }

            componentY = componentBottom + ComponentGap;
        }

        return positions;
    }

    private static HashSet<(string From, string To)> FindBackEdges(
        IReadOnlyList<string> component,
        Dictionary<string, List<string>> outgoing)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var backEdges = new HashSet<(string From, string To)>();

        foreach (var root in component)
        {
            if (state.ContainsKey(root)) continue;

            var stack = new List<(string NodeId, int Index)> { (root, 0) };
            state[root] = 1;

            while (stack.Count > 0)
            {
                var current = stack[^1];
                var children = outgoing[current.NodeId];

                if (current.Index < children.Count)
                {
                    var child = children[current.Index];
                    stack[^1] = (current.NodeId, current.Index + 1);

                    if (!state.TryGetValue(child, out var childState))
                    {
                        state[child] = 1;
                        stack.Add((child, 0));
                    }
                    else if (childState == 1)
                    {
                        backEdges.Add((current.NodeId, child));
                    }
                }
                else
                {
                    state[current.NodeId] = 2;
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        return backEdges;
    }

    private static List<string> TopologicalOrder(
        IReadOnlyList<string> component,
        Dictionary<string, List<string>> outgoing,
        HashSet<(string From, string To)> backEdges)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var postOrder = new List<string>();

        foreach (var root in component)
        {
            if (!visited.Add(root)) continue;
            var stack = new List<(string NodeId, int Index)> { (root, 0) };

            while (stack.Count > 0)
            {
                var current = stack[^1];
                var children = outgoing[current.NodeId];

                if (current.Index < children.Count)
                {
                    var child = children[current.Index];
                    stack[^1] = (current.NodeId, current.Index + 1);

                    if (backEdges.Contains((current.NodeId, child)) || visited.Contains(child))
                        continue;

                    visited.Add(child);
                    stack.Add((child, 0));
                }
                else
                {
                    postOrder.Add(current.NodeId);
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        postOrder.Reverse();
        return postOrder;
    }

    private static List<List<string>> FindComponents(
        IReadOnlyList<string> ids,
        Dictionary<string, List<string>> outgoing,
        Dictionary<string, List<string>> incoming)
    {
        var result = new List<List<string>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ids)
        {
            if (!visited.Add(root)) continue;

            var component = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                component.Add(id);

                foreach (var next in outgoing[id].Concat(incoming[id]))
                {
                    if (visited.Add(next)) queue.Enqueue(next);
                }
            }

            result.Add(component);
        }

        return result;
    }
}