namespace AssistQuestEditor.Domain;

public sealed class RoadRoutePlanner
{
    private const double NodeMergeToleranceMeters = 8d;
    private const double JunctionSnapToleranceMeters = 8d;
    private const double RecoveryGapMaxMeters = 30d;
    private const double RecoveryDirectionDot = 0.45d;
    private const double GridCellSizeMeters = 100d;

    /// <summary>
    /// Максимальное расстояние от точки авторинга/игрока до оси дороги, при котором
    /// точка автоматически привязывается к дорожному графу. Дальше маршрут почти
    /// наверняка означает ошибочно поставленную точку, и скрытая длинная «прямая»
    /// до дороги только маскировала бы проблему.
    /// </summary>
    public const double MaxRouteSnapDistanceMeters = 100d;
    private const double JunctionGridCellSizeMeters = 250d;

    private readonly List<Node> _nodes = new();
    private readonly List<List<Edge>> _adjacency = new();
    private readonly List<Fragment> _fragments = new();
    private readonly Dictionary<(int X, int Z), List<int>> _nodeGrid = new();
    private readonly Dictionary<(int X, int Z), List<JunctionPoint>> _junctionGrid = new();

    private sealed class Node(double x, double z)
    {
        public double X { get; } = x;
        public double Z { get; } = z;
    }

    private readonly record struct Edge(int To, double Cost, bool Recovery);
    private readonly record struct Fragment(RoadSegment Segment, int NodeA, int NodeB);

    public RoadRoutePlanner(
        IReadOnlyList<RoadSegment> segments,
        IReadOnlyList<JunctionPoint>? junctions = null)
    {
        ArgumentNullException.ThrowIfNull(segments);

        foreach (var junction in junctions ?? Array.Empty<JunctionPoint>())
        {
            if (!double.IsFinite(junction.X) || !double.IsFinite(junction.Z))
                continue;

            var cell = CellOf(junction.X, junction.Z, JunctionGridCellSizeMeters);
            if (!_junctionGrid.TryGetValue(cell, out var bucket))
            {
                bucket = new List<JunctionPoint>();
                _junctionGrid[cell] = bucket;
            }

            bucket.Add(junction);
        }

        BuildGraph(segments);
    }

    public bool IsEmpty => _fragments.Count == 0;

    /// <summary>
    /// Строит маршрут между сохранёнными путевыми точками. Этот overload оставляет
    /// прежний контракт для доменных тестов и инструментов, где уже задана
    /// начальная точка маршрута.
    /// </summary>
    public RoutePlan Build(RouteState route) =>
        Build(route, null);

    /// <summary>
    /// Строит маршрут начиная С ТЕКУЩЕЙ ПОЗИЦИИ ИГРОКА.
    ///
    /// Поэтому маршрут с одной путевой точкой уже является полноценным маршрутом:
    /// первый leg имеет StartWaypointIndex = -1 (игрок) и EndWaypointIndex = 0.
    /// </summary>
    public RoutePlan Build(RouteState route, WorldCoordinate? playerPosition)
    {
        route = (route ?? RouteState.Empty).Normalize();

        if (route.Waypoints.Count == 0)
            return RoutePlan.Empty;

        if (IsEmpty)
        {
            return new RoutePlan(
                Array.Empty<RouteLeg>(),
                new[]
                {
                    "Маршрут не построен: дорожная геометрия пуста. " +
                    "Файл data/world/roads.json не загружен или не содержит отрезков."
                });
        }

        var legs = new List<RouteLeg>();
        var errors = new List<string>();

        if (playerPosition is WorldCoordinate currentPlayer)
        {
            var first = route.Waypoints[0];
            var firstResult = BuildLeg(
                -1,
                0,
                "текущей позиции игрока",
                "точки 1",
                currentPlayer,
                first.Position,
                includeExactStart: true);

            if (firstResult.Leg is not null)
                legs.Add(firstResult.Leg);
            else if (!string.IsNullOrWhiteSpace(firstResult.Error))
                errors.Add(firstResult.Error);
        }
        else if (route.Waypoints.Count == 1)
        {
            errors.Add(
                "Маршрут с одной точкой не построен: для него нужна текущая позиция игрока.");
        }

        for (var index = 0; index < route.Waypoints.Count - 1; index++)
        {
            var start = route.Waypoints[index];
            var end = route.Waypoints[index + 1];
            var result = BuildLeg(
                index,
                index + 1,
                "точки " + (index + 1),
                "точки " + (index + 2),
                start.Position,
                end.Position,
                includeExactStart: false);

            if (result.Leg is not null)
                legs.Add(result.Leg);
            else if (!string.IsNullOrWhiteSpace(result.Error))
                errors.Add(result.Error);
        }

        return new RoutePlan(legs, errors);
    }

    public RoadProjection? ProjectToRoad(WorldCoordinate position)
    {
        var bestDistance = double.PositiveInfinity;
        RoadProjection? best = null;

        foreach (var fragment in _fragments)
        {
            var segment = fragment.Segment;
            var dx = segment.X2 - segment.X1;
            var dz = segment.Z2 - segment.Z1;
            var lengthSquared = dx * dx + dz * dz;
            if (lengthSquared <= double.Epsilon)
                continue;

            var t = ((position.X - segment.X1) * dx + (position.Z - segment.Z1) * dz) /
                    lengthSquared;
            t = Math.Clamp(t, 0d, 1d);

            var px = segment.X1 + t * dx;
            var pz = segment.Z1 + t * dz;
            var distance = Math.Sqrt(
                (position.X - px) * (position.X - px) +
                (position.Z - pz) * (position.Z - pz));

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = new RoadProjection(
                new WorldCoordinate(px, position.Y, pz),
                fragment.NodeA,
                fragment.NodeB,
                distance);
        }

        return best;
    }

    private void BuildGraph(IReadOnlyList<RoadSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (!IsFinite(segment))
                continue;

            var dx = segment.X2 - segment.X1;
            var dz = segment.Z2 - segment.Z1;
            var lengthSquared = dx * dx + dz * dz;
            if (lengthSquared <= double.Epsilon)
                continue;

            var split = new List<(double T, int NodeId)>
            {
                (0d, GetOrCreateNode(segment.X1, segment.Z1)),
                (1d, GetOrCreateNode(segment.X2, segment.Z2))
            };

            foreach (var junction in QueryJunctions(segment))
            {
                var t = ((junction.X - segment.X1) * dx + (junction.Z - segment.Z1) * dz) /
                        lengthSquared;
                if (t <= 0.000001d || t >= 0.999999d)
                    continue;

                split.Add((t, GetOrCreateNode(junction.X, junction.Z)));
            }

            split = split
                .OrderBy(item => item.T)
                .ThenBy(item => item.NodeId)
                .ToList();

            var unique = new List<(double T, int NodeId)>();
            foreach (var item in split)
            {
                if (unique.Count > 0 &&
                    unique[^1].NodeId == item.NodeId)
                    continue;

                unique.Add(item);
            }

            for (var index = 0; index < unique.Count - 1; index++)
            {
                var a = unique[index].NodeId;
                var b = unique[index + 1].NodeId;
                if (a == b)
                    continue;

                var length = Distance(_nodes[a], _nodes[b]);
                if (length <= 0.000001d)
                    continue;

                AddEdge(a, b, length, recovery: false);
                _fragments.Add(new Fragment(segment, a, b));
            }
        }

        AddDirectionalRecoveryEdges();
    }

    private IEnumerable<JunctionPoint> QueryJunctions(RoadSegment segment)
    {
        var minX = Math.Min(segment.X1, segment.X2) - JunctionSnapToleranceMeters;
        var maxX = Math.Max(segment.X1, segment.X2) + JunctionSnapToleranceMeters;
        var minZ = Math.Min(segment.Z1, segment.Z2) - JunctionSnapToleranceMeters;
        var maxZ = Math.Max(segment.Z1, segment.Z2) + JunctionSnapToleranceMeters;

        var minCellX = (int)Math.Floor(minX / JunctionGridCellSizeMeters);
        var maxCellX = (int)Math.Floor(maxX / JunctionGridCellSizeMeters);
        var minCellZ = (int)Math.Floor(minZ / JunctionGridCellSizeMeters);
        var maxCellZ = (int)Math.Floor(maxZ / JunctionGridCellSizeMeters);

        for (var x = minCellX; x <= maxCellX; x++)
        for (var z = minCellZ; z <= maxCellZ; z++)
        {
            if (!_junctionGrid.TryGetValue((x, z), out var bucket))
                continue;

            foreach (var junction in bucket)
            {
                if (DistanceSquaredTo(junction, segment) <=
                    JunctionSnapToleranceMeters * JunctionSnapToleranceMeters)
                    yield return junction;
            }
        }
    }

    private void AddDirectionalRecoveryEdges()
    {
        var degrees = _adjacency.Select(item => item.Count).ToArray();
        var pairs = new HashSet<(int A, int B)>();

        foreach (var entry in _nodeGrid)
        {
            var (cellX, cellZ) = entry.Key;
            var bucket = entry.Value;

            for (var dxCell = -1; dxCell <= 1; dxCell++)
            for (var dzCell = -1; dzCell <= 1; dzCell++)
            {
                if (!_nodeGrid.TryGetValue((cellX + dxCell, cellZ + dzCell), out var otherBucket))
                    continue;

                foreach (var a in bucket)
                foreach (var b in otherBucket)
                {
                    if (a == b)
                        continue;

                    var key = a < b ? (a, b) : (b, a);
                    if (!pairs.Add(key) || HasEdge(a, b))
                        continue;

                    var vx = _nodes[b].X - _nodes[a].X;
                    var vz = _nodes[b].Z - _nodes[a].Z;
                    var distance = Math.Sqrt(vx * vx + vz * vz);
                    if (distance <= NodeMergeToleranceMeters || distance > RecoveryGapMaxMeters)
                        continue;

                    var dirX = vx / distance;
                    var dirZ = vz / distance;
                    if (!CanContinue(degrees, a, dirX, dirZ) ||
                        !CanContinue(degrees, b, -dirX, -dirZ))
                        continue;

                    AddEdge(a, b, distance, recovery: true);
                }
            }
        }
    }

    private bool CanContinue(int[] degrees, int nodeId, double dirX, double dirZ)
    {
        var edges = _adjacency[nodeId];
        if (edges.Count == 0)
            return true;

        if (degrees[nodeId] == 1)
        {
            var neighbor = _nodes[edges[0].To];
            var current = _nodes[nodeId];
            var vx = current.X - neighbor.X;
            var vz = current.Z - neighbor.Z;
            var length = Math.Sqrt(vx * vx + vz * vz);
            if (length <= double.Epsilon)
                return false;

            return (vx / length) * dirX + (vz / length) * dirZ >= RecoveryDirectionDot;
        }

        foreach (var edge in edges)
        {
            var neighbor = _nodes[edge.To];
            var current = _nodes[nodeId];
            var vx = neighbor.X - current.X;
            var vz = neighbor.Z - current.Z;
            var length = Math.Sqrt(vx * vx + vz * vz);
            if (length <= double.Epsilon)
                continue;

            if ((vx / length) * dirX + (vz / length) * dirZ >= RecoveryDirectionDot)
                return true;
        }

        return false;
    }

    private (RouteLeg? Leg, string? Error) BuildLeg(
        int startWaypointIndex,
        int endWaypointIndex,
        string startLabel,
        string endLabel,
        WorldCoordinate startPosition,
        WorldCoordinate endPosition,
        bool includeExactStart)
    {
        var start = ProjectToRoad(startPosition);
        if (start is null)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                "для начальной позиции не найдена дорожная геометрия.");
        }

        if (start.Value.DistanceMeters > MaxRouteSnapDistanceMeters)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                $"{startLabel} находится в {start.Value.DistanceMeters:F1} м от ближайшей дороги, " +
                $"а допустимое расстояние привязки — {MaxRouteSnapDistanceMeters:F0} м. " +
                $"Ближайшая привязка: ({start.Value.Position.X:F1}, {start.Value.Position.Z:F1}).");
        }

        var end = ProjectToRoad(endPosition);
        if (end is null)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                "для конечной позиции не найдена дорожная геометрия.");
        }

        if (end.Value.DistanceMeters > MaxRouteSnapDistanceMeters)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                $"{endLabel} находится в {end.Value.DistanceMeters:F1} м от ближайшей дороги, " +
                $"а допустимое расстояние привязки — {MaxRouteSnapDistanceMeters:F0} м. " +
                $"Ближайшая привязка: ({end.Value.Position.X:F1}, {end.Value.Position.Z:F1}).");
        }

        const int virtualStart = -1;
        const int virtualEnd = -2;

        var distances = new Dictionary<int, double> { [virtualStart] = 0d };
        var previous = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(virtualStart, 0d);
        var visited = new HashSet<int>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
                continue;

            if (current == virtualEnd)
                break;

            foreach (var neighbor in NeighborsWithVirtuals(
                         current,
                         virtualStart,
                         virtualEnd,
                         start.Value,
                         end.Value))
            {
                var candidateDistance = distances[current] + neighbor.Cost;
                if (!distances.TryGetValue(neighbor.To, out var old) ||
                    candidateDistance < old)
                {
                    distances[neighbor.To] = candidateDistance;
                    previous[neighbor.To] = current;
                    var heuristic = neighbor.To < 0
                        ? 0d
                        : Heuristic(neighbor.To, end.Value.Position);
                    queue.Enqueue(neighbor.To, candidateDistance + heuristic);
                }
            }
        }

        if (!distances.ContainsKey(virtualEnd))
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не найден: " +
                "обе позиции успешно привязаны к дороге, но между их дорожными узлами " +
                "нет непрерывного пути в графе. Проверьте разрыв дороги, отсутствующий " +
                "перекрёсток или точку, привязанную к другой изолированной части сети. " +
                $"Начало привязано в ({start.Value.Position.X:F1}, {start.Value.Position.Z:F1}), " +
                $"конец — в ({end.Value.Position.X:F1}, {end.Value.Position.Z:F1})."
            );
        }

        var path = new List<int> { virtualEnd };
        var node = virtualEnd;
        while (previous.TryGetValue(node, out var parent))
        {
            node = parent;
            path.Add(node);
            if (node == virtualStart)
                break;
        }

        if (path[^1] != virtualStart)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                "служебный путь дорожного графа не вернулся к начальной позиции."
            );
        }

        path.Reverse();

        var polyline = new List<WorldCoordinate>();
        if (includeExactStart)
            AddUnique(polyline, startPosition);

        AddUnique(polyline, start.Value.Position);

        foreach (var nodeId in path)
        {
            if (nodeId is virtualStart or virtualEnd)
                continue;

            var graphNode = _nodes[nodeId];
            AddUnique(polyline, new WorldCoordinate(
                graphNode.X,
                startPosition.Y,
                graphNode.Z));
        }

        AddUnique(polyline, end.Value.Position);
        if (polyline.Count < 2)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                "после построения дорожной полилинии осталось меньше двух точек."
            );
        }

        var length = 0d;
        for (var index = 0; index < polyline.Count - 1; index++)
            length += Distance(polyline[index], polyline[index + 1]);

        return (
            new RouteLeg(
                startWaypointIndex,
                endWaypointIndex,
                polyline,
                length),
            null);
    }

    private IEnumerable<Edge> NeighborsWithVirtuals(
        int node,
        int virtualStart,
        int virtualEnd,
        RoadProjection start,
        RoadProjection end)
    {
        if (node == virtualStart)
        {
            yield return new Edge(
                start.NodeA,
                DistanceToNode(start.Position, start.NodeA),
                false);

            if (start.NodeB != start.NodeA)
            {
                yield return new Edge(
                    start.NodeB,
                    DistanceToNode(start.Position, start.NodeB),
                    false);
            }

            yield break;
        }

        if (node == virtualEnd)
            yield break;

        foreach (var edge in _adjacency[node])
            yield return edge;

        if (node == end.NodeA)
        {
            yield return new Edge(
                virtualEnd,
                DistanceToNode(end.Position, node),
                false);
        }

        if (node == end.NodeB && end.NodeB != end.NodeA)
        {
            yield return new Edge(
                virtualEnd,
                DistanceToNode(end.Position, node),
                false);
        }
    }

    private double Heuristic(int nodeId, WorldCoordinate target)
    {
        var node = _nodes[nodeId];
        var dx = node.X - target.X;
        var dz = node.Z - target.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private double DistanceToNode(WorldCoordinate point, int nodeId)
    {
        var node = _nodes[nodeId];
        var dx = point.X - node.X;
        var dz = point.Z - node.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private int GetOrCreateNode(double x, double z)
    {
        var cell = CellOf(x, z, GridCellSizeMeters);

        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (!_nodeGrid.TryGetValue((cell.X + dx, cell.Z + dz), out var bucket))
                continue;

            foreach (var nodeId in bucket)
            {
                var node = _nodes[nodeId];
                var distance = Math.Sqrt(
                    (node.X - x) * (node.X - x) +
                    (node.Z - z) * (node.Z - z));

                if (distance <= NodeMergeToleranceMeters)
                    return nodeId;
            }
        }

        var created = _nodes.Count;
        _nodes.Add(new Node(x, z));
        _adjacency.Add(new List<Edge>());

        if (!_nodeGrid.TryGetValue(cell, out var nodes))
        {
            nodes = new List<int>();
            _nodeGrid[cell] = nodes;
        }

        nodes.Add(created);
        return created;
    }

    private void AddEdge(int a, int b, double cost, bool recovery)
    {
        if (a == b || HasEdge(a, b))
            return;

        _adjacency[a].Add(new Edge(b, cost, recovery));
        _adjacency[b].Add(new Edge(a, cost, recovery));
    }

    private bool HasEdge(int a, int b) =>
        _adjacency[a].Any(edge => edge.To == b);

    private static bool IsFinite(RoadSegment segment) =>
        double.IsFinite(segment.X1) &&
        double.IsFinite(segment.Z1) &&
        double.IsFinite(segment.X2) &&
        double.IsFinite(segment.Z2);

    private static double Distance(Node a, Node b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static double Distance(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static double DistanceSquaredTo(JunctionPoint point, RoadSegment segment)
    {
        var dx = segment.X2 - segment.X1;
        var dz = segment.Z2 - segment.Z1;
        var lengthSquared = dx * dx + dz * dz;

        if (lengthSquared <= double.Epsilon)
        {
            var x = point.X - segment.X1;
            var z = point.Z - segment.Z1;
            return x * x + z * z;
        }

        var t = ((point.X - segment.X1) * dx + (point.Z - segment.Z1) * dz) /
                lengthSquared;
        t = Math.Clamp(t, 0d, 1d);

        var px = segment.X1 + t * dx;
        var pz = segment.Z1 + t * dz;
        var rx = point.X - px;
        var rz = point.Z - pz;
        return rx * rx + rz * rz;
    }

    private static void AddUnique(List<WorldCoordinate> points, WorldCoordinate point)
    {
        if (points.Count == 0 || Distance(points[^1], point) > 0.01d)
            points.Add(point);
    }

    private static (int X, int Z) CellOf(double x, double z, double cellSize) =>
        ((int)Math.Floor(x / cellSize), (int)Math.Floor(z / cellSize));
}
