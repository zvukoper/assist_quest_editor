namespace AssistQuestEditor.Domain;

public interface IDataChannel
{
    string Key { get; }
    string DisplayName { get; }
    Type ValueType { get; }
    object UntypedValue { get; }
    bool Mutable { get; }
}

public interface IDataChannel<T> : IDataChannel
{
    T Value { get; }
    event EventHandler<DataChannelChangedEventArgs<T>>? Changed;
    void Set(T value, string source);
}

public sealed class DataChannelChangedEventArgs<T>(
    T previousValue,
    T currentValue,
    string source) : EventArgs
{
    public T PreviousValue { get; } = previousValue;
    public T CurrentValue { get; } = currentValue;
    public string Source { get; } = source;
}

public sealed class DataChannel<T>(
    string key,
    string displayName,
    T initialValue,
    bool mutable = true) : IDataChannel<T>
{
    public string Key { get; } = key;
    public string DisplayName { get; } = displayName;
    public Type ValueType => typeof(T);
    public bool Mutable { get; } = mutable;
    public T Value { get; private set; } = initialValue;
    public object UntypedValue => Value!;

    public event EventHandler<DataChannelChangedEventArgs<T>>? Changed;

    public void Set(T value, string source)
    {
        if (!Mutable)
        {
            throw new InvalidOperationException($"Канал «{Key}» недоступен для записи.");
        }

        var previous = Value;
        Value = value;
        Changed?.Invoke(this, new DataChannelChangedEventArgs<T>(previous, value, source));
    }
}

public interface IEventChannel<TEvent>
{
    event Action<TEvent>? Published;
    void Publish(TEvent value);
}

public sealed class EventChannel<TEvent> : IEventChannel<TEvent>
{
    public event EventHandler<TEvent>? Published;

    public void Publish(TEvent value)
    {
        Published?.Invoke(value);
    }
}

public interface IDataChannelHub
{
    IDataChannel<T> Get<T>(string key);
    IReadOnlyCollection<DataChannelDescriptor> Describe();
    SimulatorSnapshot GetSnapshot();
    IEventChannel<SimulatorEvent> Events { get; }
}

public sealed class SimulatorDataChannelHub : IDataChannelHub
{
    private readonly Dictionary<string, IDataChannel> _channels;

    public SimulatorDataChannelHub()
    {
        Player = new DataChannel<PlayerState>(
            "player",
            "Игрок",
            new PlayerState(new WorldCoordinate(120, 0, 80), 0, 0, false, true));

        World = new DataChannel<WorldState>(
            "world",
            "Мир",
            new WorldState(
                "ETS2 X/Y/Z • вид сверху использует X/Z",
                new[]
                {
                    new WorldPoint("ruslan", "Руслан", "Квестовый персонаж", new WorldCoordinate(180, 0, 80)),
                    new WorldPoint("gosha", "Гоша", "Квестовый персонаж", new WorldCoordinate(430, 0, -120)),
                    new WorldPoint("yard", "Испытательный двор", "Локация", new WorldCoordinate(-140, 0, -60)),
                    new WorldPoint("village", "Деревня", "Локация", new WorldCoordinate(30, 0, 260))
                },
                "Испытательный двор"));

        Facts = new DataChannel<FactState>(
            "facts",
            "Факты",
            new FactState(new Dictionary<string, string>
            {
                ["quest.ruslan.introductionSeen"] = "false",
                ["world.marketOpen"] = "true",
                ["player.hasLicense"] = "A"
            }));

        QuestStatuses = new DataChannel<QuestStatusesState>(
            "quest-statuses",
            "Статусы квестов",
            new QuestStatusesState(new[]
            {
                new QuestStatusEntry("special_marinated_shashlik", QuestStatus.Available, "available")
            }));

        States = new DataChannel<RuntimeStatesState>(
            "states",
            "Состояния",
            new RuntimeStatesState(
                new Dictionary<string, bool>
                {
                    ["ruslan.offerPending"] = false,
                    ["quest.demoMode"] = true
                },
                new Dictionary<string, string>
                {
                    ["test.number"] = "0",
                    ["quest.lastChoice"] = ""
                },
                null,
                null));

        Inventory = new DataChannel<InventoryState>(
            "inventory",
            "Инвентарь",
            new InventoryState(new Dictionary<string, int>
            {
                ["money"] = 1500,
                ["special_marinade_meat"] = 0,
                ["legendary_shashlik"] = 0
            }));

        Reputation = new DataChannel<ReputationState>(
            "reputation",
            "Репутация",
            new ReputationState(new Dictionary<string, int>
            {
                ["ruslan"] = 0,
                ["gosha"] = 0
            }));

        Telemetry = new DataChannel<TelemetryState>(
            "telemetry",
            "Телеметрия",
            new TelemetryState(
                0, 800, 0, 0, 0, 78, 82, 34, 0, 0, 0, 0, false));

        Environment = new DataChannel<EnvironmentState>(
            "environment",
            "Окружение",
            new EnvironmentState("Ясно", 0, "12:30", 5000));

        System = new DataChannel<SystemState>(
            "system",
            "Система",
            new SystemState(true, "Симулятор", "", "Приложение запущено"));

        _channels = new Dictionary<string, IDataChannel>(StringComparer.OrdinalIgnoreCase)
        {
            [Player.Key] = Player,
            [World.Key] = World,
            [Facts.Key] = Facts,
            [QuestStatuses.Key] = QuestStatuses,
            [States.Key] = States,
            [Inventory.Key] = Inventory,
            [Reputation.Key] = Reputation,
            [Telemetry.Key] = Telemetry,
            [Environment.Key] = Environment,
            [System.Key] = System
        };

        foreach (var channel in _channels.Values)
        {
            Subscribe(channel);
        }
    }

    public DataChannel<PlayerState> Player { get; }
    public DataChannel<WorldState> World { get; }
    public DataChannel<FactState> Facts { get; }
    public DataChannel<QuestStatusesState> QuestStatuses { get; }
    public DataChannel<RuntimeStatesState> States { get; }
    public DataChannel<InventoryState> Inventory { get; }
    public DataChannel<ReputationState> Reputation { get; }
    public DataChannel<TelemetryState> Telemetry { get; }
    public DataChannel<EnvironmentState> Environment { get; }
    public DataChannel<SystemState> System { get; }

    public EventChannel<SimulatorEvent> Events { get; } = new();

    IDataChannel<T> IDataChannelHub.Get<T>(string key)
    {
        if (!_channels.TryGetValue(key, out var channel) || channel is not IDataChannel<T> typed)
        {
            throw new KeyNotFoundException($"Канал «{key}» типа «{typeof(T).Name}» не найден.");
        }

        return typed;
    }

    public void Reset()
    {
        Player.Set(new PlayerState(new WorldCoordinate(120, 0, 80), 0, 0, false, true), "Сброс симулятора");
        World.Set(new WorldState(
            "ETS2 X/Y/Z • вид сверху использует X/Z",
            new[]
            {
                new WorldPoint("ruslan", "Руслан", "Квестовый персонаж", new WorldCoordinate(180, 0, 80)),
                new WorldPoint("gosha", "Гоша", "Квестовый персонаж", new WorldCoordinate(430, 0, -120)),
                new WorldPoint("yard", "Испытательный двор", "Локация", new WorldCoordinate(-140, 0, -60)),
                new WorldPoint("village", "Деревня", "Локация", new WorldCoordinate(30, 0, 260))
            },
            "Испытательный двор"), "Сброс симулятора");
        Facts.Set(new FactState(new Dictionary<string, string>
        {
            ["quest.ruslan.introductionSeen"] = "false",
            ["world.marketOpen"] = "true",
            ["player.hasLicense"] = "A"
        }), "Сброс симулятора");
        QuestStatuses.Set(new QuestStatusesState(new[]
        {
            new QuestStatusEntry("special_marinated_shashlik", QuestStatus.Available, "available")
        }), "Сброс симулятора");
        States.Set(new RuntimeStatesState(
            new Dictionary<string, bool>
            {
                ["ruslan.offerPending"] = false,
                ["quest.demoMode"] = true
            },
            new Dictionary<string, string>
            {
                ["test.number"] = "0",
                ["quest.lastChoice"] = ""
            },
            null,
            null), "Сброс симулятора");
        Inventory.Set(new InventoryState(new Dictionary<string, int>
        {
            ["money"] = 1500,
            ["special_marinade_meat"] = 0,
            ["legendary_shashlik"] = 0
        }), "Сброс симулятора");
        Reputation.Set(new ReputationState(new Dictionary<string, int>
        {
            ["ruslan"] = 0,
            ["gosha"] = 0
        }), "Сброс симулятора");
        Telemetry.Set(new TelemetryState(0, 800, 0, 0, 0, 78, 82, 34, 0, 0, 0, 0, false), "Сброс симулятора");
        Environment.Set(new EnvironmentState("Ясно", 0, "12:30", 5000), "Сброс симулятора");
        System.Set(new SystemState(true, "Симулятор", "", "Симуляция сброшена"), "Сброс симулятора");
    }

    public IReadOnlyCollection<DataChannelDescriptor> Describe() =>
        _channels.Values
            .Select(channel => new DataChannelDescriptor(
                channel.Key,
                channel.DisplayName,
                channel.ValueType.Name,
                "Simulator",
                channel.Mutable))
            .ToArray();

    public SimulatorSnapshot GetSnapshot() =>
        new(
            Player.Value,
            World.Value,
            Facts.Value,
            QuestStatuses.Value,
            States.Value,
            Inventory.Value,
            Reputation.Value,
            Telemetry.Value,
            Environment.Value,
            System.Value);

    private void Subscribe(IDataChannel channel)
    {
        if (channel is DataChannel<PlayerState> typed)
        {
            typed.Changed += (_, args) => PublishTransition("player", args.Source, $"Позиция {args.CurrentValue.Position}");
        }
        else if (channel is DataChannel<TelemetryState> telemetry)
        {
            telemetry.Changed += (_, args) => PublishTransition("telemetry", args.Source, "Изменена телеметрия");
        }
        else if (channel is DataChannel<QuestStatusesState> statuses)
        {
            statuses.Changed += (_, args) => PublishTransition("quest-statuses", args.Source, "Изменён статус квеста");
        }
        else if (channel is DataChannel<RuntimeStatesState> states)
        {
            states.Changed += (_, args) => PublishTransition("states", args.Source, "Изменено состояние");
        }
        else if (channel is DataChannel<FactState> facts)
        {
            facts.Changed += (_, args) => PublishTransition("facts", args.Source, "Изменён факт");
        }
        else if (channel is DataChannel<InventoryState> inventory)
        {
            inventory.Changed += (_, args) => PublishTransition("inventory", args.Source, "Изменён инвентарь");
        }
        else if (channel is DataChannel<ReputationState> reputation)
        {
            reputation.Changed += (_, args) => PublishTransition("reputation", args.Source, "Изменена репутация");
        }
        else if (channel is DataChannel<EnvironmentState> environment)
        {
            environment.Changed += (_, args) => PublishTransition("environment", args.Source, "Изменено окружение");
        }
        else if (channel is DataChannel<WorldState> world)
        {
            world.Changed += (_, args) => PublishTransition("world", args.Source, "Изменён мир");
        }
        else if (channel is DataChannel<SystemState> system)
        {
            system.Changed += (_, args) => PublishTransition("system", args.Source, "Изменено состояние системы");
        }
    }

    private void PublishTransition(string channel, string source, string description)
    {
        var evt = new SimulatorEvent(
            "ChannelChanged",
            DateTimeOffset.UtcNow,
            source,
            new Dictionary<string, string>
            {
                ["channel"] = channel,
                ["description"] = description
            });

        Events.Publish(evt);
    }
}
