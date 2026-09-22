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
    bool Repeatable = false);

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

public sealed record PlayerVitalsState(
    double Health,
    double MaxHealth,
    double Energy,
    double MaxEnergy,
    double Hydration,
    double MaxHydration,
    double Fatigue,
    double MaxFatigue);

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
    string Text);

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
    int Version = 1);

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
    InterfaceState Interfaces);