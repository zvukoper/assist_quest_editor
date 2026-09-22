using System.Globalization;

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
    public event Action<TEvent>? Published;

    public void Publish(TEvent value)
    {
        Published?.Invoke(value);
    }
}

public interface IDataSourceAdapter
{
    string AdapterId { get; }
    IDataChannelHub Channels { get; }
}

public sealed class SimulatorDataSourceAdapter : IDataSourceAdapter
{
    public string AdapterId => "simulator";
    public IDataChannelHub Channels { get; }

    public SimulatorDataSourceAdapter(IEnumerable<WorldPoint>? worldPoints = null)
    {
        Channels = new SimulatorDataChannelHub(worldPoints);
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
    private readonly WorldState _initialWorld;
    private readonly PlayerState _initialPlayer;

    public SimulatorDataChannelHub(IEnumerable<WorldPoint>? worldPoints = null)
    {
        var points = (worldPoints ?? CreateFallbackWorld()).ToArray();
        _initialWorld = new WorldState(
            "ETS2 X/Y/Z • вид сверху использует X/Z",
            points,
            "СДО");

        _initialPlayer = new PlayerState(
            GetWorldCenter(points),
            0,
            0,
            false,
            true);

        Player = new DataChannel<PlayerState>(
            "player",
            "Игрок",
            _initialPlayer);

        World = new DataChannel<WorldState>(
            "world",
            "Мир",
            _initialWorld);

        Selection = new DataChannel<WorldSelectionState>(
            "world-selection",
            "Выбранная точка",
            new WorldSelectionState(null, "Нет выбора"));

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
                new QuestStatusEntry("tutorial_ruslan_shashlik", QuestStatus.Available, "available"),
                new QuestStatusEntry("gosha_homemade_sausage", QuestStatus.Available, "available"),
                new QuestStatusEntry("ruslan_shashlik_delivery", QuestStatus.Available, "available")
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

        PlayerVitals = new DataChannel<PlayerVitalsState>(
            "player-vitals",
            "Потребности игрока",
            new PlayerVitalsState(100, 100, 100, 100, 100, 100, 0, 100));

        PlayerProgress = new DataChannel<PlayerProgressState>(
            "player-progress",
            "Деньги и опыт",
            new PlayerProgressState(1500, 0, 0));

        Character = new DataChannel<CharacterState>(
            "character",
            "Персонаж",
            new CharacterState(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["strength"] = 5,
                    ["perception"] = 5,
                    ["endurance"] = 5,
                    ["charisma"] = 5,
                    ["intelligence"] = 5,
                    ["agility"] = 5,
                    ["luck"] = 5
                },
                new[]
                {
                    new CharacterSkillState("ce-driver", "Автошкольник", "Получил права категории CE.", true, "CE"),
                    new CharacterSkillState("field-repair", "Очумелые ручки", "Навыки полевого ремонта базового уровня.", true, "Базовый")
                },
                Array.Empty<string>(),
                Array.Empty<string>()));

        Inventory = new DataChannel<InventoryState>(
            "inventory",
            "Инвентарь",
            new InventoryState(new Dictionary<string, int>
            {
                ["ruslan.raw_meat"] = 0
            }));

        Reputation = new DataChannel<ReputationState>(
            "reputation",
            "Репутация",
            ReputationState.ForNpcs(NpcCatalogFactory.CreateStarter()));

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

        Interfaces = new DataChannel<InterfaceState>(
            "interfaces",
            "Интерфейсы",
            new InterfaceState(null));

        _channels = new Dictionary<string, IDataChannel>(StringComparer.OrdinalIgnoreCase)
        {
            [Player.Key] = Player,
            [PlayerVitals.Key] = PlayerVitals,
            [PlayerProgress.Key] = PlayerProgress,
            [Character.Key] = Character,
            [World.Key] = World,
            [Selection.Key] = Selection,
            [Facts.Key] = Facts,
            [QuestStatuses.Key] = QuestStatuses,
            [States.Key] = States,
            [Inventory.Key] = Inventory,
            [Reputation.Key] = Reputation,
            [Telemetry.Key] = Telemetry,
            [Environment.Key] = Environment,
            [System.Key] = System,
            [Interfaces.Key] = Interfaces
        };

        foreach (var channel in _channels.Values)
        {
            Subscribe(channel);
        }
    }

    public DataChannel<PlayerState> Player { get; }
    public DataChannel<PlayerVitalsState> PlayerVitals { get; }
    public DataChannel<PlayerProgressState> PlayerProgress { get; }
    public DataChannel<CharacterState> Character { get; }
    public DataChannel<WorldState> World { get; }
    public DataChannel<WorldSelectionState> Selection { get; }
    public DataChannel<FactState> Facts { get; }
    public DataChannel<QuestStatusesState> QuestStatuses { get; }
    public DataChannel<RuntimeStatesState> States { get; }
    public DataChannel<InventoryState> Inventory { get; }
    public DataChannel<ReputationState> Reputation { get; }
    public DataChannel<TelemetryState> Telemetry { get; }
    public DataChannel<EnvironmentState> Environment { get; }
    public DataChannel<SystemState> System { get; }
    public DataChannel<InterfaceState> Interfaces { get; }

    public EventChannel<SimulatorEvent> Events { get; } = new();
    IEventChannel<SimulatorEvent> IDataChannelHub.Events => Events;

    public IDataChannel<T> Get<T>(string key)
    {
        if (!_channels.TryGetValue(key, out var channel) || channel is not IDataChannel<T> typed)
        {
            throw new KeyNotFoundException($"Канал «{key}» типа «{typeof(T).Name}» не найден.");
        }

        return typed;
    }

    public void Reset()
    {
        Player.Set(_initialPlayer, "Сброс симулятора");
        World.Set(_initialWorld, "Сброс симулятора");
        Selection.Set(new WorldSelectionState(null, "Сброс симулятора"), "Сброс симулятора");
        Facts.Set(new FactState(new Dictionary<string, string>
        {
            ["quest.ruslan.introductionSeen"] = "false",
            ["world.marketOpen"] = "true",
            ["player.hasLicense"] = "A"
        }), "Сброс симулятора");
        QuestStatuses.Set(new QuestStatusesState(new[]
        {
            new QuestStatusEntry("tutorial_ruslan_shashlik", QuestStatus.Available, "available"),
            new QuestStatusEntry("gosha_homemade_sausage", QuestStatus.Available, "available"),
            new QuestStatusEntry("ruslan_shashlik_delivery", QuestStatus.Available, "available")
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
        PlayerVitals.Set(new PlayerVitalsState(100, 100, 100, 100, 100, 100, 0, 100), "Сброс симулятора");
        PlayerProgress.Set(new PlayerProgressState(1500, 0, 0), "Сброс симулятора");
        Character.Set(
            new CharacterState(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["strength"] = 5,
                    ["perception"] = 5,
                    ["endurance"] = 5,
                    ["charisma"] = 5,
                    ["intelligence"] = 5,
                    ["agility"] = 5,
                    ["luck"] = 5
                },
                new[]
                {
                    new CharacterSkillState("ce-driver", "Автошкольник", "Получил права категории CE.", true, "CE"),
                    new CharacterSkillState("field-repair", "Очумелые ручки", "Навыки полевого ремонта базового уровня.", true, "Базовый")
                },
                Array.Empty<string>(),
                Array.Empty<string>()),
            "Сброс симулятора");
        Inventory.Set(new InventoryState(new Dictionary<string, int>
        {
            ["ruslan.raw_meat"] = 0
        }), "Сброс симулятора");
        Reputation.Set(ReputationState.ForNpcs(NpcCatalogFactory.CreateStarter()), "Сброс симулятора");
        Telemetry.Set(new TelemetryState(0, 800, 0, 0, 0, 78, 82, 34, 0, 0, 0, 0, false), "Сброс симулятора");
        Environment.Set(new EnvironmentState("Ясно", 0, "12:30", 5000), "Сброс симулятора");
        System.Set(new SystemState(true, "Симулятор", "", "Симуляция сброшена"), "Сброс симулятора");
        Interfaces.Set(new InterfaceState(null), "Сброс симулятора");
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
            PlayerVitals.Value,
            PlayerProgress.Value,
            Character.Value,
            World.Value,
            Selection.Value,
            Facts.Value,
            QuestStatuses.Value,
            States.Value,
            Inventory.Value,
            Reputation.Value,
            Telemetry.Value,
            Environment.Value,
            System.Value,
            Interfaces.Value);

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
            inventory.Changed += (_, args) =>
            {
                PublishTransition("inventory", args.Source, "Изменён инвентарь");
                PublishInventoryChanges(args.PreviousValue, args.CurrentValue, args.Source);
            };
        }
        else if (channel is DataChannel<PlayerVitalsState> vitals)
        {
            vitals.Changed += (_, args) => PublishTransition("player-vitals", args.Source, "Изменены потребности игрока");
        }
        else if (channel is DataChannel<PlayerProgressState> progress)
        {
            progress.Changed += (_, args) => PublishTransition("player-progress", args.Source, "Изменены деньги/опыт");
        }
        else if (channel is DataChannel<CharacterState> character)
        {
            character.Changed += (_, args) => PublishTransition("character", args.Source, "Изменены данные персонажа");
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
        else if (channel is DataChannel<WorldSelectionState> selection)
        {
            selection.Changed += (_, args) =>
                PublishTransition("world-selection", args.Source, args.CurrentValue.Point is null
                    ? "Снято выделение точки"
                    : $"Выбрана СДО-точка {args.CurrentValue.Point.Id}");
        }
        else if (channel is DataChannel<SystemState> system)
        {
            system.Changed += (_, args) => PublishTransition("system", args.Source, "Изменено состояние системы");
        }
        else if (channel is DataChannel<InterfaceState> interfaces)
        {
            interfaces.Changed += (_, args) => PublishTransition(
                "interfaces",
                args.Source,
                args.CurrentValue.ActiveDialog is null
                    ? "Интерфейсный запрос закрыт"
                    : $"Открыт диалог выбора {args.CurrentValue.ActiveDialog.RequestId}");
        }
    }

    private void PublishInventoryChanges(InventoryState previous, InventoryState current, string source)
    {
        var ids = previous.Items.Keys
            .Concat(current.Items.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var itemId in ids)
        {
            previous.Items.TryGetValue(itemId, out var before);
            current.Items.TryGetValue(itemId, out var after);
            var delta = after - before;
            if (delta == 0)
            {
                continue;
            }

            Events.Publish(new SimulatorEvent(
                "InventoryChanged",
                DateTimeOffset.UtcNow,
                source,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["itemId"] = itemId,
                    ["previousCount"] = before.ToString(CultureInfo.InvariantCulture),
                    ["currentCount"] = after.ToString(CultureInfo.InvariantCulture),
                    ["delta"] = delta.ToString(CultureInfo.InvariantCulture)
                }));
        }
    }

    private void PublishTransition(string channel, string source, string description)
    {
        Events.Publish(new SimulatorEvent(
            "ChannelChanged",
            DateTimeOffset.UtcNow,
            source,
            new Dictionary<string, string>
            {
                ["channel"] = channel,
                ["description"] = description
            }));
    }

    private static IReadOnlyList<WorldPoint> CreateFallbackWorld() =>
    [
        new WorldPoint("fallback-ruslan", "Руслан", "Квестовая точка", new WorldCoordinate(180, 0, 80))
        {
            Color = "#ff7a50"
        },
        new WorldPoint("fallback-gosha", "Гоша", "Квестовая точка", new WorldCoordinate(430, 0, -120))
        {
            Color = "#ff7a50"
        },
        new WorldPoint("fallback-yard", "Испытательный двор", "Локация", new WorldCoordinate(-140, 0, -60))
        {
            Color = "#12abe5"
        },
        new WorldPoint("fallback-village", "Деревня", "Локация", new WorldCoordinate(30, 0, 260))
        {
            Color = "#5fd08a"
        }
    ];

    private static WorldCoordinate GetWorldCenter(IReadOnlyList<WorldPoint> points)
    {
        if (points.Count == 0)
        {
            return new WorldCoordinate(0, 0, 0);
        }

        var minX = points.Min(x => x.Position.X);
        var maxX = points.Max(x => x.Position.X);
        var minY = points.Min(x => x.Position.Y);
        var maxY = points.Max(x => x.Position.Y);
        var minZ = points.Min(x => x.Position.Z);
        var maxZ = points.Max(x => x.Position.Z);

        return new WorldCoordinate(
            (minX + maxX) / 2,
            (minY + maxY) / 2,
            (minZ + maxZ) / 2);
    }
}
