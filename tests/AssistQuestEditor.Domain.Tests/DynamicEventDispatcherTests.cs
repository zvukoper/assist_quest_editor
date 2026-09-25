using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class DynamicEventDispatcherTests
{
    [Fact]
    public void DistanceTriggerUsesAccumulatedBudgetAndRandomRange()
    {
        var point = Point("cache-a", "Тайник", "cache", 1000);
        var hub = new SimulatorDataChannelHub(new[] { point });

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            trigger: new DynamicEventTriggerDefinition
            {
                Type = "DistanceTravelled",
                MinDistanceMeters = 100,
                MaxDistanceMeters = 100
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition },
            new Random(1));

        MovePlayer(hub, 0);
        dispatcher.SetSimulationRunning(true);
        MovePlayer(hub, 99);
        dispatcher.Tick();
        Assert.Empty(dispatcher.State.Instances);

        MovePlayer(hub, 101);
        dispatcher.Tick();

        var instance = Assert.Single(dispatcher.State.Instances);
        Assert.Equal("cache", instance.DefinitionId);
        Assert.Equal("cache-a", instance.Point.Id);
        Assert.Contains(
            dispatcher.State.Schedules,
            schedule => schedule.DefinitionId == "cache" &&
                schedule.DistanceBudgetMeters == 1);
    }

    [Fact]
    public void ManualSpawnsUsingSameDynamicLocationGetIndependentResolutionScopes()
    {
        var first = Point("cache-1", "Тайник 1", "cache", 100);
        var second = Point("cache-2", "Тайник 2", "cache", 200);
        var resolver = new ScopedFakeLocationResolver(first, second);
        var hub = new SimulatorDataChannelHub(new[] { first, second });

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            trigger: new DynamicEventTriggerDefinition { Type = "Manual" },
            policy: new DynamicEventSpawnPolicy { MaxActiveInstances = 2 });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            resolver,
            () => new[] { definition });

        Assert.True(dispatcher.TrySpawn("cache"));
        Assert.True(dispatcher.TrySpawn("cache"));

        var instances = dispatcher.State.Instances;
        Assert.Equal(2, instances.Count);
        Assert.Equal(
            new[] { "cache-1", "cache-2" },
            instances.Select(item => item.Point.Id).ToArray());

        Assert.Equal(
            new[] { "cache#1", "cache#2" },
            resolver.ResolutionKeys);
    }

    [Fact]
    public void ActiveLimitLeavesDistanceTriggerPendingInsteadOfLosingDistance()
    {
        var point = Point("cache", "Тайник", "cache", 0);
        var hub = new SimulatorDataChannelHub(new[] { point });

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            trigger: new DynamicEventTriggerDefinition
            {
                Type = "DistanceTravelled",
                MinDistanceMeters = 100,
                MaxDistanceMeters = 100
            },
            policy: new DynamicEventSpawnPolicy
            {
                MaxActiveInstances = 1,
                RemoveOnCompleted = false
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        dispatcher.SetSimulationRunning(true);
        MovePlayer(hub, 100);
        dispatcher.Tick();

        Assert.Single(dispatcher.State.Instances);

        MovePlayer(hub, 200);
        dispatcher.Tick();

        var schedule = Assert.Single(dispatcher.State.Schedules);
        Assert.True(schedule.TriggerPending);
        Assert.Equal(100, schedule.DistanceBudgetMeters);

        Assert.True(dispatcher.Complete(
            dispatcher.State.Instances[0].InstanceId));

        dispatcher.Tick();

        Assert.Equal(2, dispatcher.State.Instances.Count(item =>
            item.DefinitionId == "cache"));
    }

    [Fact]
    public void WorldEventTriggerMatchesEventTypeAndPayload()
    {
        var point = Point("hitch", "Попутчик", "hitchhiker", 500);
        var hub = new SimulatorDataChannelHub(new[] { point });

        var definition = Definition(
            "hitch",
            "Попутчик",
            "hitch-location",
            trigger: new DynamicEventTriggerDefinition
            {
                Type = "WorldEvent",
                EventType = "PlayerStopped"
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        dispatcher.SetSimulationRunning(true);

        hub.Events.Publish(new SimulatorEvent(
            "HornPressed",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string>()));

        Assert.Empty(dispatcher.State.Instances);

        hub.Events.Publish(new SimulatorEvent(
            "PlayerStopped",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string>()));

        Assert.Single(dispatcher.State.Instances);
        Assert.Equal("hitch", dispatcher.State.Instances[0].DefinitionId);
    }

    [Fact]
    public void LifetimeExpiresActiveInstance()
    {
        var point = Point("cache", "Тайник", "cache", 100);
        var hub = new SimulatorDataChannelHub(new[] { point });

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            trigger: new DynamicEventTriggerDefinition { Type = "Manual" },
            policy: new DynamicEventSpawnPolicy
            {
                MaxActiveInstances = 1,
                LifetimeGameHours = 1
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        Assert.True(dispatcher.TrySpawn("cache"));
        var start = hub.Get<WorldClockState>("sim-time").Value;

        hub.Get<WorldClockState>("sim-time").Set(
            start with { Elapsed = start.Elapsed + TimeSpan.FromHours(2) },
            "Test");

        dispatcher.SetSimulationRunning(true);
        dispatcher.Tick();

        var instance = Assert.Single(dispatcher.State.Instances);
        Assert.Equal(DynamicEventInstanceStatus.Expired, instance.Status);
    }

    [Fact]
    public void ResetClearsInstancesAndReinitializesSchedules()
    {
        var point = Point("cache", "Тайник", "cache", 0);
        var hub = new SimulatorDataChannelHub(new[] { point });

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            trigger: new DynamicEventTriggerDefinition
            {
                Type = "DistanceTravelled",
                MinDistanceMeters = 100,
                MaxDistanceMeters = 100
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        Assert.True(dispatcher.TrySpawn("cache"));
        Assert.Single(dispatcher.State.Instances);

        dispatcher.Reset();

        Assert.Empty(dispatcher.State.Instances);
        Assert.Single(dispatcher.State.Schedules);
        Assert.Equal(100, dispatcher.State.Schedules[0].NextDistanceThresholdMeters);
    }

    [Fact]
    public void SimulationStartSpawnsInitialDispatcherEvent()
    {
        var point = Point("cache-1", "Тайник", "cache", 100);
        var hub = new SimulatorDataChannelHub(new[] { point });
        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            new DynamicEventTriggerDefinition
            {
                Type = "DynamicEventDiscovery",
                SourceDefinitionId = "cache",
                MinGameHours = 5d / 60d,
                MaxGameHours = 5d / 60d
            },
            new DynamicEventSpawnPolicy
            {
                SpawnOnSimulationStart = true,
                MaxActiveInstances = 1,
                LifetimeGameHours = 10d / 60d
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        dispatcher.SetSimulationRunning(true);

        var instance = Assert.Single(dispatcher.State.Instances);
        Assert.Equal(DynamicEventInstanceStatus.Active, instance.Status);
        Assert.Equal("cache-1", instance.Point.Id);
    }

    [Fact]
    public void DiscoverySchedulesNextSpawnAndStartsLinkedQuest()
    {
        var point = Point("cache-1", "Первый тайник", "cache", 100);
        var nextPoint = Point("cache-2", "Второй тайник", "cache", 200);
        var hub = new SimulatorDataChannelHub(new[] { point, nextPoint });
        var quest = new FakeQuestRuntime();
        var resolver = new ScopedFakeLocationResolver(point, nextPoint);

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            new DynamicEventTriggerDefinition
            {
                Type = "DynamicEventDiscovery",
                SourceDefinitionId = "cache",
                MinGameHours = 5d / 60d,
                MaxGameHours = 5d / 60d
            },
            new DynamicEventSpawnPolicy
            {
                SpawnOnSimulationStart = true,
                MaxActiveInstances = 1,
                LifetimeGameHours = 10d / 60d,
                RemoveOnCompleted = true
            }) with
            {
                QuestId = "cache-quest"
            };

        var dispatcher = new DynamicEventDispatcher(
            hub,
            resolver,
            () => new[] { definition },
            questRuntime: quest);

        dispatcher.SetSimulationRunning(true);
        MovePlayer(hub, 100);
        dispatcher.Tick();

        var discovered = Assert.Single(dispatcher.State.Instances);
        Assert.Equal(DynamicEventInstanceStatus.Discovered, discovered.Status);
        Assert.Null(quest.StartedQuestId);

        Assert.True(dispatcher.Activate(discovered.InstanceId));
        Assert.Equal("cache-quest", quest.StartedQuestId);

        var schedule = Assert.Single(dispatcher.State.Schedules);
        Assert.True(schedule.NextGameElapsed.HasValue);

        var currentClock = hub.Get<WorldClockState>("sim-time").Value;
        hub.Get<WorldClockState>("sim-time").Set(
            currentClock with
            {
                Elapsed = currentClock.Elapsed + TimeSpan.FromMinutes(5)
            },
            "Test");

        dispatcher.Tick();

        var spawnedAgain = Assert.Single(
            dispatcher.State.Instances.Where(item =>
                item.DefinitionId == "cache" &&
                item.Status == DynamicEventInstanceStatus.Active));
        Assert.Equal("cache-2", spawnedAgain.Point.Id);
    }

    [Fact]
    public void UnfoundEventExpiresAfterTenGameMinutesAndRespawnsElsewhere()
    {
        var first = Point("cache-1", "Первый тайник", "cache", 100);
        var second = Point("cache-2", "Новый тайник", "cache", 200);
        var hub = new SimulatorDataChannelHub(new[] { first, second });
        var resolver = new ScopedFakeLocationResolver(first, second);

        var definition = Definition(
            "cache",
            "Тайник",
            "cache-location",
            new DynamicEventTriggerDefinition
            {
                Type = "DynamicEventDiscovery",
                SourceDefinitionId = "cache"
            },
            new DynamicEventSpawnPolicy
            {
                SpawnOnSimulationStart = true,
                RespawnOnExpired = true,
                LifetimeGameHours = 10d / 60d,
                MaxActiveInstances = 1
            });

        var dispatcher = new DynamicEventDispatcher(
            hub,
            resolver,
            () => new[] { definition });

        dispatcher.SetSimulationRunning(true);
        var start = hub.Get<WorldClockState>("sim-time").Value;

        hub.Get<WorldClockState>("sim-time").Set(
            start with
            {
                Elapsed = start.Elapsed + TimeSpan.FromMinutes(10)
            },
            "Ten game minutes passed");

        dispatcher.Tick();

        var instances = dispatcher.State.Instances.ToArray();
        Assert.Contains(instances, item =>
            item.Point.Id == "cache-1" &&
            item.Status == DynamicEventInstanceStatus.Expired);
        Assert.Contains(instances, item =>
            item.Point.Id == "cache-2" &&
            item.Status == DynamicEventInstanceStatus.Active);
    }

    private static DynamicEventDefinition Definition(
        string id,
        string name,
        string locationId,
        DynamicEventTriggerDefinition trigger,
        DynamicEventSpawnPolicy? policy = null) =>
        new(id, name)
        {
            LocationId = locationId,
            Trigger = trigger,
            SpawnPolicy = policy ?? new DynamicEventSpawnPolicy()
        };

    private static WorldPoint Point(string id, string name, string category, double x) =>
        new(id, name, category, new WorldCoordinate(x, 0, 0));

    private static void MovePlayer(SimulatorDataChannelHub hub, double x)
    {
        var current = hub.Get<PlayerState>("player").Value;
        hub.Get<PlayerState>("player").Set(
            current with { Position = new WorldCoordinate(x, 0, 0) },
            "Test");
    }

    private sealed class FakeQuestRuntime : IQuestRuntimeController
    {
        public QuestRuntimeState State { get; } =
            new("fake", null, QuestRuntimeStatus.Stopped, null, string.Empty, string.Empty);

        public QuestGraph? ActiveGraph => null;
        public bool SimulationRunning => true;
        public bool IsPaused => false;
        public double SimulationSpeed => 1d;
        public IReadOnlyCollection<string> EnabledQuestIds => Array.Empty<string>();
        public string? StartedQuestId { get; private set; }

        public event EventHandler<QuestRuntimeEvent>? Published;

        public void Start() { }

        public bool StartQuest(string questId)
        {
            StartedQuestId = questId;
            return true;
        }

        public void Stop(string reason = "Runtime остановлен") { }
        public void Reset() { }
        public void Tick() { }
        public void SetSimulationRunning(bool running) { }
        public void PauseSimulation() { }
        public void ResumeSimulation() { }
        public void SetSimulationSpeed(double speed) { }
        public void SetQuestEnabled(string questId, bool enabled) { }

        public void Dispose()
        {
            Published = null;
        }
    }

    private sealed class FakeLocationResolver(WorldPoint point) : ILocationResolver
    {
        public WorldPoint? Resolve(string locationId) => point;
    }

    private sealed class ScopedFakeLocationResolver(
        WorldPoint first,
        WorldPoint second) : ILocationResolver
    {
        private int _counter;

        public List<string> ResolutionKeys { get; } = new();

        public WorldPoint? Resolve(string locationId) => first;

        public WorldPoint? Resolve(string locationId, string? resolutionKey)
        {
            ResolutionKeys.Add(resolutionKey ?? string.Empty);
            _counter++;
            return _counter == 1 ? first : second;
        }
    }
}
