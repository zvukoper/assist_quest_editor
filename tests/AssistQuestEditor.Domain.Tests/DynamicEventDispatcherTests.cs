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

        var director = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition },
            new Random(1));

        MovePlayer(hub, 0);
        director.SetSimulationRunning(true);
        MovePlayer(hub, 99);
        director.Tick();
        Assert.Empty(director.State.Instances);

        MovePlayer(hub, 101);
        director.Tick();

        var instance = Assert.Single(director.State.Instances);
        Assert.Equal("cache", instance.DefinitionId);
        Assert.Equal("cache-a", instance.Point.Id);
        Assert.Contains(
            director.State.Schedules,
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

        var director = new DynamicEventDispatcher(
            hub,
            resolver,
            () => new[] { definition });

        Assert.True(director.TrySpawn("cache"));
        Assert.True(director.TrySpawn("cache"));

        var instances = director.State.Instances;
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

        var director = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        director.SetSimulationRunning(true);
        MovePlayer(hub, 100);
        director.Tick();

        Assert.Single(director.State.Instances);

        MovePlayer(hub, 200);
        director.Tick();

        var schedule = Assert.Single(director.State.Schedules);
        Assert.True(schedule.TriggerPending);
        Assert.Equal(100, schedule.DistanceBudgetMeters);

        Assert.True(director.Complete(
            director.State.Instances[0].InstanceId));

        director.Tick();

        Assert.Equal(2, director.State.Instances.Count(item =>
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

        var director = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        director.SetSimulationRunning(true);

        hub.Events.Publish(new SimulatorEvent(
            "HornPressed",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string>()));

        Assert.Empty(director.State.Instances);

        hub.Events.Publish(new SimulatorEvent(
            "PlayerStopped",
            DateTimeOffset.UtcNow,
            "Test",
            new Dictionary<string, string>()));

        Assert.Single(director.State.Instances);
        Assert.Equal("hitch", director.State.Instances[0].DefinitionId);
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

        var director = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        Assert.True(director.TrySpawn("cache"));
        var start = hub.Get<WorldClockState>("sim-time").Value;

        hub.Get<WorldClockState>("sim-time").Set(
            start with { Elapsed = start.Elapsed + TimeSpan.FromHours(2) },
            "Test");

        director.SetSimulationRunning(true);
        director.Tick();

        var instance = Assert.Single(director.State.Instances);
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

        var director = new DynamicEventDispatcher(
            hub,
            new FakeLocationResolver(point),
            () => new[] { definition });

        Assert.True(director.TrySpawn("cache"));
        Assert.Single(director.State.Instances);

        director.Reset();

        Assert.Empty(director.State.Instances);
        Assert.Single(director.State.Schedules);
        Assert.Equal(100, director.State.Schedules[0].NextDistanceThresholdMeters);
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
