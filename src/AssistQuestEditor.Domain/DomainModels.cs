using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

public enum QuestStatus
{
    Available,
    Active,
    Completed,
    Cancelled,
    Failed,
    Archived
}

public enum QuestStartMode
{
    Manual,
    Proximity
}

public enum SkillKind
{
    Static,
    Levelled
}

public sealed record QuestActivation(
    QuestStartMode Mode = QuestStartMode.Manual,
    string? WorldPointId = null,
    double Radius = 35,
    string? RequiredReputationNpcId = null,
    int? RequiredReputation = null,
    bool Repeatable = false,
    string? LocationId = null);

public enum SocketDirection
{
    Input,
    Output
}

public enum FlowKind
{
    Normal,
    Cut
}

public sealed record WorldCoordinate(double X, double Y, double Z);

public sealed record WorldPoint(
    string Id,
    string Name,
    string Category,
    WorldCoordinate Position,
    double TriggerRadius = 35)
{
    public bool Editable { get; init; } = true;
    public string Color { get; init; } = "#78c8f0";
    public bool IsCity { get; init; }
}

public sealed record PlayerState(
    WorldCoordinate Position,
    double SpeedKmh,
    double Heading,
    bool Paused,
    bool InCab);

/// <summary>
/// Потребности игрока. Все значения — в ЕДИНИЦАХ шкалы 0..10000
/// (<see cref="PlayerConditionScale"/>), а не в процентах: интерфейс делит их
/// на 100 и показывает проценты, но начисление ведётся единицами, иначе шаг за
/// игровую минуту был бы меньше процента и терялся бы при показе.
/// </summary>
public sealed record PlayerVitalsState(
    double Health,
    double MaxHealth,
    double Energy,
    double MaxEnergy,
    double Hydration,
    double MaxHydration,
    double Fatigue,
    double MaxFatigue)
{
    public double Hygiene { get; init; } = PlayerConditionScale.Maximum;
    public double Resilience { get; init; } =
        PlayerConditionScale.FromPercent(
            CharacterVitalsTuning.DefaultResiliencePercent);
    public double Metabolism { get; init; } =
        PlayerConditionScale.FromPercent(
            CharacterVitalsTuning.DefaultMetabolismPercent);

    /// <summary>
    /// Энергия шкалы в КИЛОКАЛОРИЯХ (задано автором: 0..5000).
    ///
    /// Внутри энергия хранится в единицах 0..10000, как и все шкалы, а наружу —
    /// в калориях: 100% шкалы равно
    /// <see cref="CharacterDigestion.EnergyScaleKilocalories"/> ккал. Пересчёт
    /// живёт в домене, а не в JavaScript: подсказка интерфейса и правила движка
    /// обязаны читать одно и то же число, иначе «съел 400 ккал» в окне не
    /// совпало бы с тем, что начислил движок.
    /// </summary>
    public double EnergyKilocalories =>
        PlayerConditionScale.ToPercent(Energy) /
        100d *
        CharacterDigestion.EnergyScaleKilocalories;

    public double MaxEnergyKilocalories =>
        PlayerConditionScale.ToPercent(MaxEnergy) /
        100d *
        CharacterDigestion.EnergyScaleKilocalories;

    /// <summary>Жидкость шкалы в МИЛЛИЛИТРАХ (задано автором: 0..3000).</summary>
    public double HydrationMilliliters =>
        PlayerConditionScale.ToPercent(Hydration) /
        100d *
        CharacterDigestion.HydrationScaleMilliliters;

    public double MaxHydrationMilliliters =>
        PlayerConditionScale.ToPercent(MaxHydration) /
        100d *
        CharacterDigestion.HydrationScaleMilliliters;

    /// <summary>Полные шкалы и нулевая усталость — стартовое состояние мира.</summary>
    public static PlayerVitalsState Default => new(
        PlayerConditionScale.Maximum,
        PlayerConditionScale.Maximum,
        PlayerConditionScale.Maximum,
        PlayerConditionScale.Maximum,
        PlayerConditionScale.Maximum,
        PlayerConditionScale.Maximum,
        0d,
        PlayerConditionScale.Maximum);

    /// <summary>
    /// Приводит границы и значения к диапазону 0..10000. Одно место для
    /// ограничения: состояние приходит из редактора, нод квестов и сохранений,
    /// и каждый источник обязан получить одинаковый отпор.
    /// </summary>
    public PlayerVitalsState Normalize() => this with
    {
        MaxHealth = ClampMaximum(MaxHealth),
        MaxEnergy = ClampMaximum(MaxEnergy),
        MaxHydration = ClampMaximum(MaxHydration),
        MaxFatigue = ClampMaximum(MaxFatigue),
        Health = ClampValue(Health, MaxHealth),
        Energy = ClampValue(Energy, MaxEnergy),
        Hydration = ClampValue(Hydration, MaxHydration),
        Fatigue = ClampValue(Fatigue, MaxFatigue),
        Hygiene = ClampValue(Hygiene, PlayerConditionScale.Maximum),
        Resilience = ClampValue(Resilience, PlayerConditionScale.Maximum),
        Metabolism = ClampValue(Metabolism, PlayerConditionScale.Maximum)
    };

    private static double ClampMaximum(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, PlayerConditionScale.Maximum) : 0d;

    private static double ClampValue(double value, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, ClampMaximum(maximum)) : 0d;
}

public sealed record PlayerProgressState(
    int Money,
    int Experience,
    int Reserve);

public sealed record WorldState(
    string CoordinateSystem,
    IReadOnlyList<WorldPoint> Points,
    string ActiveLocation);

public sealed record WorldSelectionState(
    WorldPoint? Point,
    string Source);

public sealed record FactState(
    IReadOnlyDictionary<string, string> Values);

public sealed record QuestStatusEntry(
    string QuestId,
    QuestStatus Status,
    string Step);

public sealed record QuestStatusesState(
    IReadOnlyList<QuestStatusEntry> Quests);

public sealed record RuntimeStatesState(
    IReadOnlyDictionary<string, bool> Flags,
    IReadOnlyDictionary<string, string> Variables,
    string? DialogueId,
    string? DialogueAnchor);

public sealed record InterfaceChoiceOption(
    string Id,
    string Text);

public sealed record InterfaceChoiceDialog(
    string RequestId,
    string Title,
    string Speaker,
    string Text,
    IReadOnlyList<InterfaceChoiceOption> Options);

public sealed record InterfaceDialogue(
    string RequestId,
    string Title,
    string Speaker,
    string Text,
    string ButtonText = "Продолжить");

public sealed record InterfaceState(
    InterfaceChoiceDialog? ActiveDialog,
    InterfaceDialogue? ActiveDialogue = null);

public sealed record InventoryState
{
    public IReadOnlyDictionary<string, int> Items { get; }
    public IReadOnlyCollection<string> NewItemIds { get; }

    public InventoryState(
        IReadOnlyDictionary<string, int> items,
        IReadOnlyCollection<string>? newItemIds = null)
    {
        Items = items;
        NewItemIds = newItemIds ?? Array.Empty<string>();
    }
}

public sealed record ItemDefinition(
    string Id,
    string Name,
    string Description,
    string Category,
    string Color);

public sealed record CharacterSkillState(
    string Id,
    string Name,
    string Description,
    bool Unlocked,
    string LevelLabel)
{
    public SkillKind Kind { get; init; } = SkillKind.Static;
    public int Level { get; init; }
    public int MaxLevel { get; init; } = 1;
}

public sealed record CharacterState(
    IReadOnlyDictionary<string, int> Stats,
    IReadOnlyList<CharacterSkillState> Skills,
    IReadOnlyList<string> Buffs,
    IReadOnlyList<string> Debuffs);

public sealed record ActivePlayerEffectState(
    string Id,
    string Name,
    double RemainingRealSeconds,
    bool IsDebuff,
    double ExperienceMultiplier = 1d,
    double StressAccumulationSlowdownPercent = 0d)
{
    public ActivePlayerEffectState Normalize() => this with
    {
        RemainingRealSeconds = Math.Max(
            0d,
            double.IsFinite(RemainingRealSeconds) ? RemainingRealSeconds : 0d),
        ExperienceMultiplier = Math.Max(
            1d,
            double.IsFinite(ExperienceMultiplier) ? ExperienceMultiplier : 1d),
        StressAccumulationSlowdownPercent = Math.Clamp(
            double.IsFinite(StressAccumulationSlowdownPercent)
                ? StressAccumulationSlowdownPercent
                : 0d,
            0d,
            100d)
    };
}

public sealed record PlayerConditionState(
    double CumulativeHealth,
    double CumulativeEnergy,
    double CumulativeHydration,
    double Stress,
    double CumulativeStress,
    double CumulativeFatigue,
    double CriticalFatigueGameSeconds,
    double CriticalStressGameSeconds,
    IReadOnlyList<ActivePlayerEffectState> Effects)
{
    public double OverchargeHealth { get; init; }
    public double OverchargeEnergy { get; init; }
    public double OverchargeHydration { get; init; }
    public double OverchargeFatigue { get; init; }
    public double OverchargeStress { get; init; }

    public IReadOnlyDictionary<string, int> ItemUseCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double SkinIssuesGameSeconds { get; init; }
    public long RandomSequence { get; init; }

    /// <summary>
    /// Накопитель ДРОБНЫХ порций броска устойчивости: сколько «десятиминутных
    /// порций» штрафа ещё не разыграно.
    ///
    /// Нужен потому, что шаг симуляции (250 мс) много меньше порции (10 игровых
    /// минут): без накопителя бросок делался бы четыре раза в секунду по
    /// дробной доле, и заданное «R/2 процентов шанса» превратилось бы в
    /// случайность с совсем другой вероятностью. Хранится в сохранении: иначе
    /// перезапуск обнулял бы уже накопленную долю порции.
    /// </summary>
    public double ExhaustionRollPending { get; init; }
    public double CaffeineLoadMg { get; init; }
    public double CaffeineDailyMg { get; init; }
    public double CaffeineDaySeconds { get; init; }
    public double CaffeineDependence { get; init; }

    /// <summary>
    /// Id ПОСЛЕДНЕГО употреблённого предмета (расходника).
    ///
    /// Нужен монитору показателей: пока идёт переваривание, он обязан показать
    /// ПУНКТ этого предмета и его воздействие на шкалы, а в желудке хранится
    /// только «сколько осталось» — без имени непонятно, от чего именно.
    /// Хранится в сохранении: загрузка посреди переваривания иначе потеряла бы
    /// подпись пункта, и монитор показывал бы безымянный остаток.
    /// </summary>
    public string LastConsumedItemId { get; init; } = string.Empty;

    /// <summary>
    /// Скрытый «виртуальный желудок»: ещё не усвоенное восстановление энергии и
    /// жидкости. Еда и питьё не восполняют шкалы мгновенно — они перевариваются
    /// (см. <see cref="CharacterDigestion"/>, <c>Character_Vitals.md</c>), и
    /// состояние желудка СОХРАНЯЕТСЯ, иначе загрузка во время переваривания
    /// мгновенно выдала бы остаток.
    /// </summary>
    public StomachContents Stomach { get; init; } = StomachContents.Empty;
    /// <summary>
    /// Кумулятивные шкалы и стресс — в ЕДИНИЦАХ (<see cref="PlayerConditionScale"/>),
    /// как и <see cref="PlayerVitalsState"/>. Исключение — критические счётчики
    /// `Critical*GameSeconds`: это ИГРОВЫЕ СЕКУНДЫ, а не шкала, и они не
    /// переводятся в единицы (иначе час критической усталости стал бы ста часами).
    /// </summary>
    public static PlayerConditionState Empty =>
        new(0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, Array.Empty<ActivePlayerEffectState>());

    public PlayerConditionState Normalize() => this with
    {
        CumulativeHealth = Math.Clamp(CumulativeHealth, 0d, PlayerConditionScale.Maximum),
        CumulativeEnergy = Math.Clamp(CumulativeEnergy, 0d, PlayerConditionScale.Maximum),
        CumulativeHydration = Math.Clamp(CumulativeHydration, 0d, PlayerConditionScale.Maximum),
        Stress = Math.Clamp(
            Stress,
            0d,
            PlayerConditionScale.Maximum -
                Math.Clamp(CumulativeStress, 0d, PlayerConditionScale.Maximum)),
        CumulativeStress = Math.Clamp(CumulativeStress, 0d, PlayerConditionScale.Maximum),
        CumulativeFatigue = Math.Clamp(CumulativeFatigue, 0d, PlayerConditionScale.Maximum),
        OverchargeHealth = NormalizeOvercharge(OverchargeHealth),
        OverchargeEnergy = NormalizeOvercharge(OverchargeEnergy),
        OverchargeHydration = NormalizeOvercharge(OverchargeHydration),
        OverchargeFatigue = NormalizeOvercharge(OverchargeFatigue),
        OverchargeStress = NormalizeOvercharge(OverchargeStress),
        ItemUseCounts = (ItemUseCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => Math.Max(0, pair.Value), StringComparer.OrdinalIgnoreCase),
        SkinIssuesGameSeconds = Math.Max(0d, double.IsFinite(SkinIssuesGameSeconds) ? SkinIssuesGameSeconds : 0d),
        ExhaustionRollPending = Math.Clamp(
            double.IsFinite(ExhaustionRollPending) ? ExhaustionRollPending : 0d,
            0d,
            1d),
        CaffeineLoadMg = NormalizeNonNegative(CaffeineLoadMg),
        CaffeineDailyMg = NormalizeNonNegative(CaffeineDailyMg),
        CaffeineDaySeconds = Math.Max(0d, double.IsFinite(CaffeineDaySeconds) ? CaffeineDaySeconds : 0d),
        CaffeineDependence = Math.Clamp(double.IsFinite(CaffeineDependence) ? CaffeineDependence : 0d, 0d, 100d),
        LastConsumedItemId = string.IsNullOrWhiteSpace(LastConsumedItemId)
            ? string.Empty
            : LastConsumedItemId.Trim(),
        Stomach = (Stomach ?? StomachContents.Empty).Normalize(),
        CriticalFatigueGameSeconds = Math.Max(
            0d,
            double.IsFinite(CriticalFatigueGameSeconds) ? CriticalFatigueGameSeconds : 0d),
        CriticalStressGameSeconds = Math.Max(
            0d,
            double.IsFinite(CriticalStressGameSeconds) ? CriticalStressGameSeconds : 0d),
        Effects = (Effects ?? Array.Empty<ActivePlayerEffectState>())
            .Select(effect => effect.Normalize())
            .Where(effect => effect.RemainingRealSeconds > 0d)
            .ToArray()
    };

    private static double NormalizeOvercharge(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;
}

// ReputationState живёт в NpcReputation.cs: репутация ведётся по НПЦ, а не по
// фракциям, поэтому модель и шкала диапазонов лежат рядом.

public sealed record TelemetryState(
    double SpeedKmh,
    double EngineRpm,
    double Throttle,
    double Brake,
    double Steering,
    double FuelPercent,
    double EngineTemperature,
    double CabinTemperature,
    double DamageCabPercent,
    double DamageEnginePercent,
    double DamageTransmissionPercent,
    double DamageWheelPercent,
    bool HornPressed);

public sealed record EnvironmentState(
    string Weather,
    double RainPercent,
    string GameTime,
    double VisibilityMeters);

public sealed record SystemState(
    bool RuntimeRunning,
    string RuntimeMode,
    string LastEvent,
    string LastTransition);

public sealed record SimulatorEvent(
    string EventType,
    DateTimeOffset Timestamp,
    string Source,
    IReadOnlyDictionary<string, string> Payload);

public sealed record SocketDefinition(
    string SocketId,
    string Name,
    SocketDirection Direction,
    FlowKind FlowKind = FlowKind.Normal);

public sealed record QuestNode(
    string NodeId,
    string NodeType,
    string Title,
    double X,
    double Y,
    IReadOnlyList<SocketDefinition> Sockets)
{
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record QuestConnection(
    string FromNodeId,
    string FromSocketId,
    string ToNodeId,
    string ToSocketId);

public sealed record QuestGraph(
    string Id,
    string Name,
    IReadOnlyList<QuestNode> Nodes,
    IReadOnlyList<QuestConnection> Connections);

public sealed record QuestDefinition(
    string Id,
    string Title,
    string Description,
    QuestGraph Graph,
    IReadOnlyList<string> SceneIds,
    QuestActivation? Activation = null,
    int Version = 1,
    // Мир и кампания-родители. Записываются в файл, потому что игрок может
    // физически положить квест не в ту кампанию: список покажет оранжевое
    // предупреждение о чужом родителе, но работать не помешает — объект может
    // быть перенесён осознанно (например при подготовке экспорта).
    string? WorldId = null,
    string? CampaignId = null,
    // Авторство и даты создания/изменения.
    ResourceMetadata? Metadata = null);

public sealed record QuestDefinitionDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Format,
    [property: JsonPropertyOrder(2)] QuestDefinition Definition);

public sealed record DataChannelDescriptor(
    string Key,
    string DisplayName,
    string DataKind,
    string Source,
    bool Mutable);

public sealed record SimulatorSnapshot(
    PlayerState Player,
    PlayerVitalsState PlayerVitals,
    PlayerProgressState PlayerProgress,
    CharacterState Character,
    WorldState World,
    WorldSelectionState Selection,
    FactState Facts,
    QuestStatusesState QuestStatuses,
    RuntimeStatesState States,
    InventoryState Inventory,
    ReputationState Reputation,
    TelemetryState Telemetry,
    EnvironmentState Environment,
    WorldClockState Clock,
    SystemState System,
    InterfaceState Interfaces)
{
    /// <summary>
    /// Активные/завершённые runtime-экземпляры динамических событий.
    /// Это состояние мира, а не canonical authoring resource.
    /// </summary>
    public DynamicEventRuntimeState DynamicEvents { get; init; } =
        DynamicEventRuntimeState.Empty;

    public PlayerConditionState Conditions { get; init; } = PlayerConditionState.Empty;
}