namespace AssistQuestEditor.Domain;

/// <summary>
/// Owns the lifecycle of multiple independent Quest Definitions.
///
/// Only one QuestRuntime may actively execute at a time. Other quests remain
/// Available and can be activated by their own QuestActivation policy.
/// This keeps the Quest Graph equivalent to a resource/document, while runtime
/// orchestration decides which resource is currently active.
/// </summary>
public sealed class QuestRuntimeCoordinator : IQuestRuntimeController
{
    private readonly IDataChannelHub _hub;
    private readonly SceneRuntime _sceneRuntime;
    private readonly Func<IReadOnlyList<QuestDefinition>> _definitionsProvider;
    private readonly string _defaultQuestId;

    private QuestRuntime? _activeRuntime;
    private QuestRuntimeState _lastState;
    private readonly Dictionary<string, bool> _activationReady = new(StringComparer.OrdinalIgnoreCase);

    public QuestRuntimeCoordinator(
        IDataChannelHub hub,
        SceneRuntime sceneRuntime,
        Func<IReadOnlyList<QuestDefinition>> definitionsProvider,
        string defaultQuestId)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _sceneRuntime = sceneRuntime ?? throw new ArgumentNullException(nameof(sceneRuntime));
        _definitionsProvider = definitionsProvider ?? throw new ArgumentNullException(nameof(definitionsProvider));

        if (string.IsNullOrWhiteSpace(defaultQuestId))
            throw new ArgumentException("Default QuestId обязателен.", nameof(defaultQuestId));

        _defaultQuestId = defaultQuestId;
        _lastState = new QuestRuntimeState(
            defaultQuestId,
            null,
            QuestRuntimeStatus.Stopped,
            null,
            string.Empty,
            string.Empty);

        _hub.Get<PlayerState>("player").Changed += Player_Changed;
        _hub.Get<ReputationState>("reputation").Changed += Reputation_Changed;

        RefreshQuestStatuses();
    }

    public QuestRuntimeState State => _activeRuntime?.State ?? _lastState;

    public QuestGraph? ActiveGraph => _activeRuntime?.ActiveGraph;

    public event EventHandler<QuestRuntimeEvent>? Published;

    /// <summary>
    /// Manual test/start command: starts the configured main quest even if it was
    /// completed before. The Simulator reset action is the normal way to prepare
    /// a fresh test state.
    /// </summary>
    public void Start() => StartQuest(_defaultQuestId, ignoreLifecycle: true);

    public bool StartQuest(string questId) => StartQuest(questId, ignoreLifecycle: true);

    public void Stop(string reason = "Runtime остановлен")
    {
        if (_activeRuntime is null)
        {
            _lastState = _lastState with
            {
                Status = QuestRuntimeStatus.Stopped,
                WaitingFor = null,
                LastEvent = "RuntimeStopped",
                LastTransition = reason
            };
            return;
        }

        _activeRuntime.Stop(reason);
    }

    public void Reset()
    {
        if (_activeRuntime is not null)
            _activeRuntime.Stop("Сброс Quest Runtime.");

        DisposeActiveRuntime();
        _activationReady.Clear();
        RefreshQuestStatuses(forceAvailable: true);

        _lastState = _lastState with
        {
            Status = QuestRuntimeStatus.Stopped,
            CurrentNodeId = null,
            WaitingFor = null,
            LastEvent = "RuntimeReset",
            LastTransition = "Quest Runtime сброшен."
        };
    }

    public void Tick() => _activeRuntime?.Tick();

    private bool StartQuest(string questId, bool ignoreLifecycle)
    {
        var definitions = SafeLoadDefinitions();
        var definition = definitions.FirstOrDefault(item =>
            item.Id.Equals(questId, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            _lastState = new QuestRuntimeState(
                questId,
                null,
                QuestRuntimeStatus.Failed,
                null,
                "RuntimeFailed",
                "Quest Definition не найден: " + questId);

            PublishSynthetic("RuntimeFailed", null, _lastState.LastTransition);
            return false;
        }

        var status = GetQuestStatus(definition.Id);
        if (!ignoreLifecycle && !CanAutoStart(definition, status))
            return false;

        if (_activeRuntime is not null)
        {
            _activeRuntime.Stop("Запускается другой Quest Runtime.");
            DisposeActiveRuntime();
        }

        _activeRuntime = new QuestRuntime(
            new QuestGraphStore(definition.Graph),
            _hub,
            _sceneRuntime);

        _activeRuntime.Published += ActiveRuntime_Published;
        _lastState = _activeRuntime.State;
        _activeRuntime.Start();

        return _activeRuntime.State.Status is
            QuestRuntimeStatus.Running or
            QuestRuntimeStatus.Waiting or
            QuestRuntimeStatus.Completed;
    }

    private void EvaluateAutomaticStart()
    {
        var runtimeBusy = _activeRuntime is not null &&
            _activeRuntime.State.Status is QuestRuntimeStatus.Running or QuestRuntimeStatus.Waiting;

        foreach (var definition in SafeLoadDefinitions())
        {
            var activation = definition.Activation;
            var readyNow =
                activation is not null &&
                activation.Mode == QuestStartMode.Proximity &&
                !string.IsNullOrWhiteSpace(activation.WorldPointId) &&
                ActivationMatches(activation);

            var wasReady = _activationReady.TryGetValue(definition.Id, out var ready) && ready;
            _activationReady[definition.Id] = readyNow;

            // Updating readiness while another Quest is running is important:
            // leaving a repeatable quest area must arm its next activation edge.
            if (runtimeBusy || !readyNow || wasReady)
                continue;

            var status = GetQuestStatus(definition.Id);
            if (!CanAutoStart(definition, status))
                continue;

            if (StartQuest(definition.Id, ignoreLifecycle: false))
            {
                runtimeBusy = true;
                break;
            }
        }
    }

    private bool ActivationMatches(QuestActivation activation)
    {
        var point = _hub.Get<WorldState>("world").Value.Points.FirstOrDefault(item =>
            item.Id.Equals(activation.WorldPointId, StringComparison.OrdinalIgnoreCase));

        if (point is null)
            return false;

        var player = _hub.Get<PlayerState>("player").Value.Position;
        var dx = player.X - point.Position.X;
        var dy = player.Y - point.Position.Y;
        var dz = player.Z - point.Position.Z;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (distance > Math.Max(0, activation.Radius))
            return false;

        if (!string.IsNullOrWhiteSpace(activation.RequiredReputationNpcId) &&
            activation.RequiredReputation.HasValue &&
            _hub.Get<ReputationState>("reputation").Value.ValueOf(activation.RequiredReputationNpcId) <
            activation.RequiredReputation.Value)
            return false;

        return true;
    }

    private bool CanAutoStart(QuestDefinition definition, QuestStatus status)
    {
        if (status == QuestStatus.Archived)
            return false;

        if (status == QuestStatus.Available)
            return true;

        return definition.Activation?.Repeatable == true &&
               status is QuestStatus.Completed or QuestStatus.Cancelled or QuestStatus.Failed;
    }

    private QuestStatus GetQuestStatus(string questId)
    {
        return _hub.Get<QuestStatusesState>("quest-statuses").Value.Quests
            .FirstOrDefault(item =>
                item.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase))
            ?.Status ?? QuestStatus.Available;
    }

    private void RefreshQuestStatuses(bool forceAvailable = false)
    {
        var current = _hub.Get<QuestStatusesState>("quest-statuses").Value.Quests
            .ToDictionary(item => item.QuestId, item => item, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in SafeLoadDefinitions())
        {
            if (forceAvailable || !current.ContainsKey(definition.Id))
            {
                current[definition.Id] = new QuestStatusEntry(
                    definition.Id,
                    QuestStatus.Available,
                    "available");
            }
        }

        _hub.Get<QuestStatusesState>("quest-statuses").Set(
            current.Values
                .OrderBy(item => item.QuestId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            "QuestRuntimeCoordinator");
    }

    private IReadOnlyList<QuestDefinition> SafeLoadDefinitions()
    {
        try
        {
            return (_definitionsProvider() ?? Array.Empty<QuestDefinition>())
                .Where(item => item is not null)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }
        catch
        {
            return Array.Empty<QuestDefinition>();
        }
    }

    private void ActiveRuntime_Published(object? sender, QuestRuntimeEvent e)
    {
        if (sender is not QuestRuntime runtime)
            return;

        _lastState = runtime.State;
        Published?.Invoke(this, e);

        if (e.EventType.Equals("QuestCompleted", StringComparison.OrdinalIgnoreCase) ||
            e.EventType.Equals("RuntimeFailed", StringComparison.OrdinalIgnoreCase))
        {
            DisposeActiveRuntime();
        }
    }

    private void PublishSynthetic(string eventType, string? nodeId, string message) =>
        Published?.Invoke(
            this,
            new QuestRuntimeEvent(
                eventType,
                DateTimeOffset.UtcNow,
                "QuestRuntimeCoordinator",
                nodeId,
                message));

    private void Player_Changed(object? sender, DataChannelChangedEventArgs<PlayerState> e) =>
        EvaluateAutomaticStart();

    private void Reputation_Changed(object? sender, DataChannelChangedEventArgs<ReputationState> e) =>
        EvaluateAutomaticStart();

    private void DisposeActiveRuntime()
    {
        if (_activeRuntime is null)
            return;

        _activeRuntime.Published -= ActiveRuntime_Published;
        _activeRuntime.Dispose();
        _activeRuntime = null;
    }

    public void Dispose()
    {
        _hub.Get<PlayerState>("player").Changed -= Player_Changed;
        _hub.Get<ReputationState>("reputation").Changed -= Reputation_Changed;
        DisposeActiveRuntime();
    }
}
