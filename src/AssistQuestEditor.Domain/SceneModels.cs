using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

public enum SceneRuntimeStatus
{
    Stopped,
    Running,
    Waiting,
    Completed,
    Failed
}

public sealed record SceneNode(
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

public sealed record SceneConnection(
    string FromNodeId,
    string FromSocketId,
    string ToNodeId,
    string ToSocketId);

public sealed record SceneGraph(
    string Id,
    string Name,
    IReadOnlyList<SceneNode> Nodes,
    IReadOnlyList<SceneConnection> Connections);

public sealed record SceneDialogue(
    string Id,
    string Speaker,
    string Text);

public sealed record SceneChoiceOption(
    string Id,
    string Text,
    string OutputSocketId)
{
    /// <summary>
    /// Требование к репутации для показа варианта. null — вариант виден всегда.
    ///
    /// Проверяется при построении интерфейсного запроса: недоступный вариант
    /// вообще не попадает в список, а не показывается неактивным. Так требование
    /// ТЗ «возможности просто не видно» выполняется буквально.
    /// </summary>
    public ReputationRequirement? Requirement { get; init; }
}

/// <summary>
/// Порог репутации для показа варианта выбора или ноды.
/// Сравнение идёт по модулю значения по умолчанию: «от 350 и более» — это
/// значение больше либо равно порогу.
/// </summary>
public sealed record ReputationRequirement(
    string NpcId,
    int MinValue);

public sealed record SceneChoice(
    string Id,
    string Title,
    string Speaker,
    string Text,
    IReadOnlyList<SceneChoiceOption> Options);

public sealed record SceneDefinition(
    string Id,
    string Title,
    string Description,
    SceneGraph Graph,
    IReadOnlyList<SceneDialogue> Dialogues,
    IReadOnlyList<SceneChoice> Choices);

public sealed record SceneDefinitionDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Format,
    [property: JsonPropertyOrder(2)] SceneDefinition Definition);

public sealed record SceneRuntimeState(
    string? SceneId,
    string? CurrentNodeId,
    SceneRuntimeStatus Status,
    string? WaitingFor,
    string? LastChoiceId,
    string LastEvent,
    string LastTransition);

public sealed record SceneRuntimeEvent(
    string EventType,
    DateTimeOffset Timestamp,
    string SceneId,
    string? NodeId,
    string? ChoiceId,
    string Message);
