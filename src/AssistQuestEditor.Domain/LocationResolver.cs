namespace AssistQuestEditor.Domain;

public sealed record LocationResolution(
    LocationDefinition Location,
    WorldPoint? Point,
    bool Success,
    string Message);

public sealed record LocationTestCandidate(
    string CandidateId,
    string Name,
    string Category,
    WorldCoordinate Position,
    bool Selected,
    string Message);

public sealed record LocationTestResult(
    string LocationId,
    bool Supported,
    int RequestedRounds,
    IReadOnlyList<LocationTestCandidate> Candidates,
    IReadOnlyList<string> Diagnostics)
{
    public int SuccessfulRounds => Candidates.Count(item => item.Selected);
}

/// <summary>
/// Базовый резолвер Location для Sandbox.
///
/// Он намеренно работает только с возможностями, которые уже есть в текущем
/// WorldState. Provider-specific критерии сохраняются в файле, но помечаются как
/// неподдерживаемые до появления соответствующей capability у провайдера.
/// </summary>
public sealed class LocationResolver
{
    private readonly Random _random;

    public LocationResolver(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public LocationResolution Resolve(
        LocationDefinition location,
        IReadOnlyList<WorldPoint> worldPoints,
        WorldCoordinate? playerPosition = null,
        ILocationUsageHistory? history = null,
        RoadIndex? roads = null,
        JunctionIndex? junctions = null)
    {
        var result = Test(location, worldPoints, 1, playerPosition, history, roads, junctions);
        var candidate = result.Candidates.FirstOrDefault();
        if (!result.Supported || candidate is null)
        {
            return new LocationResolution(location, null, false,
                result.Diagnostics.FirstOrDefault() ?? "Подходящая точка не найдена.");
        }

        var point = worldPoints.FirstOrDefault(item =>
            item.Id.Equals(candidate.CandidateId, StringComparison.OrdinalIgnoreCase));

        return point is null
            ? new LocationResolution(location, null, false, "Кандидат исчез из WorldState.")
            : new LocationResolution(location, point, true, "Локация разрешена.");
    }

    /// <summary>
    /// Прогон отбора.
    ///
    /// <paramref name="playerPosition"/> нужен критерию «радиус от игрока»: без
    /// него невозможно сказать, далеко ли точка от игрока. Позиция передаётся
    /// снаружи, потому что игрок не часть мира точек — в Sandbox его состояние
    /// живёт в отдельном канале.
    ///
    /// <paramref name="roads"/> нужен критерию «рядом с дорогой». Дорожная
    /// геометрия передаётся отдельно и НЕ входит в список точек мира: дорог
    /// ~98 000, и в снимке карты они весили бы почти 19 МБ вместо 0.9 МБ.
    ///
    /// <paramref name="junctions"/> нужен критерию «в радиусе от перекрёстка».
    /// Перекрёстки, как и дороги, — отдельный слой: они приходят предпосчитанным
    /// списком (нодировка всей сети — сотни миллисекунд).
    /// </summary>
    public LocationTestResult Test(
        LocationDefinition location,
        IReadOnlyList<WorldPoint> worldPoints,
        int rounds,
        WorldCoordinate? playerPosition = null,
        ILocationUsageHistory? history = null,
        RoadIndex? roads = null,
        JunctionIndex? junctions = null)
    {
        rounds = Math.Clamp(rounds, 1, 128);
        var diagnostics = new List<string>();

        if (location.Mode == LocationMode.Fixed)
        {
            if (string.IsNullOrWhiteSpace(location.WorldPointId))
            {
                return new LocationTestResult(location.Id, false, rounds,
                    Array.Empty<LocationTestCandidate>(),
                    ["Для Fixed Location не задан WorldPointId."]);
            }

            var point = worldPoints.FirstOrDefault(item =>
                item.Id.Equals(location.WorldPointId, StringComparison.OrdinalIgnoreCase));

            if (point is null)
            {
                return new LocationTestResult(location.Id, false, rounds,
                    Array.Empty<LocationTestCandidate>(),
                    [$"WorldPoint «{location.WorldPointId}» не найден в WorldState."]);
            }

            return new LocationTestResult(
                location.Id,
                true,
                rounds,
                Enumerable.Repeat(
                    new LocationTestCandidate(
                        point.Id,
                        point.Name,
                        point.Category,
                        point.Position,
                        true,
                        "Фиксированная точка."),
                    rounds).ToArray(),
                ["Fixed Location: выбор не рандомизируется."]);
        }

        var criteria = location.Query?.Criteria ?? Array.Empty<LocationCriterion>();

        if (criteria.Any(IsUnsupportedCriterion))
        {
            diagnostics.Add("Sandbox не может выполнить один или несколько критериев Location.");
            diagnostics.AddRange(criteria
                .Where(IsUnsupportedCriterion)
                .Select(item => "Не поддерживается: " + item.Type));
            return new LocationTestResult(
                location.Id,
                false,
                rounds,
                Array.Empty<LocationTestCandidate>(),
                diagnostics);
        }

        // Критерий известного типа, но с неполными параметрами (нет категории,
        // радиуса, значения) раньше молча пропускал ВСЕ точки: Matches сообщал
        // «не могу оценить», кандидат не отбраковывался, и запрос с опечаткой в
        // параметре выглядел как «критерий ничего не фильтрует». Теперь это
        // ошибка настройки, о которой сообщается явно — так же, как для
        // неизвестного типа критерия.
        var parameterProblems = criteria
            .Select(CriterionParameterProblem)
            .Where(problem => problem is not null)
            .Select(problem => problem!)
            .ToArray();

        if (parameterProblems.Length > 0)
        {
            diagnostics.Add("Sandbox не может выполнить один или несколько критериев Location.");
            diagnostics.AddRange(parameterProblems);
            return new LocationTestResult(
                location.Id,
                false,
                rounds,
                Array.Empty<LocationTestCandidate>(),
                diagnostics);
        }

        // Позиция игрока проверяется ОДИН раз до фильтрации, а не внутри Matches.
        //
        // Если её проверять в Matches, отсутствие позиции давало бы по диагностике
        // на каждую точку мира: список сообщений забивался бы тысячами одинаковых
        // строк, а внятного объяснения («позиция игрока неизвестна») в начале
        // списка не было бы. Это ошибка окружения, а не свойство точки.
        if (criteria.Any(IsPlayerDistanceCriterion) && playerPosition is null)
        {
            diagnostics.Add(
                "Критерий «Радиус от игрока» не может быть выполнен: позиция игрока неизвестна. " +
                "Запустите симуляцию или задайте позицию игрока на карте Симулятора.");
            return new LocationTestResult(
                location.Id,
                false,
                rounds,
                Array.Empty<LocationTestCandidate>(),
                diagnostics);
        }

        // Дорожная геометрия проверяется один раз до фильтрации — по той же
        // причине, что и позиция игрока: её отсутствие это ошибка окружения, а не
        // свойство точки, и сообщение не должно размножаться на каждую точку.
        if (criteria.Any(IsRoadCriterion) && (roads is null || roads.IsEmpty))
        {
            diagnostics.Add(
                "Критерий «Рядом с дорогой» не может быть выполнен: дорожная геометрия не загружена. " +
                "Проверьте файл data/world/roads.json рядом с приложением.");
            return new LocationTestResult(
                location.Id,
                false,
                rounds,
                Array.Empty<LocationTestCandidate>(),
                diagnostics);
        }

        // Перекрёстки проверяются так же один раз ДО фильтрации: их отсутствие —
        // ошибка окружения, а не свойство точки, и сообщение не должно
        // размножаться на каждую точку мира.
        if (criteria.Any(IsJunctionCriterion) && (junctions is null || junctions.IsEmpty))
        {
            diagnostics.Add(
                "Критерий «В радиусе от перекрёстка» не может быть выполнен: " +
                "список перекрёстков не загружен. Проверьте файл data/world/junctions.json рядом с приложением.");
            return new LocationTestResult(
                location.Id,
                false,
                rounds,
                Array.Empty<LocationTestCandidate>(),
                diagnostics);
        }

        // Индекс строится один раз: критерии «рядом есть категория» и «нет категории
        // в радиусе» просматривают окрестность каждой точки, а точек в мире тысячи.
        // Без индекса это был бы полный перебор по всем парам (5192² ≈ 27 млн
        // вычислений расстояния на каждый прогон), с индексом — только по точкам
        // нужной категории.
        var index = new WorldPointIndex(worldPoints);

        var candidates = worldPoints.Where(point =>
            MatchesAll(point, criteria, worldPoints, index, playerPosition, roads, junctions, diagnostics)).ToList();

        candidates = candidates.Where(point =>
            MatchesHistory(location.Id, point.Id, location.Query?.History, history)).ToList();

        if (candidates.Count == 0)
        {
            if (criteria.Any(item => IsUnsupported(item.Type)))
            {
                diagnostics.Add("Часть критериев требует capability текущего World Provider.");
            }

            diagnostics.Add("Подходящих кандидатов нет.");
            return new LocationTestResult(
                location.Id,
                !criteria.Any(IsUnsupportedCriterion) &&
                !HasUnsupportedHistoryCriteria(location.Query?.History),
                rounds,
                Array.Empty<LocationTestCandidate>(),
                diagnostics);
        }

        if (candidates.Count < rounds)
        {
            diagnostics.Add(
                $"Уникальных кандидатов найдено {candidates.Count}; для {rounds} раундов некоторые результаты будут повторяться.");
        }

        var selected = new List<LocationTestCandidate>(rounds);
        var available = candidates.ToList();

        // «Минимальная дистанция между точками» — это НЕ фильтр, а стратегия
        // отбора: критерий не отбраковывает кандидатов, а заставляет раунды
        // расходиться по площади. Поэтому он читается здесь, а не в MatchesAll.
        var minDistance = GetMinDistanceBetweenCandidates(criteria);
        var distanceRelaxed = false;

        for (var round = 0; round < rounds; round++)
        {
            if (available.Count == 0)
                available = candidates.ToList();

            var pick = minDistance is { } meters
                ? SelectFarEnough(available, selected, meters)
                : null;

            if (pick is null && minDistance is { } required)
            {
                // Соблюсти дистанцию невозможно (мало кандидатов). Раунд всё равно
                // выдаётся: пустой результат хуже повтора, но пользователь должен
                // знать, что дистанция не выдержана.
                if (!distanceRelaxed)
                {
                    diagnostics.Add(
                        $"Кандидатов, отстоящих друг от друга на {required:0.#} м, не хватает; " +
                        "часть раундов выбирает точки ближе.");
                    distanceRelaxed = true;
                }
            }

            var point = pick ?? available[_random.Next(available.Count)];
            available.Remove(point);

            selected.Add(new LocationTestCandidate(
                point.Id,
                point.Name,
                point.Category,
                point.Position,
                true,
                "Кандидат прошёл все поддерживаемые критерии."));
        }

        return new LocationTestResult(
            location.Id,
            !criteria.Any(IsUnsupportedCriterion) &&
            !HasUnsupportedHistoryCriteria(location.Query?.History),
            rounds,
            selected,
            diagnostics);
    }

    /// <summary>
    /// Ближайший к запрошенному разбросу кандидат из доступных.
    ///
    /// Возвращает первый кандидат, отстоящий от ВСЕХ уже выбранных не менее чем на
    /// <paramref name="meters"/>, либо null, если такого нет. Выбор случайный, а не
    /// «первый по списку»: иначе точки всегда брались бы с одного конца мира.
    /// </summary>
    private WorldPoint? SelectFarEnough(
        List<WorldPoint> available,
        List<LocationTestCandidate> selected,
        double meters)
    {
        if (selected.Count == 0)
            return available[_random.Next(available.Count)];

        var eligible = available.Where(candidate =>
            selected.All(chosen =>
                Distance(candidate.Position, chosen.Position) >= meters)).ToList();

        return eligible.Count == 0 ? null : eligible[_random.Next(eligible.Count)];
    }

    /// <summary>
    /// Значение критерия «минимальная дистанция между точками» или null.
    ///
    /// Первое найденное значение: несколько таких критериев противоречили бы друг
    /// другу, а молчаливое «побеждает последний» было бы неочевидным.
    /// </summary>
    private static double? GetMinDistanceBetweenCandidates(IReadOnlyList<LocationCriterion> criteria)
    {
        foreach (var criterion in criteria)
        {
            if (!criterion.Type.Trim().Equals(MinDistanceCriterion, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryGetDouble(criterion.SafeParameters, "meters", out var meters))
                return meters;
        }

        return null;
    }

    private static bool MatchesAll(
        WorldPoint candidate,
        IReadOnlyList<LocationCriterion> criteria,
        IReadOnlyList<WorldPoint> worldPoints,
        WorldPointIndex index,
        WorldCoordinate? playerPosition,
        RoadIndex? roads,
        JunctionIndex? junctions,
        ICollection<string> diagnostics)
    {
        foreach (var criterion in criteria)
        {
            // Критерий-стратегия не фильтрует кандидатов, а управляет ВЫБОРОМ
            // раундов, поэтому в фильтрации пропускается.
            //
            // Раньше он доходил до default-ветки Matches и на КАЖДУЮ точку мира
            // добавлял диагностику «пока не поддерживается». Сам отбор дистанции
            // при этом работал, но список диагностик забивался сообщениями об
            // ошибке, и со стороны это выглядело как «минимальная дистанция не
            // работает»: пользователь видел только текст про неподдерживаемый
            // критерий и не видел предупреждения о несоблюдённой дистанции.
            if (IsStrategyCriterion(criterion))
                continue;

            var supported = Matches(candidate, criterion, worldPoints, index, playerPosition, roads, junctions,
                out var result, out var message);
            if (!supported)
            {
                diagnostics.Add(message);
                continue;
            }

            var matches = criterion.Negate ? !result : result;
            if (!matches)
                return false;
        }

        return true;
    }

    private static bool Matches(
        WorldPoint candidate,
        LocationCriterion criterion,
        IReadOnlyList<WorldPoint> worldPoints,
        WorldPointIndex index,
        WorldCoordinate? playerPosition,
        RoadIndex? roads,
        JunctionIndex? junctions,
        out bool result,
        out string message)
    {
        var parameters = criterion.SafeParameters;
        var type = criterion.Type.Trim();

        switch (type.ToLowerInvariant())
        {
            case "categoryis":
            case "worldpointcategoryis":
                result = parameters.TryGetValue("value", out var category) &&
                         candidate.Category.Equals(category, StringComparison.OrdinalIgnoreCase);
                message = string.Empty;
                return parameters.ContainsKey("value");

            case "categorycontains":
            case "worldpointcategorycontains":
                result = parameters.TryGetValue("value", out category) &&
                         candidate.Category.Contains(category, StringComparison.OrdinalIgnoreCase);
                message = string.Empty;
                return parameters.ContainsKey("value");

            case "namecontains":
            case "worldpointnamecontains":
                result = parameters.TryGetValue("value", out var name) &&
                         candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
                message = string.Empty;
                return parameters.ContainsKey("value");

            case "withindistanceofpoint":
            case "fartherthanpoint":
                if (!parameters.TryGetValue("pointId", out var pointId))
                {
                    result = false;
                    message = $"Критерий {type}: не задан pointId.";
                    return false;
                }

                var reference = worldPoints.FirstOrDefault(item =>
                    item.Id.Equals(pointId, StringComparison.OrdinalIgnoreCase));
                if (reference is null)
                {
                    result = false;
                    message = $"Критерий {type}: WorldPoint «{pointId}» не найден.";
                    return false;
                }

                if (!TryGetDouble(parameters, "meters", out var meters))
                {
                    result = false;
                    message = $"Критерий {type}: не задано числовое расстояние meters.";
                    return false;
                }

                var distance = Distance(candidate.Position, reference.Position);
                result = type.Equals("fartherthanpoint", StringComparison.OrdinalIgnoreCase)
                    ? distance >= meters
                    : distance <= meters;
                message = string.Empty;
                return true;

            case "excludecategory":
                result = parameters.TryGetValue("value", out category) &&
                         !candidate.Category.Equals(category, StringComparison.OrdinalIgnoreCase);
                message = string.Empty;
                return parameters.ContainsKey("value");

            // «Рядом с точкой должна быть категория»: кандидат подходит, если в
            // радиусе есть ХОТЯ БЫ одна точка указанной категории. Так ищут
            // «мужчину рядом с котом» или «разбитую машину рядом с магазином».
            case "categorywithinnearby":
            case "nearbycategory":
                return MatchNearbyCategory(candidate, type, parameters, index, expectPresent: true,
                    out result, out message);

            // «В радиусе нет категории»: кандидат подходит, если в радиусе НЕТ ни
            // одной точки указанной категории — отдельно стоящий человек.
            //
            // Отличие от «НЕ» над критерием выше принципиально: «НЕ» инвертирует
            // результат для ОДНОЙ точки, а здесь проверяется отсутствие ВСЕХ
            // подходящих соседей. Инверсия «(есть сосед)» и «(нет соседей)» —
            // это разные условия, когда соседей несколько.
            case "categorynotwithinnearby":
            case "nonearbycategory":
                return MatchNearbyCategory(candidate, type, parameters, index, expectPresent: false,
                    out result, out message);

            // «Радиус от игрока»: точка не ближе первого значения диапазона и не
            // дальше второго — например 100-1000 метров от игрока. Одно число
            // задаёт только минимум («не ближе 150 м»), максимум тогда
            // бесконечен: иначе нельзя было бы искать «что-нибудь подальше».
            case "distancefromplayer":
            case "playerdistance":
                // Проверка нужна компилятору (nullable), но недостижима: отсутствие
                // позиции игрока отсекается ДО фильтрации, поэтому диагностика не
                // может размножиться на каждую точку мира.
                if (playerPosition is not { } player)
                {
                    result = false;
                    message = $"Критерий {type}: позиция игрока неизвестна.";
                    return false;
                }

                if (!parameters.TryGetValue("meters", out var playerRangeText) ||
                    !TryParseDistanceRange(playerRangeText, out var playerRange))
                {
                    result = false;
                    message = $"Критерий {type}: расстояние задаётся числом или диапазоном, например 150 или 100-1000.";
                    return false;
                }

                result = playerRange.Contains(Distance(candidate.Position, player));
                message = string.Empty;
                return true;

            // «Рядом с дорогой»: кандидат не дальше указанного расстояния от
            // любой дороги. Ограничение передаётся в индекс: без него перебор шёл
            // бы по всем 98 000 отрезкам для каждой точки мира.
            //
            // Наличие дорог проверено до фильтрации (см. Test); null-проверка
            // нужна только компилятору и остаётся дешёвой.
            case "nearbyroad":
            case "maxroaddistance":
            {
                if (!TryGetDouble(parameters, "meters", out var roadMeters))
                {
                    result = false;
                    message = $"Критерий {type}: не задано числовое расстояние meters.";
                    return false;
                }

                if (roadMeters <= 0)
                {
                    result = false;
                    message = $"Критерий {type}: расстояние должно быть больше нуля.";
                    return false;
                }

                if (roads is null || roads.IsEmpty)
                {
                    result = false;
                    message = $"Критерий {type}: дорожная геометрия не загружена.";
                    return false;
                }

                var roadDistance = roads.DistanceToNearest(
                    candidate.Position.X, candidate.Position.Z, roadMeters);

                result = roadDistance <= roadMeters;
                message = string.Empty;
                return true;
            }

            // «В радиусе от перекрёстка»: кандидат в заданном диапазоне расстояний
            // до ближайшего перекрёстка. Диапазон, а не только максимум: одним
            // числом задаётся минимум («не ближе 100 м»), двумя через дефис —
            // и минимум, и максимум («не ближе 100 и не дальше 500»).
            //
            // Ближайший перекрёсток ищется в пределах МАКСИМУМА диапазона. Если
            // максимум не задан, радиус поиска берётся с запасом: без ограничения
            // перебор шёл бы по всем 3 483 узлам для каждой точки мира.
            case "nearbyjunction":
            case "junctionradius":
            case "junctiondistance":
            {
                if (!parameters.TryGetValue("meters", out var junctionRangeText) ||
                    !TryParseDistanceRange(junctionRangeText, out var junctionRange))
                {
                    result = false;
                    message = $"Критерий {type}: расстояние задаётся числом или диапазоном, например 300 или 100-500.";
                    return false;
                }

                if (junctions is null || junctions.IsEmpty)
                {
                    result = false;
                    message = $"Критерий {type}: список перекрёстков не загружен.";
                    return false;
                }

                // Когда максимум не задан, поиск ограничивается минимальным
                // расстоянием, ниже которого перекрёсток всё равно не подойдёт:
                // искать ближе смысла нет.
                var searchRadius = junctionRange.Max ?? Math.Max(junctionRange.Min, MaxJunctionSearchRadius);
                var junctionDistance = junctions.DistanceToNearest(
                    candidate.Position.X, candidate.Position.Z, searchRadius);

                result = junctionRange.Contains(junctionDistance);
                message = string.Empty;
                return true;
            }

            default:
                result = false;
                message = $"Критерий {type} пока не поддерживается текущим Sandbox Provider.";
                return false;
        }
    }

    /// <summary>
    /// Критерий-стратегия: он не отбраковывает кандидатов, а влияет на выбор
    /// раундов, поэтому в фильтрации участвовать не должен.
    /// </summary>
    private static bool IsStrategyCriterion(LocationCriterion criterion) =>
        criterion.Type.Trim().Equals(MinDistanceCriterion, StringComparison.OrdinalIgnoreCase);

    private static bool IsRoadCriterion(LocationCriterion criterion)
    {
        var type = criterion.Type.Trim();
        return type.Equals("nearbyroad", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("maxroaddistance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerDistanceCriterion(LocationCriterion criterion)
    {
        var type = criterion.Type.Trim();
        return type.Equals("distancefromplayer", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("playerdistance", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJunctionCriterion(LocationCriterion criterion)
    {
        var type = criterion.Type.Trim();
        return type.Equals("nearbyjunction", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("junctionradius", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("junctiondistance", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Диапазон расстояния от игрока.
    ///
    /// <see cref="Max"/> = null означает «до бесконечности»: автор указал одно
    /// число, и это МИНИМАЛЬНАЯ дистанция («не ближе 150 м»), а не точное
    /// значение. Требовать максимум в этом случае значило бы запретить обычный
    /// сценарий «найти что-нибудь подальше от игрока».
    /// </summary>
    private readonly record struct DistanceRange(double Min, double? Max)
    {
        public bool Contains(double value) =>
            value >= Min && (Max is not { } max || value <= max);
    }

    /// <summary>
    /// Разбирает «100-1000» или «150».
    ///
    /// Дефис как разделитель выбран потому, что именно так диапазон пишут в
    /// текстовых данных; пробелы вокруг значений и дефиса допускаются, чтобы
    /// «100 - 1000» не считалось ошибкой ввода.
    ///
    /// Метод проверяет ТОЛЬКО синтаксис: перепутанные границы (1000-100) — это
    /// синтаксически корректный диапазон, но бессмысленный, и сообщение о нём
    /// должно быть точным. Если бы порядок проверялся здесь, пользователь
    /// получал бы общее «задайте число или диапазон» вместо «минимум больше
    /// максимума».
    /// </summary>
    private static bool TryParseDistanceRange(string? raw, out DistanceRange range)
    {
        range = default;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        var separator = text.IndexOf('-');
        if (separator < 0)
        {
            if (!TryGetNumber(text, out var single) || single < 0)
                return false;

            range = new DistanceRange(single, null);
            return true;
        }

        if (!TryGetNumber(text[..separator], out var min) || min < 0)
            return false;

        if (!TryGetNumber(text[(separator + 1)..], out var max) || max < 0)
            return false;

        range = new DistanceRange(min, max);
        return true;
    }

    private static bool TryGetNumber(string text, out double value)
    {
        value = 0d;
        return double.TryParse(
            text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value) &&
            double.IsFinite(value);
    }

    /// <summary>
    /// Общая проверка соседства по категории.
    ///
    /// <paramref name="expectPresent"/> = true — «рядом есть категория»;
    /// false — «в радиусе нет категории». Радиус обязателен: без него критерий
    /// либо всегда истинен, либо всегда ложен, и это неочевидно пользователю.
    /// </summary>
    private static bool MatchNearbyCategory(
        WorldPoint candidate,
        string type,
        IReadOnlyDictionary<string, string> parameters,
        WorldPointIndex index,
        bool expectPresent,
        out bool result,
        out string message)
    {
        result = false;

        if (!parameters.TryGetValue("value", out var category) || string.IsNullOrWhiteSpace(category))
        {
            message = $"Критерий {type}: не задана категория value.";
            return false;
        }

        if (!TryGetDouble(parameters, "meters", out var meters))
        {
            message = $"Критерий {type}: не задано числовое расстояние meters.";
            return false;
        }

        // Радиус 0 бессмыслен: ни один сосед не может быть «на нулевом расстоянии»,
        // поэтому «рядом есть» всегда ложно, а «рядом нет» — всегда истинно.
        // Это формально верно, но выглядит как сломанный критерий, поэтому радиус
        // требуется строго положительным.
        if (meters <= 0)
        {
            message = $"Критерий {type}: радиус должен быть больше нуля.";
            return false;
        }

        var neighbours = index.ByCategory(category);
        var found = neighbours.Any(neighbour =>
            !ReferenceEquals(neighbour, candidate) &&
            !neighbour.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase) &&
            Distance(candidate.Position, neighbour.Position) <= meters);

        result = expectPresent ? found : !found;
        message = string.Empty;
        return true;
    }

    private static bool MatchesHistory(
        string locationId,
        string candidateId,
        LocationHistoryConstraints? constraints,
        ILocationUsageHistory? history)
    {
        if (!HasHistoryCriteria(constraints))
            return true;

        // out-параметр присваивается только при вызове TryGet, а из-за короткого
        // замыкания && его может не быть вовсе. Неприсвоенный record давал CS0165,
        // поэтому присваиваем явно, когда записи истории нет.
        LocationUsageRecord record;
        if (history is null || !history.TryGet(locationId, candidateId, out record))
        {
            record = new LocationUsageRecord(locationId, candidateId);
        }

        if (constraints?.MinVisitCount is { } minVisits && record.VisitCount < minVisits)
            return false;
        if (constraints?.MaxVisitCount is { } maxVisits && record.VisitCount > maxVisits)
            return false;
        if (constraints?.MaxSelectionCount is { } maxSelections &&
            record.SelectionCount > maxSelections)
            return false;

        var nowUtc = DateTimeOffset.UtcNow;
        if (constraints?.MinRealHoursSinceLastVisit is { } realVisitHours &&
            record.LastVisitedUtc is { } lastVisit &&
            nowUtc - lastVisit < TimeSpan.FromHours(realVisitHours))
            return false;

        if (constraints?.MinRealHoursSinceLastSelection is { } realSelectionHours &&
            record.LastSelectedUtc is { } lastSelection &&
            nowUtc - lastSelection < TimeSpan.FromHours(realSelectionHours))
            return false;

        return true;
    }

    private static bool HasUnsupportedHistoryCriteria(LocationHistoryConstraints? value) =>
        value is not null &&
        (value.MinGameHoursSinceLastVisit.HasValue ||
         value.MinGameHoursSinceLastSelection.HasValue);

    private static bool HasHistoryCriteria(LocationHistoryConstraints? value) =>
        value is not null &&
        (value.MinVisitCount.HasValue ||
         value.MaxVisitCount.HasValue ||
         value.MaxSelectionCount.HasValue ||
         value.MinGameHoursSinceLastVisit.HasValue ||
         value.MinRealHoursSinceLastVisit.HasValue ||
         value.MinGameHoursSinceLastSelection.HasValue ||
         value.MinRealHoursSinceLastSelection.HasValue);

    /// <summary>
    /// Проверяет параметры критериев, добавленных вместе с соседством.
    ///
    /// Зачем отдельная проверка: `MatchesAll` при неоценимом критерии добавляет
    /// диагностику и ПРОПУСКАЕТ точку (не отбраковывает). Для фильтрующих
    /// критериев это терпимо, но у критерия соседства пропущенный радиус означал
    /// бы «условие не проверялось», и запрос вернул бы весь мир — что выглядит
    /// как «критерий ничего не делает».
    ///
    /// Проверяются только НОВЫЕ критерии: менять поведение уже опубликованных
    /// (например `CategoryIs` без значения) в этой правке не следует — на них
    /// может опираться существующий контент.
    /// </summary>
    private static string? CriterionParameterProblem(LocationCriterion criterion)
    {
        var type = criterion.Type.Trim();
        var parameters = criterion.SafeParameters;

        var isNearby = type.Equals("categorywithinnearby", StringComparison.OrdinalIgnoreCase) ||
                       type.Equals("nearbycategory", StringComparison.OrdinalIgnoreCase) ||
                       type.Equals("categorynotwithinnearby", StringComparison.OrdinalIgnoreCase) ||
                       type.Equals("nonearbycategory", StringComparison.OrdinalIgnoreCase);

        if (isNearby)
        {
            if (!parameters.TryGetValue("value", out var category) || string.IsNullOrWhiteSpace(category))
                return $"Критерий {type}: не задана категория value.";

            if (!TryGetDouble(parameters, "meters", out var meters))
                return $"Критерий {type}: не задано числовое расстояние meters.";

            if (meters <= 0)
                return $"Критерий {type}: радиус должен быть больше нуля.";
        }

        if (type.Equals(MinDistanceCriterion, StringComparison.OrdinalIgnoreCase) &&
            !TryGetDouble(parameters, "meters", out _))
        {
            return $"Критерий {type}: не задано числовое расстояние meters.";
        }

        if (IsPlayerDistanceCriterion(criterion))
        {
            if (!parameters.TryGetValue("meters", out var rangeText) ||
                !TryParseDistanceRange(rangeText, out var range))
            {
                return $"Критерий {type}: расстояние задаётся числом или диапазоном, например 150 или 100-1000.";
            }

            // TryParseDistanceRange отвергает только синтаксис. Перепутанные
            // границы (1000-100) синтаксически верны, но бессмысленны, и молча
            // вернуть «ничего не найдено» здесь хуже, чем назвать причину.
            if (range.Max is { } upper && upper < range.Min)
                return $"Критерий {type}: минимум {range.Min:0.#} больше максимума {upper:0.#}.";
        }

        if (IsRoadCriterion(criterion))
        {
            if (!TryGetDouble(parameters, "meters", out var roadMeters))
                return $"Критерий {type}: не задано числовое расстояние meters.";

            // Нулевое расстояние до дороги недостижимо: точка на самой линии
            // встречается практически никогда, и критерий выглядел бы всегда
            // ложным. Это ошибка настройки, а не «ничего не найдено».
            if (roadMeters <= 0)
                return $"Критерий {type}: расстояние до дороги должно быть больше нуля.";
        }

        if (IsJunctionCriterion(criterion))
        {
            if (!parameters.TryGetValue("meters", out var junctionText) ||
                !TryParseDistanceRange(junctionText, out var junctionRange))
            {
                return $"Критерий {type}: расстояние задаётся числом или диапазоном, например 300 или 100-500.";
            }

            // Перепутанные границы (500-100) синтаксически верны, но бессмысленны:
            // молчаливое «ничего не найдено» здесь хуже точного сообщения.
            if (junctionRange.Max is { } junctionUpper && junctionUpper < junctionRange.Min)
                return $"Критерий {type}: минимум {junctionRange.Min:0.#} больше максимума {junctionUpper:0.#}.";
        }

        return null;
    }

    private static bool IsUnsupportedCriterion(LocationCriterion item) => IsUnsupported(item.Type);

    private static bool IsUnsupported(string? type) =>
        string.IsNullOrWhiteSpace(type) ||
        !SupportedCriteria.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Критерий, задающий минимальное расстояние между ВЫБРАННЫМИ точками.
    ///
    /// Он не отбраковывает кандидатов, а управляет разбросом раундов, поэтому
    /// обрабатывается отдельно от остальных критериев.
    /// </summary>
    private const string MinDistanceCriterion = "minDistanceBetweenCandidates";

    private static readonly string[] SupportedCriteria =
    [
        "categoryis", "worldpointcategoryis",
        "categorycontains", "worldpointcategorycontains",
        "namecontains", "worldpointnamecontains",
        "withindistanceofpoint", "fartherthanpoint",
        "excludecategory",
        "categorywithinnearby", "nearbycategory",
        "categorynotwithinnearby", "nonearbycategory",
        "distancefromplayer", "playerdistance",
        "nearbyroad", "maxroaddistance",
        "nearbyjunction", "junctionradius", "junctiondistance",
        MinDistanceCriterion
    ];

    /// <summary>
    /// Запасной радиус поиска перекрёстка, когда критерий задан одним числом.
    ///
    /// Одно число — это МИНИМУМ («не ближе 300 м»), максимум при этом
    /// не ограничен. Искать бесконечно далеко нельзя, но и обрезать слишком
    /// рано — значит терять точки: берётся величина заведомо больше типичного
    /// расстояния до ближайшего узла (медиана по миру ~677 м).
    /// </summary>
    private const double MaxJunctionSearchRadius = 20000d;

    /// <summary>
    /// Индекс мира по категориям.
    ///
    /// Критерии соседства проверяют окрестность КАЖДОГО кандидата, поэтому парный
    /// перебор был бы квадратичным (5192 точки — около 27 млн вычислений
    /// расстояния на прогон). Здесь заранее сгруппированы точки по категории, и
    /// каждая проверка идёт только по точкам искомой категории.
    ///
    /// Неизменяемость намеренна: тест и разрешение не должны менять мир.
    /// </summary>
    private sealed class WorldPointIndex
    {
        private readonly Dictionary<string, IReadOnlyList<WorldPoint>> _byCategory;

        public WorldPointIndex(IReadOnlyList<WorldPoint> points)
        {
            _byCategory = points
                .GroupBy(point => point.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<WorldPoint>)group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Точки указанной категории. Пустая коллекция, если категории нет:
        /// критерий «рядом есть категория» тогда честно не находит соседей, а
        /// «в радиусе нет категории» — честно их не находит тоже.
        /// </summary>
        public IReadOnlyList<WorldPoint> ByCategory(string category) =>
            _byCategory.TryGetValue(category?.Trim() ?? string.Empty, out var points)
                ? points
                : Array.Empty<WorldPoint>();
    }

    private static bool TryGetDouble(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out double value)
    {
        // out-параметр обязан быть присвоен на всех путях выхода, в том числе
        // когда параметра нет в словаре (короткое замыкание && в выражении
        // оставляло value неприсвоенным — CS0177).
        value = 0d;
        return parameters.TryGetValue(key, out var raw) &&
            double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value) &&
            double.IsFinite(value);
    }

    private static double Distance(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
