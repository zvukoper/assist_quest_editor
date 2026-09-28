using System.Globalization;

namespace AssistQuestEditor.Domain;

public sealed class RoadRoutePlanner
{
    // 8 м ошибочно склеивал короткие и близкие параллельные участки дорог.
    // Для узлов графа это должна быть только почти точная дедупликация концов.
    private const double NodeMergeToleranceMeters = 1d;
    private const double JunctionSnapToleranceMeters = 8d;
    /// <summary>Максимальный восстанавливаемый разрыв дорожной геометрии.</summary>
    private const double RecoveryGapMaxMeters = 300d;
    private const double RecoveryDirectionDot = 0.45d;
    // Мост через пропущенный фрагмент — запасной вариант, а не новая «дорога».
    // Штраф не даёт прямому мосту выигрывать у существующего дорожного пути.
    private const double RecoveryEdgeCostMultiplier = 20d;
    /// <summary>
    /// Радиус повторных раундов сшивки компонент. Штатному восстановлению нужен
    /// весь <see cref="RecoveryGapMaxMeters"/>, а сшивка закрывает реальные стыки
    /// полотна: в данных они 10..25 м, изредка до 60 м. Короткий радиус делает
    /// повторные раунды дешёвыми — площадь просмотра падает вчетверо.
    /// </summary>
    private const double StitchReachMeters = 80d;
    /// <summary>
    /// Дальше этого сшивка компонент не имеет смысла: она не должна сама
    /// придумывать длинные дороги. Настоящие обрывы на стыках — 10..25 м.
    /// </summary>
    private const double StitchCandidateMaxMeters = 60d;
    /// <summary>
    /// Ограничение числа раундов сшивки. Один раунд закрывает почти все стыки,
    /// второй — цепочки «остров → промежуточная компонента → материк».
    /// </summary>
    private const int MaxStitchRounds = 3;
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
    private readonly Dictionary<(int X, int Z), List<int>> _fragmentGrid = new();
    private readonly Dictionary<(int X, int Z), List<JunctionPoint>> _junctionGrid = new();
    /// <summary>
    /// Union-find для сшивки компонент: -1 означает «корень сам по себе».
    /// Родитель хранится числом, а не ссылкой, чтобы не заводить лишних объектов
    /// на 139 000 узлов ради одного прохода.
    /// </summary>
    private readonly List<int> _unionParent = new();
    /// <summary>
    /// Отметки «фрагмент уже просмотрен для этого тупика».
    ///
    /// Заменяет HashSet пар (тупик, фрагмент): тупиков 70 388, а фрагментов вокруг
    /// каждого — сотни, поэтому миллионы вставок в хеш-таблицу заметны на старте.
    /// Фрагменты во время восстановления не добавляются (SplitFragmentAtNode
    /// добавляет только рёбра), поэтому массив можно завести один раз.
    /// </summary>
    private int[] _fragmentStamp = Array.Empty<int>();
    private int _fragmentStampValue;

    private sealed class Node(double x, double z)
    {
        public double X { get; } = x;
        public double Z { get; } = z;
    }

    private readonly record struct Edge(int To, double Cost, bool Recovery);
    private readonly record struct Fragment(RoadSegment Segment, int NodeA, int NodeB);
    /// <summary>Кандидат сшивки: мост от тупика к точке на чужом фрагменте.</summary>
    private readonly record struct Stitch(
        int Source,
        int Fragment,
        double Distance,
        double ProjectionX,
        double ProjectionZ);

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
    /// Строит фиксированную геометрию маршрута.
    ///
    /// Когда известна позиция игрока, fixed-часть начинается не с самой путевой
    /// точки, а с ближайшей точки ДОРОГИ к игроку. Сам игрок в RoutePlan не
    /// попадает: от игрока до этой дорожной точки всегда остаётся один прямой
    /// динамический сегмент. После дорожного якоря маршрут идёт по дорожному
    /// графу к путевым точкам, а отмеченные как «бездорожье» legs идут напрямую.
    ///
    /// Именно эта граница разделяет две механики:
    /// player → road point = dynamic,
    /// road point → waypoint(s) = fixed route.
    /// </summary>
    public RoutePlan Build(RouteState route, WorldCoordinate? playerPosition)
    {
        route = (route ?? RouteState.Empty).Normalize();

        if (route.Waypoints.Count == 0)
            return RoutePlan.Empty;

        var legs = new List<RouteLeg>();
        var errors = new List<string>();

        if (playerPosition is { } player)
        {
            if (IsEmpty)
            {
                return new RoutePlan(
                    Array.Empty<RouteLeg>(),
                    new[]
                    {
                        "Маршрут не построен: дорожная геометрия пуста. " +
                        "Невозможно определить ближайшую к игроку точку дороги для динамического сегмента."
                    },
                    route.Waypoints);
            }

            var playerRoad = ProjectToRoad(player);
            if (playerRoad is null)
            {
                return new RoutePlan(
                    Array.Empty<RouteLeg>(),
                    new[]
                    {
                        "Маршрут не построен: для игрока не найдена дорожная геометрия. " +
                        "Динамический сегмент обязан заканчиваться на точке дороги, а не на путевой точке."
                    },
                    route.Waypoints);
            }

            if (playerRoad.Value.DistanceMeters > MaxRouteSnapDistanceMeters)
            {
                return new RoutePlan(
                    Array.Empty<RouteLeg>(),
                    new[]
                    {
                        "Маршрут не построен: игрок находится в " +
                        $"{F(playerRoad.Value.DistanceMeters)} м от ближайшей дороги, " +
                        $"а допустимое расстояние до дорожного якоря — {MaxRouteSnapDistanceMeters:F0} м. " +
                        $"Ближайшая точка дороги: ({F(playerRoad.Value.Position.X)}, {F(playerRoad.Value.Position.Z)})."
                    },
                    route.Waypoints);
            }

            var first = route.Waypoints[0];
            var firstLeg = first.IsOffRoad
                ? BuildDirectLeg(
                    -1,
                    0,
                    playerRoad.Value.Position,
                    first.Position)
                : BuildLeg(
                    -1,
                    0,
                    "дорожного якоря игрока",
                    "точки 1",
                    playerRoad.Value.Position,
                    first.Position,
                    includeExactStart: false);

            if (firstLeg.Leg is not null)
                legs.Add(firstLeg.Leg);
            else if (!string.IsNullOrWhiteSpace(firstLeg.Error))
                errors.Add(firstLeg.Error);

            if (errors.Count > 0)
                return new RoutePlan(legs, errors, route.Waypoints);
        }
        else if (IsEmpty && route.Waypoints.Any(item => !item.IsOffRoad))
        {
            return new RoutePlan(
                Array.Empty<RouteLeg>(),
                new[]
                {
                    "Маршрут не построен: дорожная геометрия пуста. " +
                    "Файл data/world/roads.json не загружен или не содержит отрезков."
                },
                route.Waypoints);
        }
        else if (route.Waypoints.Count == 1)
        {
            // Legacy/domain-only вызов без позиции игрока.
            legs.Add(new RouteLeg(
                0,
                0,
                new[] { route.Waypoints[0].Position },
                0d));

            return new RoutePlan(legs, errors, route.Waypoints);
        }

        // Остальная fixed-часть начинается уже от путевых точек. Это важно:
        // player не участвует в этих legs, он существует только в dynamic segment.
        for (var index = 0; index < route.Waypoints.Count - 1; index++)
        {
            var start = route.Waypoints[index];
            var end = route.Waypoints[index + 1];
            var result = end.IsOffRoad
                ? BuildDirectLeg(index, index + 1, start.Position, end.Position)
                : BuildLeg(
                    index,
                    index + 1,
                    "точки " + (index + 1),
                    "точки " + (index + 2),
                    start.Position,
                    end.Position,
                    includeExactStart: false,
                    allowStartOffRoad: start.IsOffRoad);

            if (result.Leg is not null)
                legs.Add(result.Leg);
            else if (!string.IsNullOrWhiteSpace(result.Error))
                errors.Add(result.Error);
        }

        return new RoutePlan(legs, errors, route.Waypoints);
    }

    /// <summary>
    /// Числа в сообщениях об ошибках обязаны печататься с точкой как разделителем.
    /// Текст показывается в панели Симулятора и попадает в журнал, а интерполяция
    /// по умолчанию берёт текущую локаль: при русской получалось «1000,0», и
    /// координаты из сообщения нельзя было сверить с данными маршрута.
    /// </summary>
    private static string F(double value, int decimals = 1) =>
        value.ToString("F" + decimals, CultureInfo.InvariantCulture);

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
                var fragmentIndex = _fragments.Count;
                _fragments.Add(new Fragment(segment, a, b));
                RegisterFragment(fragmentIndex, segment);
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

    /// <summary>
    /// Восстанавливает разрывы дорожной геометрии по принципу «ближайшая
    /// пригодная точка дороги впереди», а не «только конечный узел напротив».
    ///
    /// Реальная выгрузка дорог ETS2 содержит короткие фрагменты и разрывы, где
    /// один конец дороги должен попасть в середину другого отрезка. Старый
    /// endpoint→endpoint recovery такого случая не видел и в результате
    /// маршрут мог перескочить напрямик через бездорожье.
    ///
    /// Второй проход — сшивка компонент — добавлен потому, что «ближайшая точка
    /// впереди» почти всегда лежит в СВОЕЙ же компоненте: сеть рассыпана на
    /// десятки тысяч кусков, и вокруг обрыва стоят и свои, и чужие полотна.
    /// На стыке (161176.2, -83177.9) ближайшая точка оказалась в своей
    /// компоненте в 15.3 м, а партнёрский обрыв — в чужой в 15.9 м, и
    /// единственное ребро тратилось на свою. Поэтому после штатного прохода
    /// выполняется сшивка: тупик ищет ближайшую пригодную точку именно ЧУЖОЙ
    /// компоненты (union-find) и соединяется с ней.
    ///
    /// Ремонт не удаляет и не фильтрует обрывы: перекрытые съезды и «тупики»
    /// автор достраивает намеренно, они ценны как квестовые локации. Добавляются
    /// только рёбра-догадки со штрафом <see cref="RecoveryEdgeCostMultiplier"/>,
    /// поэтому A* по-прежнему предпочтёт настоящий объезд.
    /// </summary>
    private void AddDirectionalRecoveryEdges()
    {
        // Направления «наружу» фиксируются ДО постройки мостов: после моста
        // степень тупика уже 2, и направление считалось бы не то, что видит автор.
        var deadEnds = new List<(int NodeId, double OutwardX, double OutwardZ)>();

        for (var nodeId = 0; nodeId < _nodes.Count; nodeId++)
        {
            if (_adjacency[nodeId].Count != 1)
                continue;

            var neighborId = _adjacency[nodeId][0].To;
            var source = _nodes[nodeId];
            var neighbor = _nodes[neighborId];

            var outwardX = source.X - neighbor.X;
            var outwardZ = source.Z - neighbor.Z;
            var outwardLength = Math.Sqrt(
                outwardX * outwardX +
                outwardZ * outwardZ);

            if (outwardLength <= double.Epsilon)
                continue;

            deadEnds.Add((nodeId, outwardX / outwardLength, outwardZ / outwardLength));
        }

        RunDirectionalRecovery(deadEnds, RecoveryGapMaxMeters, collectCandidates: false);
        StitchComponents(deadEnds);
    }

    /// <summary>
    /// Сшивает компоненты связности дорожной сети.
    ///
    /// Фильтр «цель в чужой компоненте» можно применять прямо во время сканирования:
    /// слияние компонент только уменьшает их число, поэтому пара, признанная своей
    /// раньше, никогда не станет чужой позже. Это позволяет не хранить все
    /// кандидаты (их около миллиона), а копить только подходящие — их сотни.
    ///
    /// Кандидаты сортируются по расстоянию и обрабатываются по возрастанию
    /// (как в алгоритме Краскала): если пара уже оказалась в одной компоненте,
    /// ребро не добавляется. Дальний радиус тут не нужен — повторные раунды идут
    /// с <see cref="StitchReachMeters"/> и <see cref="StitchCandidateMaxMeters"/>.
    /// </summary>
    private void StitchComponents(
        IReadOnlyList<(int NodeId, double OutwardX, double OutwardZ)> deadEnds)
    {
        for (var round = 1; round <= MaxStitchRounds; round++)
        {
            // Внутри раунда сначала собираются кандидаты по ВСЕМ тупикам, и только
            // потом ставятся мосты. Если сшивать сразу в том же цикле, мосты,
            // поставленные первым тупиком, испортят кандидатов следующим — сетка
            // фрагментов и компоненты уже изменятся.
            var merged = RunDirectionalRecovery(
                deadEnds,
                StitchReachMeters,
                collectCandidates: true);

            if (merged == 0)
                break;
        }
    }

    /// <summary>
    /// Один раунд прохода по тупикам. Штатный мост выбирается всегда (самый
    /// ближний подходящий во всём радиусе, как раньше), а при
    /// <paramref name="collectCandidates"/> попутно копятся кандидаты, чья цель
    /// лежит в другой компоненте; они и сшиваются по возрастанию расстояния.
    /// Возвращает число сшитых пар.
    /// </summary>
    private int RunDirectionalRecovery(
        IReadOnlyList<(int NodeId, double OutwardX, double OutwardZ)> deadEnds,
        double reachMeters,
        bool collectCandidates)
    {
        _fragmentStamp = new int[_fragments.Count];
        var stitches = new List<Stitch>(capacity: 512);

        foreach (var (sourceId, outwardX, outwardZ) in deadEnds)
        {
            // Степень НЕ перепроверяется: после штатного прохода тупик уже имеет
            // степень 2 (единственное ребро ушло в свою же компоненту), но для
            // сшивки он остаётся тупиком — направление наружу заморожено, и
            // второй мост делает из него нормальную мини-развязку.
            if (sourceId < 0 || sourceId >= _nodes.Count)
                continue;

            var source = _nodes[sourceId];
            _fragmentStampValue++;

            var bestDistance = double.PositiveInfinity;
            var bestFragmentIndex = -1;
            var bestProjectionX = 0d;
            var bestProjectionZ = 0d;

            var reach = reachMeters + GridCellSizeMeters;
            var minCell = CellOf(
                source.X - reach,
                source.Z - reach,
                GridCellSizeMeters);
            var maxCell = CellOf(
                source.X + reach,
                source.Z + reach,
                GridCellSizeMeters);

            var stitchTarget = rootOf(sourceId);
            var lastStitchFragment = -1;

            for (var cellX = minCell.X; cellX <= maxCell.X; cellX++)
            for (var cellZ = minCell.Z; cellZ <= maxCell.Z; cellZ++)
            {
                if (!_fragmentGrid.TryGetValue(
                        (cellX, cellZ),
                        out var candidates))
                    continue;

                foreach (var fragmentIndex in candidates)
                {
                    // Фрагмент лежит в нескольких клетках сетки, поэтому одну и
                    // ту же пару нельзя просматривать дважды.
                    if (_fragmentStamp[fragmentIndex] == _fragmentStampValue)
                        continue;

                    _fragmentStamp[fragmentIndex] = _fragmentStampValue;

                    var fragment = _fragments[fragmentIndex];
                    if (fragment.NodeA == sourceId ||
                        fragment.NodeB == sourceId)
                        continue;

                    var projection = ProjectToSegment(
                        source.X,
                        source.Z,
                        fragment.Segment);

                    var dx = projection.X - source.X;
                    var dz = projection.Z - source.Z;
                    var distance = Math.Sqrt(
                        dx * dx +
                        dz * dz);

                    if (distance <= NodeMergeToleranceMeters ||
                        distance > reachMeters)
                        continue;

                    var distanceDirX = dx / distance;
                    var distanceDirZ = dz / distance;

                    // Точка должна находиться именно «впереди» тупика. Это
                    // отсеивает соединение двух параллельных дорог сбоку.
                    var forwardDot =
                        outwardX * distanceDirX +
                        outwardZ * distanceDirZ;

                    if (forwardDot < RecoveryDirectionDot)
                        continue;

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestFragmentIndex = fragmentIndex;
                        bestProjectionX = projection.X;
                        bestProjectionZ = projection.Z;
                    }

                    if (!collectCandidates ||
                        distance > StitchCandidateMaxMeters)
                        continue;

                    // Куда попадёт мост: ближайший конец фрагмента — так же
                    // вычисляется целевой узел при привязке к дороге.
                    var toA = Distance(_nodes[fragment.NodeA], projection);
                    var toB = Distance(_nodes[fragment.NodeB], projection);
                    var target = toA <= toB ? fragment.NodeA : fragment.NodeB;

                    if (rootOf(target) == stitchTarget)
                        continue;

                    // Один тупик сшивается один раз за раунд: он уже был «своим»
                    // для всех предыдущих кандидатов и останется таким до конца
                    // этого раунда (мосты ставятся после сбора).
                    if (lastStitchFragment == fragmentIndex)
                        continue;

                    lastStitchFragment = fragmentIndex;

                    stitches.Add(new Stitch(
                        sourceId,
                        fragmentIndex,
                        distance,
                        projection.X,
                        projection.Z));
                }
            }

            // Штатный ближайший мост ставится только в первом проходе: в раундах
            // сшивки ближайшая точка уже не нужна, там решает чужая компонента.
            if (!collectCandidates && bestFragmentIndex >= 0)
            {
                var fragmentForBridge = _fragments[bestFragmentIndex];

                var bridgeNode = GetOrCreateNode(
                    bestProjectionX,
                    bestProjectionZ);

                SplitFragmentAtNode(
                    bestFragmentIndex,
                    bridgeNode);

                AddEdge(
                    sourceId,
                    bridgeNode,
                    bestDistance * RecoveryEdgeCostMultiplier,
                    recovery: true);
            }
        }

        if (stitches.Count == 0)
            return 0;

        stitches.Sort((left, right) => left.Distance.CompareTo(right.Distance));

        var merged = 0;

        foreach (var stitch in stitches)
        {
            var fragment = _fragments[stitch.Fragment];

            if (rootOf(stitch.Source) == rootOf(fragment.NodeA))
                continue;
            var bridgeNode = GetOrCreateNode(
                stitch.ProjectionX,
                stitch.ProjectionZ);

            // Порядок важен: сначала расщепление (чтобы мост попал в новый
            // под-фрагмент), потом слияние компонент, потом ребро.
            SplitFragmentAtNode(stitch.Fragment, bridgeNode);

            if (!Union(stitch.Source, bridgeNode))
                continue;

            AddEdge(
                stitch.Source,
                bridgeNode,
                stitch.Distance * RecoveryEdgeCostMultiplier,
                recovery: true);

            merged++;
        }

        return merged;
    }

    private double Distance(Node node, (double X, double Z) point)
    {
        var dx = node.X - point.X;
        var dz = node.Z - point.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private int rootOf(int nodeId)
    {
        if (nodeId < 0 || nodeId >= _unionParent.Count || _unionParent[nodeId] < 0)
            return nodeId;

        var root = nodeId;
        while (_unionParent[root] >= 0)
            root = _unionParent[root];

        // Сжатие путей: цепочки слияний иначе вырождаются в список.
        while (nodeId != root)
        {
            var next = _unionParent[nodeId];
            _unionParent[nodeId] = root;
            nodeId = next;
        }

        return root;
    }

    private bool Union(int a, int b)
    {
        var rootA = rootOf(a);
        var rootB = rootOf(b);

        if (rootA == rootB)
            return false;

        while (_unionParent.Count <= Math.Max(rootA, rootB))
            _unionParent.Add(-1);

        _unionParent[rootA] = rootB;
        return true;
    }

    private void RegisterFragment(
        int fragmentIndex,
        RoadSegment segment)
    {
        var minCell = CellOf(
            Math.Min(segment.X1, segment.X2),
            Math.Min(segment.Z1, segment.Z2),
            GridCellSizeMeters);
        var maxCell = CellOf(
            Math.Max(segment.X1, segment.X2),
            Math.Max(segment.Z1, segment.Z2),
            GridCellSizeMeters);

        for (var x = minCell.X; x <= maxCell.X; x++)
        for (var z = minCell.Z; z <= maxCell.Z; z++)
        {
            if (!_fragmentGrid.TryGetValue((x, z), out var bucket))
            {
                bucket = new List<int>();
                _fragmentGrid[(x, z)] = bucket;
            }

            bucket.Add(fragmentIndex);
        }
    }

    private void SplitFragmentAtNode(
        int fragmentIndex,
        int splitNode)
    {
        var fragment = _fragments[fragmentIndex];
        if (splitNode == fragment.NodeA ||
            splitNode == fragment.NodeB)
            return;

        var nodeA = _nodes[fragment.NodeA];
        var nodeB = _nodes[fragment.NodeB];
        var split = _nodes[splitNode];

        var lengthA = Distance(nodeA, split);
        var lengthB = Distance(split, nodeB);

        if (lengthA > 0.000001d)
            AddEdge(
                fragment.NodeA,
                splitNode,
                lengthA,
                recovery: false);

        if (lengthB > 0.000001d)
            AddEdge(
                splitNode,
                fragment.NodeB,
                lengthB,
                recovery: false);
    }

    private static (double X, double Z) ProjectToSegment(
        double x,
        double z,
        RoadSegment segment)
    {
        var dx = segment.X2 - segment.X1;
        var dz = segment.Z2 - segment.Z1;
        var lengthSquared = dx * dx + dz * dz;

        if (lengthSquared <= double.Epsilon)
            return (segment.X1, segment.Z1);

        var t =
            ((x - segment.X1) * dx +
             (z - segment.Z1) * dz) /
            lengthSquared;

        t = Math.Clamp(t, 0d, 1d);

        return (
            segment.X1 + t * dx,
            segment.Z1 + t * dz);
    }


    /// <summary>Графовые узлы, которыми реально пользуется планировщик.</summary>
    public double[] DebugNodesFlatArray()
    {
        var result = new double[_nodes.Count * 2];
        for (var index = 0; index < _nodes.Count; index++)
        {
            result[index * 2] = _nodes[index].X;
            result[index * 2 + 1] = _nodes[index].Z;
        }

        return result;
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
        bool includeExactStart,
        bool allowStartOffRoad = false)
    {
        var start = ProjectToRoad(startPosition);
        if (start is null)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                "для начальной позиции не найдена дорожная геометрия.");
        }

        if (start.Value.DistanceMeters > MaxRouteSnapDistanceMeters && !allowStartOffRoad)
        {
            return (
                null,
                $"Маршрут от {startLabel} к {endLabel} не построен: " +
                $"{startLabel} ({F(startPosition.X)}, {F(startPosition.Z)}) находится в " +
                $"{F(start.Value.DistanceMeters)} м от ближайшей дороги, " +
                $"а допустимое расстояние привязки — {MaxRouteSnapDistanceMeters:F0} м. " +
                $"Ближайшая привязка: ({F(start.Value.Position.X)}, {F(start.Value.Position.Z)}).");
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
                $"{endLabel} ({F(endPosition.X)}, {F(endPosition.Z)}) находится в " +
                $"{F(end.Value.DistanceMeters)} м от ближайшей дороги, " +
                $"а допустимое расстояние привязки — {MaxRouteSnapDistanceMeters:F0} м. " +
                $"Ближайшая привязка: ({F(end.Value.Position.X)}, {F(end.Value.Position.Z)}).");
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
                $"Начало привязано в ({F(start.Value.Position.X)}, {F(start.Value.Position.Z)}), " +
                $"конец — в ({F(end.Value.Position.X)}, {F(end.Value.Position.Z)})."
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
        // Обычный leg состоит только из точек дорожного графа. Прямая от
        // waypoint до ближайшей дороги допустима исключительно для явно
        // отмеченного «бездорожья».
        if (allowStartOffRoad)
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

    /// <summary>
    /// Прямой leg без дорожной геометрии. Возвращает тот же кортеж, что и
    /// <see cref="BuildLeg"/>, иначе ветви одного условного выражения в <see cref="Build"/>
    /// имеют разные типы (RouteLeg и (RouteLeg?, string?)) и общий тип не выводится.
    /// Ошибки у прямого leg нет: он строится всегда.
    /// </summary>
    private static (RouteLeg? Leg, string? Error) BuildDirectLeg(
        int startWaypointIndex,
        int endWaypointIndex,
        WorldCoordinate start,
        WorldCoordinate end)
    {
        return (
            new RouteLeg(
                startWaypointIndex,
                endWaypointIndex,
                new[] { start, end },
                Distance(start, end)),
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
            // Обе привязки лежат на ОДНОМ под-отрезке дороги: кратчайший путь между
            // ними — прямое движение вдоль него. Без этого ребра A* обязан был сперва
            // дойти до узла графа и вернуться, и маршрут делал «крюк» назад: на прямой
            // 0→250 м путь 50→100 м считался как 50 м к узлу и 100 м обратно к цели
            // (150 м вместо 50 м). Нулевое расстояние пропускаем: полилиния из одной
            // точки непригодна, там остаётся прежний обход через узлы.
            if (SameFragment(start, end) &&
                Distance(start.Position, end.Position) > 0.001d)
            {
                yield return new Edge(
                    virtualEnd,
                    Distance(start.Position, end.Position),
                    false);
            }

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

    /// <summary>
    /// Обе привязки лежат на одном дорожном под-отрезке, то есть между их
    /// узлами есть прямое ребро без промежуточных узлов.
    /// </summary>
    private static bool SameFragment(RoadProjection start, RoadProjection end) =>
        start.NodeA == end.NodeA && start.NodeB == end.NodeB;

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
        Union(a, b);
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
