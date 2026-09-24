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
    private Func<IReadOnlyList<QuestDefinition>> _definitionsProvider;
    private string _defaultQuestId;
    private readonly ILocationResolver? _locationResolver;

    private QuestRuntime? _activeRuntime;
    private QuestRuntimeState _lastState;
    private readonly Dictionary<string, bool> _activationReady = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledQuestIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _simulationRunning;
    private bool _isPaused;
    private double _simulationSpeed = 1d;
    private DateTimeOffset? _lastClockTick;

    /// <summary>
    /// Допустимый диапазон кратности игрового времени.
    ///
    /// Верхняя граница не декоративная: при 60× мир проходит игровые сутки за
    /// 24 реальных минуты, и это ещё осмысленный сценарий («переждать ночь»).
    /// Ниже 0.1× часы выглядели бы остановившимися, хотя симуляция «идёт».
    /// </summary>
    public const double MinSimulationSpeed = 0.1d;
    public const double MaxSimulationSpeed = 60d;

    public QuestRuntimeCoordinator(
        IDataChannelHub hub,
        SceneRuntime sceneRuntime,
        Func<IReadOnlyList<QuestDefinition>> definitionsProvider,
        string defaultQuestId,
        ILocationResolver? locationResolver = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _sceneRuntime = sceneRuntime ?? throw new ArgumentNullException(nameof(sceneRuntime));
        _definitionsProvider = definitionsProvider ?? throw new ArgumentNullException(nameof(definitionsProvider));

        if (string.IsNullOrWhiteSpace(defaultQuestId))
            throw new ArgumentException("Default QuestId обязателен.", nameof(defaultQuestId));

        _defaultQuestId = defaultQuestId;
        _locationResolver = locationResolver;
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
        InitializeEnabledQuestIds();
    }

    public QuestRuntimeState State => _activeRuntime?.State ?? _lastState;

    public QuestGraph? ActiveGraph => _activeRuntime?.ActiveGraph;

    public bool SimulationRunning => _simulationRunning;

    public bool IsPaused => _isPaused;

    public double SimulationSpeed => _simulationSpeed;

    public IReadOnlyCollection<string> EnabledQuestIds =>
        _enabledQuestIds.ToArray();

    public event EventHandler<QuestRuntimeEvent>? Published;

    /// <summary>
    /// Перепривязывает Runtime к каталогу другого мира без перезапуска приложения.
    /// Старый Runtime-проход сбрасывается, а список доступных квестов перечитывается
    /// из нового CampaignStore. Состояние симуляции после смены мира всегда выключено.
    /// </summary>
    public void RebindDefinitions(
        Func<IReadOnlyList<QuestDefinition>> definitionsProvider,
        string? defaultQuestId = null)
    {
        _definitionsProvider = definitionsProvider ?? throw new ArgumentNullException(nameof(definitionsProvider));

        SetSimulationRunning(false);
        _activeRuntime?.Stop("Смена мира: старый Quest Runtime остановлен.");
        _activeRuntime = null;

        if (!string.IsNullOrWhiteSpace(defaultQuestId))
            _defaultQuestId = defaultQuestId;

        _activationReady.Clear();
        _enabledQuestIds.Clear();
        _lastState = new QuestRuntimeState(
            _defaultQuestId,
            null,
            QuestRuntimeStatus.Stopped,
            null,
            "WorldChanged",
            "Мир переключён.");

        RefreshQuestStatuses();
        InitializeEnabledQuestIds();
        PublishSynthetic("WorldChanged", null, "Каталог Quest Runtime перепривязан к новому миру.");
    }

    /// <summary>
    /// Manual test/start command: starts the configured main quest even if it was
    /// completed before. The Simulator reset action is the normal way to prepare
    /// a fresh test state.
    /// </summary>
    public void Start() => StartQuest(_defaultQuestId, ignoreLifecycle: true);

    public bool StartQuest(string questId) => StartQuest(questId, ignoreLifecycle: true);

    public void SetSimulationRunning(bool running)
    {
        // Флаг паузы снимается ЛЮБЫМ переходом состояния, а не только запуском.
        //
        // Прежде здесь стоял ранний выход «уже выключено — ничего не делаем», и
        // он ломал остановку из паузы: пауза оставляет `_simulationRunning =
        // false`, поэтому нажатие «стоп» не делало НИЧЕГО — мир оставался
        // помеченным паузой, плашка продолжала пульсировать оранжевым, и выйти
        // из этого состояния кнопкой остановки было нельзя.
        //
        // «Пауза» — признак приостановленного мира, и жить она может только
        // рядом с ним: любое явное решение о состоянии её снимает.
        if (_simulationRunning == running)
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            _activeRuntime?.SetSimulationRunning(running);
            SetClockRunning(running);

            PublishSynthetic(
                running ? "SimulationStarted" : "SimulationStopped",
                null,
                running ? "Симуляция запущена." : "Симуляция остановлена.");

            return;
        }

        _simulationRunning = running;
        _isPaused = false;
        _activeRuntime?.SetSimulationRunning(running);
        SetClockRunning(running);

        PublishSynthetic(
            running ? "SimulationStarted" : "SimulationStopped",
            null,
            running ? "Симуляция запущена." : "Симуляция остановлена.");

        if (running)
            EvaluateAutomaticStart();
    }

    /// <summary>
    /// Ставит симуляцию на паузу, не сбрасывая состояние мира.
    ///
    /// Отличие от <see cref="SetSimulationRunning"/>(false) только в признаке
    /// <see cref="IsPaused"/>: само исполнение в обоих случаях останавливается.
    /// Разделять их нужно потому, что кнопка play ведёт себя по-разному —
    /// после паузы она продолжает мир, после полной остановки начинает заново,
    /// — а автосохранение должно срабатывать в обоих случаях.
    /// </summary>
    public void PauseSimulation()
    {
        if (!_simulationRunning)
        {
            // «Пауза» имеет смысл только для реально работающей симуляции.
            // После полной остановки отдельное состояние паузы не должно возникать.
            return;
        }

        _simulationRunning = false;
        _isPaused = true;
        _activeRuntime?.SetSimulationRunning(false);
        SetClockRunning(false);

        PublishSynthetic("SimulationPaused", null, "Симуляция на паузе.");
    }

    /// <summary>Продолжает приостановленную симуляцию.</summary>
    public void ResumeSimulation()
    {
        if (_simulationRunning)
            return;

        if (!_isPaused)
        {
            SetSimulationRunning(true);
            return;
        }

        _simulationRunning = true;
        _isPaused = false;
        // Точка отсчёта часов сбрасывается: иначе прирост времени за время паузы
        // был бы засчитан как игровое время, и мир «прыгнул» бы вперёд ровно на
        // длительность паузы.
        _lastClockTick = null;
        _activeRuntime?.SetSimulationRunning(true);
        SetClockRunning(true);

        PublishSynthetic("SimulationResumed", null, "Симуляция продолжена.");
    }

    public void SetSimulationSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed))
            return;

        _simulationSpeed = Math.Clamp(speed, MinSimulationSpeed, MaxSimulationSpeed);
    }

    /// <summary>
    /// Синхронизирует признак «часы идут» с состоянием симуляции.
    ///
    /// Флаг в канале времени — это ЗЕРКАЛО состояния симуляции, а не отдельное
    /// состояние с собственной жизнью. Раньше он выставлялся только в
    /// <see cref="AdvanceWorldClock"/>, поэтому первое мгновение после запуска
    /// симуляции часы сообщали «пауза» (до первого Tick), а загрузка сохранения
    /// и вовсе гасила флаг принудительно. UI по такому флагу показывал «(пауза)»
    /// при работающей симуляции.
    /// </summary>
    private void SetClockRunning(bool running)
    {
        var channel = _hub.Get<WorldClockState>("sim-time");
        var clock = channel.Value;
        if (clock.Running == running)
            return;

        channel.Set(clock with { Running = running }, "Симуляция");
    }

    public void SetQuestEnabled(string questId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(questId))
            return;

        if (enabled)
            _enabledQuestIds.Add(questId);
        else
            _enabledQuestIds.Remove(questId);

        // Логирование действия выполняет слой приложения: Domain не знает о его
        // логгере и не должен зависеть от него.
        if (!enabled &&
            _activeRuntime is not null &&
            _activeRuntime.State.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase))
        {
            _activeRuntime.Stop("Квест деактивирован в Simulator.");
            DisposeActiveRuntime();
        }

        if (_simulationRunning)
            EvaluateAutomaticStart();
    }

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

    public void Tick()
    {
        if (!_simulationRunning)
            return;

        AdvanceWorldClock();
        _activeRuntime?.Tick();
        EvaluateAutomaticStart();
    }

    /// <summary>
    /// Двигает игровое время вперёд с реальной скоростью.
    ///
    /// Время идёт только при запущенной симуляции: выключенный симулятор —
    /// визуальный инструмент, и его изменения не должны накапливаться. Шаг
    /// считается по реальным часам, а не по числу вызовов Tick: таймер UI
    /// дёргается с плавающим интервалом, и «одно деление = одна секунда» дало
    /// бы ускорение времени при частых перерисовках.
    /// </summary>
    private void AdvanceWorldClock()
    {
        var channel = _hub.Get<WorldClockState>("sim-time");
        var clock = channel.Value;
        var now = DateTimeOffset.UtcNow;

        if (_lastClockTick is { } previous)
        {
            var delta = now - previous;
            // Отрицательная дельта (перевод часов) игнорируется: время мира не
            // должно идти назад.
            if (delta > TimeSpan.Zero)
            {
                // Кратность применяется ТОЛЬКО к игровым часам: она ускоряет мир,
                // а не частоту тиков Runtime. Ускорять сами тики нельзя — они
                // дёргают срабатывание квестов, и «перемотка» превратилась бы в
                // мгновенный проскок всех триггеров подряд.
                clock = clock with { Elapsed = clock.Elapsed + delta * _simulationSpeed };
            }
        }

        _lastClockTick = now;
        // Running не выставляется здесь: он уже зеркалит состояние симуляции
        // (см. SetClockRunning). Иначе признак зависел бы от того, успел ли
        // пройти первый Tick, и часы сообщали бы «пауза» сразу после запуска.
        channel.Set(clock, "Игровое время");
    }

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

        if (!ignoreLifecycle && !_enabledQuestIds.Contains(definition.Id))
            return false;

        if (_activeRuntime is not null)
        {
            _activeRuntime.Stop("Запускается другой Quest Runtime.");
            DisposeActiveRuntime();
        }

        var runtime = new QuestRuntime(
            new QuestGraphStore(definition.Graph),
            _hub,
            _sceneRuntime,
            _locationResolver);

        _activeRuntime = runtime;
        runtime.Published += ActiveRuntime_Published;
        runtime.SetSimulationRunning(_simulationRunning);
        _lastState = runtime.State;
        runtime.Start();

        // Start() может завершить квест сразу: граф, который идёт из Start прямо в
        // End, дойдёт до конца в первом же проходе. Обработчик события в этом
        // случае уже освободил активный runtime, и поле _activeRuntime равно null.
        // Состояние поэтому читается из локальной ссылки, а не из поля.
        return runtime.State.Status is
            QuestRuntimeStatus.Running or
            QuestRuntimeStatus.Waiting or
            QuestRuntimeStatus.Completed;
    }

    private void EvaluateAutomaticStart()
    {
        if (!_simulationRunning)
            return;

        var runtimeBusy = _activeRuntime is not null &&
            _activeRuntime.State.Status is QuestRuntimeStatus.Running or QuestRuntimeStatus.Waiting;

        foreach (var definition in SafeLoadDefinitions())
        {
            var activation = definition.Activation;
            var readyNow =
                activation is not null &&
                activation.Mode == QuestStartMode.Proximity &&
                (!string.IsNullOrWhiteSpace(activation.LocationId) ||
                 !string.IsNullOrWhiteSpace(activation.WorldPointId)) &&
                ActivationMatches(activation);

            var wasReady = _activationReady.TryGetValue(definition.Id, out var ready) && ready;
            _activationReady[definition.Id] = readyNow;

            // Updating readiness while another Quest is running is important:
            // leaving a repeatable quest area must arm its next activation edge.
            if (runtimeBusy || !readyNow || wasReady)
                continue;

            var status = GetQuestStatus(definition.Id);
            if (!_enabledQuestIds.Contains(definition.Id))
                continue;
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
        var point = !string.IsNullOrWhiteSpace(activation.LocationId)
            ? _locationResolver?.Resolve(activation.LocationId)
            : _hub.Get<WorldState>("world").Value.Points.FirstOrDefault(item =>
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

    private void InitializeEnabledQuestIds()
    {
        _enabledQuestIds.Clear();
        foreach (var definition in SafeLoadDefinitions())
        {
            var status = GetQuestStatus(definition.Id);
            if (status != QuestStatus.Archived)
                _enabledQuestIds.Add(definition.Id);
        }
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

        // Канал хранит QuestStatusesState, а не массив: без обёртки Set получает
        // несовместимый тип, и канал нельзя записать.
        _hub.Get<QuestStatusesState>("quest-statuses").Set(
            new QuestStatusesState(
                current.Values
                    .OrderBy(item => item.QuestId, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
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
