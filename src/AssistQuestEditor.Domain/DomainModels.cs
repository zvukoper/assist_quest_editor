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
}

public sealed record PlayerState(
    WorldCoordinate Position,
    double SpeedKmh,
    double Heading,
    bool Paused,
    bool InCab);

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

public sealed record InventoryState(
    IReadOnlyDictionary<string, int> Items);

public sealed record ReputationState(
    IReadOnlyDictionary<string, int> Values);

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
    IReadOnlyList<string> SceneIds);

public sealed record QuestDefinitionDocument(
    int SchemaVersion,
    QuestDefinition Definition);

public sealed record DataChannelDescriptor(
    string Key,
    string DisplayName,
    string DataKind,
    string Source,
    bool Mutable);

public sealed record SimulatorSnapshot(
    PlayerState Player,
    WorldState World,
    WorldSelectionState Selection,
    FactState Facts,
    QuestStatusesState QuestStatuses,
    RuntimeStatesState States,
    InventoryState Inventory,
    ReputationState Reputation,
    TelemetryState Telemetry,
    EnvironmentState Environment,
    SystemState System);
