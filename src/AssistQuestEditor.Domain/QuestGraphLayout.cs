namespace AssistQuestEditor.Domain;

/// <summary>
/// Иерархическая (слоевая) раскладка Quest Graph.
///
/// Ноды выстраиваются по слоям слева направо в направлении связей, внутри слоя —
/// сверху вниз с вертикальными отступами. Порядок внутри слоя подбирается
/// эвристикой барицентра, чтобы уменьшить пересечения связей.
///
/// Раскладка детерминирована и не зависит от текущих X/Y, поэтому её можно
/// применять к графу, ноды которого были созданы без координат (например,
/// агентом «вслепую»): после неё ноды не накладываются друг на друга.
///
/// Связные компоненты раскладываются независимо и размещаются друг под другом,
/// поэтому одинокие ноды и «оторванные» ветки не пересекаются с основной линией.
/// </summary>
public static class QuestGraphLayout
{
    /// <summary>Ширина ноды. Дублирует <c>GRAPH_NODE_WIDTH</c> из editor.js.</summary>
    public const double NodeWidth = 260;

    /// <summary>Горизонтальный отступ между слоями.</summary>
    public const double HorizontalGap = 140;

    /// <summary>Вертикальный отступ между нодами одного слоя.</summary>
    public const double VerticalGap = 40;

    /// <summary>Отступ между независимыми связными компонентами.</summary>
    public const double ComponentGap = 120;

    /// <summary>Количество проходов барицентра при упорядочивании слоёв.</summary>
    private const int BarycenterPasses = 4;

    public sealed record NodePosition(double X, double Y);

    /// <summary>
    /// Высота ноды по количеству сокетов.
    /// Дублирует <c>graphNodeHeight</c> из editor.js, чтобы ряды не пересекались.
    /// </summary>
    public static double NodeHeight(QuestNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var inputs = node.Sockets.Count(socket => socket.Direction == SocketDirection.Input);
        var outputs = node.Sockets.Count(socket => socket.Direction == SocketDirection.Output);
        return Math.Max(110, 78 + Math.Max(inputs, outputs) * 22);
    }

    /// <summary>
    /// Определяет, выглядит ли текущая раскладка как отсутствующая или явно плохая.
    /// Это guard для импортированных/агентских графов: хороший пользовательский
    /// layout не должен неожиданно перестраиваться.
    /// </summary>
    public static bool IsObviouslyPoor(QuestGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Nodes.Count <= 1)
            return false;

        // Массовое создание без координат обычно даёт одну точку для всех нод.
        var distinctPositions = graph.Nodes
            .Select(node => (X: node.X, Y: node.Y))
            .Distinct()
            .Count();
        if (distinctPositions <= 1)
            return true;

        // Ищем реальные пересечения прямоугольников нод.
        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            var left = graph.Nodes[i];
            var leftRight = left.X + NodeWidth;
            var leftBottom = left.Y + NodeHeight(left);

            for (var j = i + 1; j < graph.Nodes.Count; j++)
            {
                var right = graph.Nodes[j];
                var rightRight = right.X + NodeWidth;
                var rightBottom = right.Y + NodeHeight(right);

                if (left.X < rightRight && leftRight > right.X &&
                    left.Y < rightBottom && leftBottom > right.Y)
                    return true;
            }
        }

        // Несколько нод, сжатых почти в одну область, тоже являются типичным
        // следствием программного создания графа без layout.
        var minX = graph.Nodes.Min(node => node.X);
        var maxX = graph.Nodes.Max(node => node.X);
        var minY = graph.Nodes.Min(node => node.Y);
        var maxY = graph.Nodes.Max(node => node.Y);
        return graph.Nodes.Count >= 3 &&
               maxX - minX < NodeWidth &&
               maxY - minY < VerticalGap * 2;
    }

    /// <summary>
    /// Вычисляет новые координаты для всех нод графа.
    /// Возвращает словарь «NodeId → позиция» без изменения исходного графа.
    /// </summary>
    public static IReadOnlyDictionary<string, NodePosition> Compute(QuestGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var positions = new Dictionary<string, NodePosition>(StringComparer.OrdinalIgnoreCase);
        if (graph.Nodes.Count == 0)
        {
            return positions;
        }

        var nodeIds = new List<string>();
        var heights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var indexOfNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outgoing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var incoming = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Nodes)
        {
            // Малформированный граф может содержать повторяющиеся id: тогда
            // сохраняем первую версию ноды и не падаем.
            if (heights.ContainsKey(node.NodeId))
            {
                continue;
            }

            indexOfNode[node.NodeId] = nodeIds.Count;
            nodeIds.Add(node.NodeId);
            heights[node.NodeId] = NodeHeight(node);
            outgoing[node.NodeId] = new List<string>();
            incoming[node.NodeId] = new List<string>();
        }

        foreach (var connection in graph.Connections)
        {
            if (!outgoing.ContainsKey(connection.FromNodeId) ||
                !outgoing.ContainsKey(connection.ToNodeId))
            {
                continue;
            }

            if (string.Equals(connection.FromNodeId, connection.ToNodeId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (outgoing[connection.FromNodeId].Contains(connection.ToNodeId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            outgoing[connection.FromNodeId].Add(connection.ToNodeId);
            incoming[connection.ToNodeId].Add(connection.FromNodeId);
        }

        var components = FindComponents(nodeIds, outgoing, incoming, indexOfNode);

        var yCursor = 0d;
        foreach (var component in components)
        {
            var local = LayoutComponent(component, outgoing, incoming, heights);
            if (local.Count == 0)
            {
                continue;
            }

            var minY = local.Values.Min(position => position.Y);
            var maxY = local.Values.Max(position => position.Y);
            var shift = yCursor - minY;

            foreach (var (nodeId, position) in local)
            {
                positions[nodeId] = new NodePosition(position.X, position.Y + shift);
            }

            yCursor = maxY + shift + ComponentGap;
        }

        return positions;
    }

    private static Dictionary<string, NodePosition> LayoutComponent(
        IReadOnlyList<string> component,
        Dictionary<string, List<string>> outgoing,
        Dictionary<string, List<string>> incoming,
        Dictionary<string, double> heights)
    {
        var (topologicalOrder, backEdges) = TopologicalOrder(component, outgoing);

        // Рёбра «назад» (замыкающие цикл) исключаются из слоёв и из барицентра:
        // иначе цикл бесконечно увеличивал бы номер слоя.
        var forwardOutgoing = ExcludeBackEdges(outgoing, backEdges, component, true);
        var forwardIncoming = ExcludeBackEdges(incoming, backEdges, component, false);

        var layers = AssignLayers(component, topologicalOrder, forwardOutgoing, heights);
        OrderLayers(layers, forwardOutgoing, forwardIncoming);

        var positions = new Dictionary<string, NodePosition>(StringComparer.OrdinalIgnoreCase);
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            var layer = layers[layerIndex];
            var totalHeight = layer.Sum(nodeId => heights[nodeId]) +
                Math.Max(0, layer.Count - 1) * VerticalGap;
            var y = -totalHeight / 2;
            var x = layerIndex * (NodeWidth + HorizontalGap);

            foreach (var nodeId in layer)
            {
                positions[nodeId] = new NodePosition(x, y);
                y += heights[nodeId] + VerticalGap;
            }
        }

        return positions;
    }

    /// <summary>
    /// Обход в глубину с поиском рёбер «назад» и построением топологического
    /// порядка. Обход итеративный, чтобы не переполнять стек на больших графах.
    /// </summary>
    private static (List<string> Order, HashSet<(string From, string To)> BackEdges) TopologicalOrder(
        IReadOnlyList<string> component,
        Dictionary<string, List<string>> outgoing)
    {
        const int Unvisited = 0;
        const int Visiting = 1;
        const int Visited = 2;

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var postOrder = new List<string>();
        var backEdges = new HashSet<(string From, string To)>();

        foreach (var root in component)
        {
            if (state.TryGetValue(root, out var rootState) && rootState == Visited)
            {
                continue;
            }

            var stack = new List<(string NodeId, int ChildIndex)> { (root, 0) };
            state[root] = Visiting;

            while (stack.Count > 0)
            {
                var (nodeId, childIndex) = stack[^1];
                var children = outgoing[nodeId];

                if (childIndex < children.Count)
                {
                    stack[^1] = (nodeId, childIndex + 1);
                    var child = children[childIndex];
                    var childState = state.TryGetValue(child, out var value) ? value : Unvisited;

                    if (childState == Visiting)
                    {
                        backEdges.Add((nodeId, child));
                    }
                    else if (childState == Unvisited)
                    {
                        state[child] = Visiting;
                        stack.Add((child, 0));
                    }
                }
                else
                {
                    state[nodeId] = Visited;
                    postOrder.Add(nodeId);
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        // Обратный postorder — топологический порядок: все предшественники
        // обрабатываются раньше своих потомков.
        postOrder.Reverse();
        return (postOrder, backEdges);
    }

    private static Dictionary<string, List<string>> ExcludeBackEdges(
        Dictionary<string, List<string>> adjacency,
        HashSet<(string From, string To)> backEdges,
        IReadOnlyList<string> component,
        bool fromIsSource)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var nodeId in component)
        {
            var kept = new List<string>();
            foreach (var neighbour in adjacency[nodeId])
            {
                var edge = fromIsSource ? (nodeId, neighbour) : (neighbour, nodeId);
                if (backEdges.Contains(edge))
                {
                    continue;
                }

                kept.Add(neighbour);
            }

            result[nodeId] = kept;
        }

        return result;
    }

    private static List<List<string>> AssignLayers(
        IReadOnlyList<string> component,
        IReadOnlyList<string> topologicalOrder,
        Dictionary<string, List<string>> forwardOutgoing,
        Dictionary<string, double> heights)
    {
        var layerOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeId in component)
        {
            layerOf[nodeId] = 0;
        }

        // Длиннейший путь от корней: нода оказывается правее всех своих
        // предшественников, даже если до неё есть короткий путь.
        foreach (var nodeId in topologicalOrder)
        {
            foreach (var child in forwardOutgoing[nodeId])
            {
                if (!layerOf.ContainsKey(child))
                {
                    continue;
                }

                var candidate = layerOf[nodeId] + 1;
                if (layerOf[child] < candidate)
                {
                    layerOf[child] = candidate;
                }
            }
        }

        var layerCount = layerOf.Values.DefaultIfEmpty(0).Max() + 1;
        var layers = new List<List<string>>();
        for (var index = 0; index < layerCount; index++)
        {
            layers.Add(new List<string>());
        }

        // Исходный порядок нод сохраняется внутри слоя до упорядочивания.
        foreach (var nodeId in component)
        {
            layers[layerOf[nodeId]].Add(nodeId);
        }

        return layers;
    }

    private static void OrderLayers(
        List<List<string>> layers,
        Dictionary<string, List<string>> forwardOutgoing,
        Dictionary<string, List<string>> forwardIncoming)
    {
        for (var pass = 0; pass < BarycenterPasses; pass++)
        {
            var downward = pass % 2 == 0;

            if (downward)
            {
                for (var index = 1; index < layers.Count; index++)
                {
                    RefineLayerOrder(layers[index], forwardIncoming, layers[index - 1]);
                }
            }
            else
            {
                for (var index = layers.Count - 2; index >= 0; index--)
                {
                    RefineLayerOrder(layers[index], forwardOutgoing, layers[index + 1]);
                }
            }
        }
    }

    /// <summary>
    /// Переупорядочивает слой по среднему индексу соседей в соседнем слое.
    /// Ноды без связей в соседнем слое сохраняют текущий порядок.
    /// </summary>
    private static void RefineLayerOrder(
        List<string> layer,
        Dictionary<string, List<string>> neighbours,
        List<string> referenceLayer)
    {
        var referenceIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < referenceLayer.Count; index++)
        {
            referenceIndex[referenceLayer[index]] = index;
        }

        var keys = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < layer.Count; index++)
        {
            var nodeId = layer[index];
            var neighbourIndexes = new List<int>();

            foreach (var neighbour in neighbours[nodeId])
            {
                if (referenceIndex.TryGetValue(neighbour, out var neighbourIndex))
                {
                    neighbourIndexes.Add(neighbourIndex);
                }
            }

            keys[nodeId] = neighbourIndexes.Count > 0 ? neighbourIndexes.Average() : index;
        }

        // OrderBy в LINQ стабилен, а Index добивает равенство — результат
        // детерминирован при одинаковых входных данных.
        var ordered = layer
            .Select((nodeId, index) => (NodeId: nodeId, Index: index, Key: keys[nodeId]))
            .OrderBy(item => item.Key)
            .ThenBy(item => item.Index)
            .Select(item => item.NodeId)
            .ToList();

        layer.Clear();
        layer.AddRange(ordered);
    }

    private static List<List<string>> FindComponents(
        IReadOnlyList<string> nodeIds,
        Dictionary<string, List<string>> outgoing,
        Dictionary<string, List<string>> incoming,
        Dictionary<string, int> indexOfNode)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<List<string>>();

        foreach (var start in nodeIds)
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var component = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();
                component.Add(nodeId);

                foreach (var neighbour in outgoing[nodeId].Concat(incoming[nodeId]))
                {
                    if (visited.Add(neighbour))
                    {
                        queue.Enqueue(neighbour);
                    }
                }
            }

            component.Sort((left, right) => indexOfNode[left].CompareTo(indexOfNode[right]));
            components.Add(component);
        }

        return components;
    }
}
