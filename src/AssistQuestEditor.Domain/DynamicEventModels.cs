using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

/// <summary>Жизненный цикл экземпляра динамического события.</summary>
public enum DynamicEventInstanceStatus
{
    Spawned,
    Active,
    Discovered,
    Engaged,
    Completed,
    Expired,
    Cancelled,
    Abandoned,
    Failed,
    Consumed
}

/// <summary>
/// Триггер генерации Dynamic Event Definition.
///
/// Type остаётся строкой: базовый Runtime знает несколько стандартных триггеров,
/// а будущий World Provider может добавлять собственные типы без изменения
/// canonical resource.
/// </summary>
public sealed record DynamicEventTriggerDefinition
{
    public string Type { get; init; } = "Manual";

    public double? MinDistanceMeters { get; init; }
    public double? MaxDistanceMeters { get; init; }

    public double? MinGameHours { get; init; }
    public double? MaxGameHours { get; init; }

    public double? MinRealHours { get; init; }
    public double? MaxRealHours { get; init; }

    /// <summary>
    /// DefinitionId события, обнаружение которого запускает этот таймер.
    /// Используется с Type = DynamicEventDiscovery.
    /// </summary>
    public string? SourceDefinitionId { get; init; }

    public string? EventType { get; init; }

    /// <summary>
    /// Для триггера WorldEvent можно потребовать совпадение отдельных payload
    /// полей. Неизвестные поля просто не проходят фильтр.
    /// </summary>
    public IReadOnlyDictionary<string, string> EventPayload { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Политика появления и жизни динамического события.
/// </summary>
public sealed record DynamicEventSpawnPolicy
{
    public int MaxActiveInstances { get; init; } = 1;

    /// <summary>Вероятность генерации после успешного срабатывания триггера, 0..1.</summary>
    public double SpawnChance { get; init; } = 1d;

    public double? CooldownGameHours { get; init; }
    public double? CooldownRealHours { get; init; }

    public double? LifetimeGameHours { get; init; }
    public double? LifetimeRealHours { get; init; }

    /// <summary>
    /// Первый экземпляр создаётся при запуске Симулятора, после чего правило
    /// ждёт обычного триггера. Это позволяет Dynamic Event с триггером
    /// DynamicEventDiscovery иметь начальную точку без отдельного «стартового»
    /// ресурса.
    /// </summary>
    public bool SpawnOnSimulationStart { get; init; }

    /// <summary>
    /// После истечения срока жизни экземпляра сразу разрешить новый экземпляр
    /// через ту же Location. Используется для временных событий вроде тайников.
    /// </summary>
    public bool RespawnOnExpired { get; init; }

    /// <summary>
    /// После завершения экземпляр остаётся в Runtime State до следующего тика,
    /// чтобы UI/журнал успели увидеть итоговое состояние, затем удаляется.
    /// </summary>
    public bool RemoveOnCompleted { get; init; } = true;
}

/// <summary>
/// Каноническое описание правила генерации события.
///
/// Это НЕ конкретный spawned event. Конкретный экземпляр появляется только
/// внутри Runtime после срабатывания триггера и успешного разрешения Location.
/// </summary>
public sealed record DynamicEventDefinition(
    string Id,
    string Name)
{
    public string Description { get; init; } = string.Empty;

    /// <summary>Логический Location, из которого Runtime получает конкретную WorldPoint.</summary>
    public string LocationId { get; init; } = string.Empty;

    /// <summary>
    /// Необязательный QuestId, который может быть активирован после обнаружения/активации.
    /// Сам факт появления Dynamic Event не требует Quest.
    /// </summary>
    public string? QuestId { get; init; }

    public double TriggerRadius { get; init; } = 35;

    /// <summary>
    /// Подпись категории для карты/отладочного представления, не семантика
    /// Location Query.
    /// </summary>
    public string Category { get; init; } = "Dynamic Event";

    public DynamicEventTriggerDefinition Trigger { get; init; } = new();

    public DynamicEventSpawnPolicy SpawnPolicy { get; init; } = new();

    /// <summary>
    /// Поля presentation остаются строками, чтобы не зашивать в Domain
    /// конкретный UI. Примеры: Hidden, Minimap, Ar, WorldMarker.
    /// </summary>
    public IReadOnlyDictionary<string, string> Presentation { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record DynamicEventInstance(
    string InstanceId,
    string DefinitionId,
    DynamicEventInstanceStatus Status,
    WorldPoint Point)
{
    public DateTimeOffset SpawnedUtc { get; init; }
    public TimeSpan SpawnedGameElapsed { get; init; }

    public DateTimeOffset? DiscoveredUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string? LastReason { get; init; }
}

/// <summary>
/// Runtime-память одного правила генерации.
///
/// Для DistanceTravelled хранится накопленный бюджет и следующий случайный
/// порог. Для временных триггеров хранится следующая запланированная отметка.
/// </summary>
public sealed record DynamicEventScheduleState(
    string DefinitionId)
{
    public double DistanceBudgetMeters { get; init; }
    public double? NextDistanceThresholdMeters { get; init; }

    public TimeSpan? NextGameElapsed { get; init; }
    public DateTimeOffset? NextRealUtc { get; init; }

    public DateTimeOffset? LastSpawnUtc { get; init; }
    public TimeSpan? LastSpawnGameElapsed { get; init; }

    public bool TriggerPending { get; init; }
}

/// <summary>
/// Полное состояние Dispatcher, входящее в Simulator/Data Channel и сохранение.
/// </summary>
public sealed record DynamicEventRuntimeState(
    IReadOnlyList<DynamicEventInstance> Instances,
    IReadOnlyList<DynamicEventScheduleState> Schedules)
{
    public static DynamicEventRuntimeState Empty { get; } =
        new(Array.Empty<DynamicEventInstance>(), Array.Empty<DynamicEventScheduleState>());
}

public sealed record DynamicEventDefinitionDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Format,
    [property: JsonPropertyOrder(2)] DynamicEventDefinition Definition);

public static class DynamicEventDefinitionRules
{
    public const string FormatName = "aqevent";
    public const int CurrentSchemaVersion = 1;

    public static bool IsSupportedTrigger(string? triggerType) =>
        triggerType is not null &&
        (triggerType.Equals("Manual", StringComparison.OrdinalIgnoreCase) ||
         triggerType.Equals("DistanceTravelled", StringComparison.OrdinalIgnoreCase) ||
         triggerType.Equals("GameTime", StringComparison.OrdinalIgnoreCase) ||
         triggerType.Equals("RealTime", StringComparison.OrdinalIgnoreCase) ||
         triggerType.Equals("WorldEvent", StringComparison.OrdinalIgnoreCase) ||
         triggerType.Equals("DynamicEventDiscovery", StringComparison.OrdinalIgnoreCase));
}
