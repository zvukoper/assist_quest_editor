namespace AssistQuestEditor.Domain;

/// <summary>
/// World-level runtime service, который решает КОГДА и КАК создавать Dynamic Event
/// instances. Он не является Quest Runtime и не меняет canonical Location/Quest
/// definitions.
///
/// Dispatcher работает поверх Data Channels и Event Bus. Поэтому Simulator и
/// будущий игровой адаптер подают в него одинаковое состояние мира.
/// </summary>
public interface IDynamicEventDispatcher : IDisposable
{
    DynamicEventRuntimeState State { get; }

    bool SimulationRunning { get; }

    event Action<SimulatorEvent>? Published;

    void SetSimulationRunning(bool running);
    void Tick();
    void Reset();

    /// <summary>Явно запросить генерацию одного экземпляра для ручной проверки.</summary>
    bool TrySpawn(string definitionId);

    bool Discover(string instanceId);
    bool Engage(string instanceId);
    bool Complete(string instanceId, bool consumed = false);
    bool Cancel(string instanceId, string reason = "Событие отменено.");
    bool Abandon(string instanceId, string reason = "Событие оставлено.");
}

/// <summary>Тип результата одной попытки генерации.</summary>
internal enum DynamicEventSpawnAttempt
{
    Spawned,
    BlockedByActiveLimit,
    BlockedByCooldown,
    SkippedByChance,
    Failed
}

/// <summary>
/// Реализация Dispatcher с дешёвым наблюдением на каждом Tick и редким запуском
/// тяжёлого Location resolution только когда сработал триггер.
/// </summary>
public sealed class DynamicEventDispatcher : IDynamicEventDispatcher
{
    private readonly IDataChannelHub _hub;
    private readonly ILocationResolver _locationResolver;
    private readonly Func<IReadOnlyList<DynamicEventDefinition>> _definitionsProvider;
    private readonly IQuestRuntimeController? _questRuntime;
    private readonly Random _random;

    private readonly Dictionary<string, DynamicEventDefinition> _definitions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Счётчик экземпляров по DefinitionId. Id обязан быть ВОСПРОИЗВОДИМЫМ и
    /// уникальным: он уходит в <c>ILocationResolver.Resolve(locationId, resolutionKey)</c>
    /// и в сохранение симуляции, поэтому Guid на роль идентификатора не годится.
    /// </summary>
    private readonly Dictionary<string, int> _instanceSequences =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _simulationRunning;
    private WorldCoordinate? _lastPlayerPosition;
    private bool _initialized;
    private bool _disposed;

    public DynamicEventDispatcher(
        IDataChannelHub hub,
        ILocationResolver locationResolver,
        Func<IReadOnlyList<DynamicEventDefinition>> definitionsProvider,
        Random? random = null,
        IQuestRuntimeController? questRuntime = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _locationResolver = locationResolver ?? throw new ArgumentNullException(nameof(locationResolver));
        _definitionsProvider = definitionsProvider ?? throw new ArgumentNullException(nameof(definitionsProvider));
        _questRuntime = questRuntime;
        _random = random ?? Random.Shared;

        RefreshDefinitions();
        _hub.Events.Published += HubEvent_Published;
        InitializeFromCurrentWorld();
    }

    public DynamicEventRuntimeState State =>
        _hub.Get<DynamicEventRuntimeState>("dynamic-events").Value;

    public bool SimulationRunning => _simulationRunning;

    public event Action<SimulatorEvent>? Published;

    public void SetSimulationRunning(bool running)
    {
        ThrowIfDisposed();

        if (_simulationRunning == running)
        {
            if (running)
                InitializeFromCurrentWorld();
            return;
        }

        _simulationRunning = running;
        InitializeFromCurrentWorld();

        if (running)
            SpawnOnSimulationStart();

        Publish(
            running ? "DynamicEventDispatcherStarted" : "DynamicEventDispatcherStopped",
            "Диспетчер динамических событий " + (running ? "запущен." : "остановлен."));
    }

    public void Tick()
    {
        ThrowIfDisposed();

        if (!_simulationRunning)
            return;

        RefreshDefinitions();

        var player = _hub.Get<PlayerState>("player").Value;
        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var now = DateTimeOffset.UtcNow;

        if (!_initialized)
            InitializeFromCurrentWorld();

        var distanceTravelled = _lastPlayerPosition is { } previousPosition
            ? Distance(previousPosition, player.Position)
            : 0d;

        // Нулевые/аномальные скачки не должны ломать бюджет. Телепорт в Simulator
        // тоже считается движением мира — это полезно для ручной проверки.
        if (!double.IsFinite(distanceTravelled) || distanceTravelled < 0)
            distanceTravelled = 0;

        var schedules = State.Schedules
            .ToDictionary(item => item.DefinitionId, item => item, StringComparer.OrdinalIgnoreCase);
        var changed = AutoDiscoverNearbyInstances(player.Position, clock, schedules);

        // Снимок обязателен: запись состояния внутри AttemptSpawn публикует
        // ChannelChanged, приходит обратно в HubEvent_Published и вызывает
        // RefreshDefinitions — то есть перестраивает _definitions ПРЯМО ВО ВРЕМЯ
        // этого перебора («Collection was modified»).
        foreach (var definition in _definitions.Values.ToArray())
        {
            var triggerType = Normalize(definition.Trigger.Type);
            if (triggerType == "distancetravelled")
            {
                changed |= EvaluateDistance(definition, distanceTravelled, clock, now, schedules);
            }
            else if (triggerType == "gametime")
            {
                changed |= EvaluateGameTime(definition, clock, now, schedules);
            }
            else if (triggerType == "realtime")
            {
                changed |= EvaluateRealTime(definition, now, schedules);
            }
            else if (triggerType == "dynamiceventdiscovery")
            {
                changed |= EvaluateDynamicEventDiscovery(definition, clock, now, schedules);
            }
        }

        changed |= ExpireInstances(clock, now);

        _lastPlayerPosition = player.Position;

        if (changed)
            WriteState(
                State.Instances,
                schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
                "DynamicEventDispatcher.Tick");
    }

    public void Reset()
    {
        ThrowIfDisposed();

        var player = _hub.Get<PlayerState>("player").Value;
        _lastPlayerPosition = player.Position;
        _initialized = true;

        var schedules = BuildInitialSchedules(_hub.Get<WorldClockState>("sim-time").Value);
        if (_locationResolver is ILocationResolutionSession locationSession)
            locationSession.Reset();

        WriteState(
            Array.Empty<DynamicEventInstance>(),
            schedules,
            "Сброс Dispatcher Dynamic Events");

        Publish("DynamicEventDispatcherReset", "Диспетчер динамических событий сброшен.");
    }

    public bool TrySpawn(string definitionId)
    {
        ThrowIfDisposed();

        RefreshDefinitions();

        if (!_definitions.TryGetValue(definitionId, out var definition))
        {
            Publish(
                "DynamicEventSpawnFailed",
                "Не найдено правило динамического события: " + definitionId);
            return false;
        }

        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var now = DateTimeOffset.UtcNow;
        var schedules = State.Schedules
            .ToDictionary(item => item.DefinitionId, item => item, StringComparer.OrdinalIgnoreCase);

        var result = AttemptSpawn(definition, clock, now, schedules, "Manual");
        WriteState(
            State.Instances,
            schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
            "DynamicEventDispatcher.Manual");

        return result == DynamicEventSpawnAttempt.Spawned;
    }

    /// <summary>
    /// Материализует правила, которым нужен первый экземпляр сразу после запуска
    /// симуляции. Дальше жизненный цикл этого экземпляра управляется обычными
    /// триггерами Dispatcher.
    /// </summary>
    private void SpawnOnSimulationStart()
    {
        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var now = DateTimeOffset.UtcNow;
        var schedules = State.Schedules
            .ToDictionary(item => item.DefinitionId, item => item, StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var definition in _definitions.Values.ToArray())
        {
            if (!definition.SpawnPolicy.SpawnOnSimulationStart)
                continue;

            if (State.Instances.Any(instance =>
                    instance.DefinitionId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase) &&
                    IsOccupyingRuntime(instance)))
                continue;

            var attempt = AttemptSpawn(definition, clock, now, schedules, "SimulationStarted");
            if (attempt == DynamicEventSpawnAttempt.Spawned)
                changed = true;
        }

        if (changed)
        {
            WriteState(
                State.Instances,
                schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
                "DynamicEventDispatcher.SpawnOnSimulationStart");
        }
    }

    /// <summary>
    /// Обнаружение автоматическое по расстоянию до фактической точки экземпляра.
    /// Это делает Dynamic Event именно мировым объектом: автору не приходится
    /// вручную отправлять Discover из Simulator.
    /// </summary>
    private bool AutoDiscoverNearbyInstances(
        WorldCoordinate playerPosition,
        WorldClockState clock,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        var changed = false;
        var nearby = State.Instances
            .Where(instance => instance.Status == DynamicEventInstanceStatus.Active)
            .Where(instance => Distance(playerPosition, instance.Point.Position) <=
                Math.Max(0, instance.Point.TriggerRadius))
            .Select(instance => instance.InstanceId)
            .ToArray();

        foreach (var instanceId in nearby)
        {
            changed |= DiscoverInternal(instanceId, clock, schedules);
        }

        return changed;
    }

    private bool EvaluateDynamicEventDiscovery(
        DynamicEventDefinition definition,
        WorldClockState clock,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        var schedule = GetOrCreateSchedule(definition, clock, now, schedules);
        if (schedule.NextGameElapsed is not { } next || clock.Elapsed < next)
            return false;

        var attempt = AttemptSpawn(definition, clock, now, schedules, "DynamicEventDiscovery");
        if (attempt == DynamicEventSpawnAttempt.Spawned)
        {
            schedules[definition.Id] = schedule with
            {
                NextGameElapsed = null,
                TriggerPending = false
            };
            return true;
        }

        // Активный лимит и cooldown должны перепроверяться на следующем тике.
        // Ошибка разрешения Location тоже оставляется pending: мир может
        // измениться, и новый кандидат появится без изменения Definition.
        schedules[definition.Id] = schedule with
        {
            TriggerPending = true
        };
        return true;
    }

    /// <summary>
    /// После обнаружения источника армается ровно один следующий запуск.
    /// Несколько одинаковых discovery triggers независимы по DefinitionId.
    /// </summary>
    private void ArmDiscoverySchedules(
        string sourceDefinitionId,
        WorldClockState clock,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        foreach (var definition in _definitions.Values)
        {
            var trigger = definition.Trigger;
            if (!Normalize(trigger.Type).Equals("dynamiceventdiscovery", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(trigger.SourceDefinitionId) &&
                !trigger.SourceDefinitionId.Equals(sourceDefinitionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var schedule = schedules.TryGetValue(definition.Id, out var existing)
                ? existing
                : GetOrCreateSchedule(definition, clock, now, schedules);

            schedules[definition.Id] = schedule with
            {
                NextGameElapsed = clock.Elapsed +
                    NextTimeInterval(trigger.MinGameHours, trigger.MaxGameHours),
                TriggerPending = false
            };
        }
    }

    public bool Discover(string instanceId)
    {
        ThrowIfDisposed();

        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var now = DateTimeOffset.UtcNow;
        var schedules = State.Schedules
            .ToDictionary(item => item.DefinitionId, item => item, StringComparer.OrdinalIgnoreCase);

        return DiscoverInternal(instanceId, clock, schedules, now);
    }

    private bool DiscoverInternal(
        string instanceId,
        WorldClockState clock,
        Dictionary<string, DynamicEventScheduleState> schedules,
        DateTimeOffset? nowOverride = null)
    {
        var current = State.Instances.ToList();
        var index = current.FindIndex(item =>
            item.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || current[index].Status != DynamicEventInstanceStatus.Active)
            return false;

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var instance = current[index] with
        {
            Status = DynamicEventInstanceStatus.Discovered,
            DiscoveredUtc = now,
            LastReason = "Событие обнаружено."
        };
        current[index] = instance;

        WriteState(
            current,
            schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
            "DynamicEventDispatcher.Discover");

        Publish(
            "DynamicEventDiscovered",
            "Динамическое событие обнаружено.",
            instance.DefinitionId,
            instance.InstanceId,
            instance.Point);

        if (_definitions.TryGetValue(instance.DefinitionId, out var sourceDefinition))
            ArmDiscoverySchedules(instance.DefinitionId, clock, now, schedules);

        // Для Dynamic Event, который связан с QuestId, обнаружение является
        // активацией: Dispatcher передаёт управление обычному Quest Runtime.
        // При успешном запуске экземпляр сразу считается использованным, чтобы
        // карта не показывала уже активированный тайник.
        if (_definitions.TryGetValue(instance.DefinitionId, out var definition) &&
            !string.IsNullOrWhiteSpace(definition.QuestId))
        {
            if (_questRuntime is null)
            {
                Publish(
                    "DynamicEventActivationFailed",
                    $"Нельзя активировать «{definition.Name}»: Quest Runtime не подключён.",
                    definition.Id,
                    instance.InstanceId,
                    instance.Point);
            }
            else if (_questRuntime.StartQuest(definition.QuestId))
            {
                Complete(instance.InstanceId, consumed: true);
            }
            else
            {
                Publish(
                    "DynamicEventActivationFailed",
                    $"Квест «{definition.QuestId}» не удалось активировать после обнаружения «{definition.Name}».",
                    definition.Id,
                    instance.InstanceId,
                    instance.Point);
            }
        }

        WriteState(
            State.Instances,
            schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
            "DynamicEventDispatcher.DiscoverySchedule");

        return true;
    }

    public bool Engage(string instanceId) =>
        Transition(instanceId,
            DynamicEventInstanceStatus.Discovered,
            DynamicEventInstanceStatus.Engaged,
            "Событие вовлечено.",
            instance => instance);

    public bool Complete(string instanceId, bool consumed = false)
    {
        var status = consumed ? DynamicEventInstanceStatus.Consumed : DynamicEventInstanceStatus.Completed;
        return Transition(
            instanceId,
            new[]
            {
                DynamicEventInstanceStatus.Spawned,
                DynamicEventInstanceStatus.Active,
                DynamicEventInstanceStatus.Discovered,
                DynamicEventInstanceStatus.Engaged
            },
            status,
            consumed ? "Событие использовано." : "Событие завершено.",
            instance => instance with { CompletedUtc = DateTimeOffset.UtcNow });
    }

    public bool Cancel(string instanceId, string reason = "Событие отменено.") =>
        TransitionAnyActive(
            instanceId,
            DynamicEventInstanceStatus.Cancelled,
            reason,
            instance => instance with { LastReason = reason });

    public bool Abandon(string instanceId, string reason = "Событие оставлено.") =>
        TransitionAnyActive(
            instanceId,
            DynamicEventInstanceStatus.Abandoned,
            reason,
            instance => instance with { LastReason = reason });

    private bool EvaluateDistance(
        DynamicEventDefinition definition,
        double distanceTravelled,
        WorldClockState clock,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        var schedule = GetOrCreateSchedule(definition, clock, now, schedules);
        var budget = schedule.DistanceBudgetMeters + distanceTravelled;
        var threshold = schedule.NextDistanceThresholdMeters ??
            NextDistanceThreshold(definition.Trigger);

        var changed = !ApproximatelyEqual(budget, schedule.DistanceBudgetMeters) ||
            !ApproximatelyEqual(threshold, schedule.NextDistanceThresholdMeters);

        schedule = schedule with
        {
            DistanceBudgetMeters = budget,
            NextDistanceThresholdMeters = threshold
        };

        // Не допускаем огромного burst после длинного телепорта/загрузки.
        // Одним Tick можно материализовать несколько независимых событий, но не
        // больше четырёх. Невыполненный порог остаётся pending.
        var attempts = 0;
        while (schedule.DistanceBudgetMeters >= threshold && attempts < 4)
        {
            var attempt = AttemptSpawn(definition, clock, now, schedules, "DistanceTravelled");

            if (attempt is DynamicEventSpawnAttempt.BlockedByActiveLimit or
                DynamicEventSpawnAttempt.BlockedByCooldown)
            {
                schedule = schedule with { TriggerPending = true };
                changed = true;
                break;
            }

            schedule = schedule with
            {
                DistanceBudgetMeters = Math.Max(0, schedule.DistanceBudgetMeters - threshold),
                NextDistanceThresholdMeters = NextDistanceThreshold(definition.Trigger),
                TriggerPending = false
            };

            attempts++;
            changed = true;

            // Probability/location failure потребили этот trigger. Следующий
            // случайный порог выбирается независимо.
            threshold = schedule.NextDistanceThresholdMeters!.Value;

            if (schedule.DistanceBudgetMeters < threshold)
                break;
        }

        schedules[definition.Id] = schedule;
        return changed;
    }

    private bool EvaluateGameTime(
        DynamicEventDefinition definition,
        WorldClockState clock,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        var schedule = GetOrCreateSchedule(definition, clock, now, schedules);
        var due = schedule.NextGameElapsed is { } next && clock.Elapsed >= next;

        if (!due)
            return false;

        var attempt = AttemptSpawn(definition, clock, now, schedules, "GameTime");

        if (attempt is DynamicEventSpawnAttempt.BlockedByActiveLimit or
            DynamicEventSpawnAttempt.BlockedByCooldown)
        {
            if (!schedule.TriggerPending)
                schedules[definition.Id] = schedule with { TriggerPending = true };

            return true;
        }

        var nextInterval = NextTimeInterval(definition.Trigger.MinGameHours, definition.Trigger.MaxGameHours);
        schedules[definition.Id] = schedule with
        {
            NextGameElapsed = clock.Elapsed + nextInterval,
            TriggerPending = false
        };

        return true;
    }

    private bool EvaluateRealTime(
        DynamicEventDefinition definition,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var schedule = GetOrCreateSchedule(definition, clock, now, schedules);
        var due = schedule.NextRealUtc is { } next && now >= next;

        if (!due)
            return false;

        var attempt = AttemptSpawn(definition, clock, now, schedules, "RealTime");

        if (attempt is DynamicEventSpawnAttempt.BlockedByActiveLimit or
            DynamicEventSpawnAttempt.BlockedByCooldown)
        {
            if (!schedule.TriggerPending)
                schedules[definition.Id] = schedule with { TriggerPending = true };

            return true;
        }

        var nextInterval = NextTimeInterval(definition.Trigger.MinRealHours, definition.Trigger.MaxRealHours);
        schedules[definition.Id] = schedule with
        {
            NextRealUtc = now + nextInterval,
            TriggerPending = false
        };

        return true;
    }

    private DynamicEventSpawnAttempt AttemptSpawn(
        DynamicEventDefinition definition,
        WorldClockState clock,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules,
        string source)
    {
        var activeCount = State.Instances.Count(IsOccupyingRuntime);

        if (activeCount >= Math.Max(0, definition.SpawnPolicy.MaxActiveInstances))
            return DynamicEventSpawnAttempt.BlockedByActiveLimit;

        var schedule = schedules.TryGetValue(definition.Id, out var existing)
            ? existing
            : new DynamicEventScheduleState(definition.Id);

        if (IsOnCooldown(definition, schedule, clock, now))
            return DynamicEventSpawnAttempt.BlockedByCooldown;

        if (!ChancePasses(definition.SpawnPolicy.SpawnChance))
        {
            Publish(
                "DynamicEventSpawnSkipped",
                $"Генерация «{definition.Name}» пропущена по вероятности.",
                definition.Id);
            return DynamicEventSpawnAttempt.SkippedByChance;
        }

        if (string.IsNullOrWhiteSpace(definition.LocationId))
        {
            Publish(
                "DynamicEventSpawnFailed",
                $"У Dynamic Event «{definition.Name}» не указан Location.",
                definition.Id);
            return DynamicEventSpawnAttempt.Failed;
        }

        var instanceId = CreateInstanceId(definition.Id);
        var point = _locationResolver.Resolve(definition.LocationId, instanceId);

        if (point is null)
        {
            Publish(
                "DynamicEventSpawnFailed",
                $"Location «{definition.LocationId}» не смог разрешить точку для «{definition.Name}».",
                definition.Id);
            return DynamicEventSpawnAttempt.Failed;
        }

        // Runtime-экземпляр получает собственный trigger radius. Location лишь
        // выбирает место; радиус взаимодействия принадлежит событию.
        var eventPoint = point with
        {
            TriggerRadius = definition.TriggerRadius
        };

        var instance = new DynamicEventInstance(
            instanceId,
            definition.Id,
            DynamicEventInstanceStatus.Spawned,
            eventPoint)
        {
            SpawnedUtc = now,
            SpawnedGameElapsed = clock.Elapsed
        };

        // Spawned — одноходовое техническое состояние. Сразу после публикации
        // Runtime считает событие Active: UI увидит обе стадии через журнал, а
        // состояние не зависнет в промежуточном статусе.
        AddInstance(instance with { Status = DynamicEventInstanceStatus.Active });

        schedules[definition.Id] = schedule with
        {
            LastSpawnUtc = now,
            LastSpawnGameElapsed = clock.Elapsed,
            TriggerPending = false
        };

        Publish(
            "DynamicEventSpawned",
            $"Появилось динамическое событие «{definition.Name}».",
            definition.Id,
            instance.InstanceId,
            instance.Point);

        return DynamicEventSpawnAttempt.Spawned;
    }

    private bool ExpireInstances(WorldClockState clock, DateTimeOffset now)
    {
        var changed = false;
        var retained = new List<DynamicEventInstance>(State.Instances.Count);
        var expiredDefinitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in State.Instances)
        {
            if (!_definitions.TryGetValue(instance.DefinitionId, out var definition) ||
                !IsOccupyingRuntime(instance))
            {
                retained.Add(instance);
                continue;
            }

            var expired =
                IsLifetimeReached(
                    definition.SpawnPolicy.LifetimeGameHours,
                    clock.Elapsed - instance.SpawnedGameElapsed) ||
                IsLifetimeReached(
                    definition.SpawnPolicy.LifetimeRealHours,
                    now - instance.SpawnedUtc);

            if (!expired)
            {
                retained.Add(instance);
                continue;
            }

            retained.Add(instance with
            {
                Status = DynamicEventInstanceStatus.Expired,
                LastReason = "Истёк срок жизни динамического события."
            });
            expiredDefinitions.Add(definition.Id);

            Publish(
                "DynamicEventExpired",
                $"Истёк срок жизни «{definition.Name}».",
                definition.Id,
                instance.InstanceId,
                instance.Point);

            changed = true;
        }

        var cleaned = retained
            .Where(instance =>
                instance.Status is not (
                    DynamicEventInstanceStatus.Completed or
                    DynamicEventInstanceStatus.Consumed or
                    DynamicEventInstanceStatus.Cancelled or
                    DynamicEventInstanceStatus.Abandoned) ||
                !_definitions.TryGetValue(instance.DefinitionId, out var definition) ||
                !definition.SpawnPolicy.RemoveOnCompleted)
            .ToArray();

        if (cleaned.Length != State.Instances.Count)
            changed = true;

        // Сначала фиксируем Expired/удалённые экземпляры. После этого
        // RespawnOnExpired может честно увидеть, что лимит активных экземпляров
        // освободился.
        if (changed)
        {
            WriteState(
                cleaned,
                State.Schedules,
                "DynamicEventDispatcher.Expire");
        }

        foreach (var definitionId in expiredDefinitions)
        {
            if (!_definitions.TryGetValue(definitionId, out var definition) ||
                !definition.SpawnPolicy.RespawnOnExpired)
                continue;

            if (State.Instances.Any(instance =>
                    instance.DefinitionId.Equals(definitionId, StringComparison.OrdinalIgnoreCase) &&
                    IsOccupyingRuntime(instance)))
                continue;

            var schedules = State.Schedules
                .ToDictionary(item => item.DefinitionId, item => item, StringComparer.OrdinalIgnoreCase);
            var attempt = AttemptSpawn(definition, clock, now, schedules, "Expired");
            if (attempt == DynamicEventSpawnAttempt.Spawned)
            {
                WriteState(
                    State.Instances,
                    schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
                    "DynamicEventDispatcher.Respawn");
                changed = true;
            }
        }

        return changed;
    }

    private DynamicEventScheduleState GetOrCreateSchedule(
        DynamicEventDefinition definition,
        WorldClockState clock,
        DateTimeOffset now,
        Dictionary<string, DynamicEventScheduleState> schedules)
    {
        if (schedules.TryGetValue(definition.Id, out var existing))
            return existing;

        var schedule = new DynamicEventScheduleState(definition.Id);
        var type = Normalize(definition.Trigger.Type);

        if (type == "distancetravelled")
        {
            schedule = schedule with
            {
                NextDistanceThresholdMeters = NextDistanceThreshold(definition.Trigger)
            };
        }
        else if (type == "gametime")
        {
            schedule = schedule with
            {
                NextGameElapsed = clock.Elapsed +
                    NextTimeInterval(definition.Trigger.MinGameHours, definition.Trigger.MaxGameHours)
            };
        }
        else if (type == "realtime")
        {
            schedule = schedule with
            {
                NextRealUtc = now +
                    NextTimeInterval(definition.Trigger.MinRealHours, definition.Trigger.MaxRealHours)
            };
        }
        else if (type == "dynamiceventdiscovery")
        {
            // Первоначальное значение не задаём: оно появляется только после
            // обнаружения sourceDefinitionId.
            schedule = schedule with { NextGameElapsed = null };
        }

        schedules[definition.Id] = schedule;
        return schedule;
    }

    private IReadOnlyList<DynamicEventScheduleState> BuildInitialSchedules(WorldClockState clock)
    {
        var now = DateTimeOffset.UtcNow;
        var schedules = new Dictionary<string, DynamicEventScheduleState>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in _definitions.Values)
            GetOrCreateSchedule(definition, clock, now, schedules);

        return schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void InitializeFromCurrentWorld()
    {
        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        _lastPlayerPosition = _hub.Get<PlayerState>("player").Value.Position;
        _initialized = true;

        var schedules = State.Schedules.Count == 0
            ? BuildInitialSchedules(clock)
            : State.Schedules;

        // Runtime State переживает паузу. При старте/возобновлении мы только
        // фиксируем новую исходную позицию, чтобы движение во время паузы не
        // начислялось в DistanceTravelled.
        if (!Enumerable.SequenceEqual(
                schedules.Select(item => item.DefinitionId).OrderBy(x => x),
                _definitions.Values
                    .Select(item => item.Id)
                    .Where(item => NormalizeTriggerNeedsSchedule(_definitions[item]))
                    .OrderBy(x => x)))
        {
            schedules = BuildInitialSchedules(clock);
        }

        WriteState(State.Instances, schedules, "DynamicEventDispatcher.Initialize");
    }

    private void RefreshDefinitions()
    {
        IReadOnlyList<DynamicEventDefinition> loaded;
        try
        {
            loaded = _definitionsProvider() ?? Array.Empty<DynamicEventDefinition>();
        }
        catch
        {
            loaded = Array.Empty<DynamicEventDefinition>();
        }

        _definitions.Clear();

        foreach (var definition in loaded)
        {
            if (definition is null ||
                string.IsNullOrWhiteSpace(definition.Id) ||
                string.IsNullOrWhiteSpace(definition.Name))
                continue;

            if (!_definitions.ContainsKey(definition.Id))
                _definitions.Add(definition.Id, definition);
        }
    }

    private static bool NormalizeTriggerNeedsSchedule(DynamicEventDefinition definition)
    {
        var type = Normalize(definition.Trigger.Type);
        return type is "distancetravelled" or "gametime" or "realtime" or "dynamiceventdiscovery";
    }

    private void AddInstance(DynamicEventInstance instance)
    {
        var current = State.Instances.ToList();
        current.RemoveAll(item => item.InstanceId.Equals(instance.InstanceId, StringComparison.OrdinalIgnoreCase));
        current.Add(instance);
        _hub.Get<DynamicEventRuntimeState>("dynamic-events").Set(
            new DynamicEventRuntimeState(current, State.Schedules),
            "DynamicEventDispatcher.Spawn");
    }

    private bool TransitionAnyActive(
        string instanceId,
        DynamicEventInstanceStatus status,
        string reason,
        Func<DynamicEventInstance, DynamicEventInstance> mutator)
    {
        var current = State.Instances.ToList();
        var index = current.FindIndex(item => item.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !IsOccupyingRuntime(current[index]))
            return false;

        current[index] = mutator(current[index]) with
        {
            Status = status,
            LastReason = reason
        };

        WriteState(current, State.Schedules, "DynamicEventDispatcher.Transition");

        Publish(
            "DynamicEventStateChanged",
            reason,
            current[index].DefinitionId,
            current[index].InstanceId,
            current[index].Point);

        return true;
    }

    private bool Transition(
        string instanceId,
        DynamicEventInstanceStatus expected,
        DynamicEventInstanceStatus next,
        string reason,
        Func<DynamicEventInstance, DynamicEventInstance> mutator) =>
        Transition(instanceId, new[] { expected }, next, reason, mutator);

    private bool Transition(
        string instanceId,
        IReadOnlyCollection<DynamicEventInstanceStatus> expected,
        DynamicEventInstanceStatus next,
        string reason,
        Func<DynamicEventInstance, DynamicEventInstance> mutator)
    {
        var current = State.Instances.ToList();
        var index = current.FindIndex(item => item.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !expected.Contains(current[index].Status))
            return false;

        current[index] = mutator(current[index]) with
        {
            Status = next,
            LastReason = reason
        };

        WriteState(current, State.Schedules, "DynamicEventDispatcher.Transition");
        Publish(
            "DynamicEventStateChanged",
            reason,
            current[index].DefinitionId,
            current[index].InstanceId,
            current[index].Point);
        return true;
    }

    private void WriteState(
        IReadOnlyList<DynamicEventInstance> instances,
        IReadOnlyList<DynamicEventScheduleState> schedules,
        string source)
    {
        _hub.Get<DynamicEventRuntimeState>("dynamic-events").Set(
            new DynamicEventRuntimeState(instances.ToArray(), schedules.ToArray()),
            source);
    }

    private bool IsOnCooldown(
        DynamicEventDefinition definition,
        DynamicEventScheduleState schedule,
        WorldClockState clock,
        DateTimeOffset now)
    {
        if (definition.SpawnPolicy.CooldownGameHours is { } gameHours &&
            schedule.LastSpawnGameElapsed is { } lastGame &&
            clock.Elapsed - lastGame < TimeSpan.FromHours(Math.Max(0, gameHours)))
            return true;

        if (definition.SpawnPolicy.CooldownRealHours is { } realHours &&
            schedule.LastSpawnUtc is { } lastReal &&
            now - lastReal < TimeSpan.FromHours(Math.Max(0, realHours)))
            return true;

        return false;
    }

    private bool ChancePasses(double chance)
    {
        if (!double.IsFinite(chance))
            return false;

        return _random.NextDouble() < Math.Clamp(chance, 0d, 1d);
    }

    private double NextDistanceThreshold(DynamicEventTriggerDefinition trigger)
    {
        var min = trigger.MinDistanceMeters ?? 0;
        var max = trigger.MaxDistanceMeters ?? min;

        if (!double.IsFinite(min) || !double.IsFinite(max))
            return 0;

        min = Math.Max(0, min);
        max = Math.Max(min, max);

        if (Math.Abs(max - min) < 0.000001)
            return min;

        return min + _random.NextDouble() * (max - min);
    }

    private TimeSpan NextTimeInterval(double? minHours, double? maxHours)
    {
        var min = minHours ?? 0;
        var max = maxHours ?? min;

        if (!double.IsFinite(min) || !double.IsFinite(max))
            return TimeSpan.Zero;

        min = Math.Max(0, min);
        max = Math.Max(min, max);

        var hours = Math.Abs(max - min) < 0.000001
            ? min
            : min + _random.NextDouble() * (max - min);

        return TimeSpan.FromHours(hours);
    }

    private static bool IsLifetimeReached(double? configuredHours, TimeSpan elapsed)
    {
        if (!configuredHours.HasValue ||
            !double.IsFinite(configuredHours.Value) ||
            configuredHours.Value < 0)
            return false;

        return elapsed >= TimeSpan.FromHours(configuredHours.Value);
    }

    private static bool IsOccupyingRuntime(DynamicEventInstance instance) =>
        instance.Status is
            DynamicEventInstanceStatus.Spawned or
            DynamicEventInstanceStatus.Active or
            DynamicEventInstanceStatus.Discovered or
            DynamicEventInstanceStatus.Engaged;

    private static double Distance(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool ApproximatelyEqual(double? a, double? b) =>
        a.HasValue == b.HasValue &&
        (!a.HasValue || Math.Abs(a.Value - b!.Value) < 0.000001);

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty)
            .ToLowerInvariant();

    private static bool MatchesWorldEvent(
        DynamicEventDefinition definition,
        SimulatorEvent e)
    {
        var trigger = definition.Trigger;
        if (!string.Equals(trigger.EventType, e.EventType, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var pair in trigger.EventPayload)
        {
            if (!e.Payload.TryGetValue(pair.Key, out var value) ||
                !string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private void HubEvent_Published(SimulatorEvent e)
    {
        if (_disposed || !_simulationRunning)
            return;

        if (e.Source.Equals("DynamicEventDispatcher", StringComparison.OrdinalIgnoreCase))
            return;

        RefreshDefinitions();

        var matching = _definitions.Values
            .Where(definition => Normalize(definition.Trigger.Type) == "worldevent")
            .Where(definition => MatchesWorldEvent(definition, e))
            .ToArray();

        if (matching.Length == 0)
            return;

        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var now = DateTimeOffset.UtcNow;
        var schedules = State.Schedules
            .ToDictionary(item => item.DefinitionId, item => item, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in matching)
            AttemptSpawn(definition, clock, now, schedules, "WorldEvent");

        // AttemptSpawn уже записал State через AddInstance; этот вызов
        // синхронизирует расписания, обновлённые внутри попытки.
        WriteState(
            State.Instances,
            schedules.Values.OrderBy(item => item.DefinitionId, StringComparer.OrdinalIgnoreCase).ToArray(),
            "DynamicEventDispatcher.WorldEvent");
    }

    private void Publish(
        string eventType,
        string message,
        string? definitionId = null,
        string? instanceId = null,
        WorldPoint? point = null)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(definitionId))
            payload["definitionId"] = definitionId;
        if (!string.IsNullOrWhiteSpace(instanceId))
            payload["instanceId"] = instanceId;
        if (point is not null)
        {
            payload["pointId"] = point.Id;
            payload["x"] = point.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture);
            payload["y"] = point.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            payload["z"] = point.Position.Z.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var e = new SimulatorEvent(eventType, DateTimeOffset.UtcNow, "DynamicEventDispatcher", payload);
        _hub.Events.Publish(e);
        Published?.Invoke(e);
    }

    private string CreateInstanceId(string definitionId)
    {
        _instanceSequences.TryGetValue(definitionId, out var sequence);

        // Загруженное состояние могло прийти из сохранения: продолжаем нумерацию
        // ПОСЛЕ уже существующих экземпляров, иначе новый id столкнётся со старым.
        foreach (var instance in State.Instances)
        {
            if (!instance.DefinitionId.Equals(definitionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var separator = instance.InstanceId.LastIndexOf('#');
            if (separator < 0 || separator + 1 >= instance.InstanceId.Length)
                continue;

            if (int.TryParse(
                    instance.InstanceId.AsSpan(separator + 1),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var existing) &&
                existing > sequence)
            {
                sequence = existing;
            }
        }

        sequence++;
        _instanceSequences[definitionId] = sequence;
        return definitionId + "#" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DynamicEventDispatcher));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hub.Events.Published -= HubEvent_Published;
    }
}
