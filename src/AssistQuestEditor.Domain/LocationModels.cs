namespace AssistQuestEditor.Domain;

public enum LocationMode
{
    Fixed,
    Dynamic
}

/// <summary>
/// Каноническое описание логической локации.
///
/// Location намеренно отделён от WorldPoint: WorldPoint — конкретный объект
/// внешнего World Provider, а Location — авторская семантика, которая может
/// ссылаться на фиксированную точку или каждый раз находить новую точку по
/// критериям.
/// </summary>
public sealed record LocationDefinition(
    string Id,
    string Name)
{
    public string Description { get; init; } = string.Empty;
    public LocationMode Mode { get; init; } = LocationMode.Fixed;
    public string? WorldPointId { get; init; }
    public double TriggerRadius { get; init; } = 35;
    public LocationQueryDefinition Query { get; init; } = new();
}

/// <summary>
/// Пространственный запрос.
///
/// Это НЕ общий Condition Tree. Здесь описывается генерация кандидатов на карте.
/// Поле Type является строковым идентификатором, чтобы новые возможности
/// конкретного World Provider можно было добавлять без изменения старых файлов.
/// </summary>
public sealed record LocationQueryDefinition
{
    public IReadOnlyList<LocationCriterion> Criteria { get; init; } =
        Array.Empty<LocationCriterion>();

    public LocationHistoryConstraints History { get; init; } = new();
}

/// <summary>Одна атомарная пространственная характеристика кандидата.</summary>
public sealed record LocationCriterion(
    string Type,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool Negate = false)
{
    public IReadOnlyDictionary<string, string> SafeParameters =>
        Parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Ограничения по истории использования кандидата.
///
/// Счётчики относятся к паре (LocationId, CandidateWorldPointId) и живут в
/// состоянии симуляции, а не в каноническом файле локации.
/// </summary>
public sealed record LocationHistoryConstraints
{
    public int? MinVisitCount { get; init; }
    public int? MaxVisitCount { get; init; }
    public int? MaxSelectionCount { get; init; }
    public double? MinGameHoursSinceLastVisit { get; init; }
    public double? MinRealHoursSinceLastVisit { get; init; }
    public double? MinGameHoursSinceLastSelection { get; init; }
    public double? MinRealHoursSinceLastSelection { get; init; }
}

/// <summary>Состояние одного кандидата в истории текущей симуляции.</summary>
public sealed record LocationUsageRecord(
    string LocationId,
    string CandidateId)
{
    public int SelectionCount { get; init; }
    public int VisitCount { get; init; }
    public DateTimeOffset? LastSelectedUtc { get; init; }
    public DateTimeOffset? LastVisitedUtc { get; init; }
    public TimeSpan? LastSelectedGameElapsed { get; init; }
    public TimeSpan? LastVisitedGameElapsed { get; init; }
}

/// <summary>Источник истории, который позднее будет подключён к Runtime/Data Channels.</summary>
public interface ILocationUsageHistory
{
    bool TryGet(string locationId, string candidateId, out LocationUsageRecord record);
}

/// <summary>Разрешает логический Location в конкретную точку текущего мира.</summary>
public interface ILocationResolver
{
    WorldPoint? Resolve(string locationId);

    /// <summary>
    /// Разрешение конкретного runtime-экземпляра.
    ///
    /// Один Dynamic Location может одновременно обслуживать много независимых
    /// событий. resolutionKey разделяет их кэши. Старые реализации, которым
    /// раздельный ключ не нужен, сохраняют прежнее поведение.
    /// </summary>
    WorldPoint? Resolve(string locationId, string? resolutionKey) => Resolve(locationId);

    /// <summary>
    /// Радиус срабатывания из самой Location или null, если он неизвестен.
    ///
    /// Задан методом по умолчанию, чтобы реализации, которым радиуса не нужно
    /// (например, тестовые), не обязаны были его писать: без значения
    /// вызывающий код остаётся на прежнем поведении — radius из ноды.
    /// </summary>
    double? ResolveTriggerRadius(string locationId) => null;
}

/// <summary>
/// Необязательный жизненный цикл кэша разрешённых Dynamic Locations.
/// Runtime может начать новый проход мира без привязки Domain к конкретному
/// способу хранения выбранного кандидата.
/// </summary>
public interface ILocationResolutionSession
{
    void Reset();

    void Invalidate(string locationId);

    /// <summary>
    /// Инвалидирует только один runtime-resolution scope.
    /// </summary>
    void Invalidate(string locationId, string? resolutionKey) => Invalidate(locationId);
}

/// <summary>
/// Канонический файл standalone location resource.
/// </summary>
public sealed record LocationDefinitionDocument(
    int SchemaVersion,
    string Format,
    LocationDefinition Definition);
