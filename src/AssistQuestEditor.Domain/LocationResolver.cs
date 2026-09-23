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
        ILocationUsageHistory? history = null)
    {
        var result = Test(location, worldPoints, 1, history);
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

    public LocationTestResult Test(
        LocationDefinition location,
        IReadOnlyList<WorldPoint> worldPoints,
        int rounds,
        ILocationUsageHistory? history = null)
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

        var candidates = worldPoints.Where(point =>
            MatchesAll(point, criteria, worldPoints, diagnostics)).ToList();

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

        for (var round = 0; round < rounds; round++)
        {
            if (available.Count == 0)
                available = candidates.ToList();

            var index = _random.Next(available.Count);
            var point = available[index];
            available.RemoveAt(index);

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

    private static bool MatchesAll(
        WorldPoint candidate,
        IReadOnlyList<LocationCriterion> criteria,
        IReadOnlyList<WorldPoint> worldPoints,
        ICollection<string> diagnostics)
    {
        foreach (var criterion in criteria)
        {
            var supported = Matches(candidate, criterion, worldPoints, out var result, out var message);
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

            default:
                result = false;
                message = $"Критерий {type} пока не поддерживается текущим Sandbox Provider.";
                return false;
        }
    }

    private static bool MatchesHistory(
        string locationId,
        string candidateId,
        LocationHistoryConstraints? constraints,
        ILocationUsageHistory? history)
    {
        if (!HasHistoryCriteria(constraints))
            return true;

        var hasRecord = history is not null &&
            history.TryGet(locationId, candidateId, out var record);

        if (!hasRecord)
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

    private static bool IsUnsupportedCriterion(LocationCriterion item) => IsUnsupported(item.Type);

    private static bool IsUnsupported(string? type) =>
        string.IsNullOrWhiteSpace(type) ||
        !new[]
        {
            "categoryis", "worldpointcategoryis",
            "categorycontains", "worldpointcategorycontains",
            "namecontains", "worldpointnamecontains",
            "withindistanceofpoint", "fartherthanpoint",
            "excludecategory"
        }.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool TryGetDouble(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out double value) =>
        parameters.TryGetValue(key, out var raw) &&
        double.TryParse(
            raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value) &&
        double.IsFinite(value);

    private static double Distance(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
