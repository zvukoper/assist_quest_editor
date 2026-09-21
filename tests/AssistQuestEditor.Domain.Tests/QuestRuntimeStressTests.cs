using System.Diagnostics;
using AssistQuestEditor.Domain;
using Xunit;
using Xunit.Abstractions;

namespace AssistQuestEditor.Domain.Tests;

public sealed class QuestRuntimeStressTests
{
    private readonly ITestOutputHelper _output;

    public QuestRuntimeStressTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Stress_ConcurrentEventWaiters_ScalesAcrossRuntimeCounts()
    {
        var counts = ReadCounts();

        // Warm up JIT and one complete runtime before measuring the actual tiers.
        _ = RunStress(1);

        _output.WriteLine("=== Quest Runtime stress test ===");
        _output.WriteLine($"Тиры: {string.Join(", ", counts)}");

        foreach (var count in counts)
        {
            var result = RunStress(count);

            Assert.Equal(count, result.WaitingBeforeEvent);
            Assert.Equal(count, result.CompletedAfterMatchingEvent);
            Assert.Equal(count + 1, result.FinalQuestStatusCount);

            _output.WriteLine(
                $"N={count}; create={result.CreateMilliseconds:F2} ms; " +
                $"start={result.StartMilliseconds:F2} ms; noise={result.NoiseEventMilliseconds:F2} ms; " +
                $"match={result.MatchingEventMilliseconds:F2} ms; total={result.TotalMilliseconds:F2} ms; " +
                $"allocated={result.ManagedAllocatedMegabytes:F2} MB; " +
                $"managedHeapAfter={result.ManagedHeapAfterMegabytes:F2} MB; " +
                $"workingSetAfter={result.WorkingSetAfterMegabytes:F2} MB");
        }
    }

    private static StressResult RunStress(int runtimeCount)
    {
        ForceGc();

        var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var totalWatch = Stopwatch.StartNew();

        var createWatch = Stopwatch.StartNew();
        var adapter = new SimulatorDataSourceAdapter(Array.Empty<WorldPoint>());
        var hub = adapter.Channels;
        var runtimes = new List<QuestRuntime>(runtimeCount);

        for (var index = 0; index < runtimeCount; index++)
        {
            runtimes.Add(CreateWaitingRuntime(hub, index));
        }

        createWatch.Stop();

        var startWatch = Stopwatch.StartNew();
        foreach (var runtime in runtimes)
        {
            runtime.Start();
        }

        startWatch.Stop();

        var waitingBeforeEvent = runtimes.Count(runtime =>
            runtime.State.Status == QuestRuntimeStatus.Waiting &&
            string.Equals(runtime.State.WaitingFor, "Event:StressPulse", StringComparison.Ordinal));

        var noiseWatch = Stopwatch.StartNew();
        hub.Events.Publish(new SimulatorEvent(
            "StressNoise",
            DateTimeOffset.UtcNow,
            "StressTest",
            new Dictionary<string, string>()));
        noiseWatch.Stop();

        var matchingWatch = Stopwatch.StartNew();
        hub.Events.Publish(new SimulatorEvent(
            "StressPulse",
            DateTimeOffset.UtcNow,
            "StressTest",
            new Dictionary<string, string>()));
        matchingWatch.Stop();

        totalWatch.Stop();

        var completedAfterMatchingEvent = runtimes.Count(runtime =>
            runtime.State.Status == QuestRuntimeStatus.Completed);

        var finalQuestStatusCount = hub.Get<QuestStatusesState>("quest-statuses").Value.Quests.Count;

        ForceGc();
        process.Refresh();

        return new StressResult(
            runtimeCount,
            createWatch.Elapsed.TotalMilliseconds,
            startWatch.Elapsed.TotalMilliseconds,
            noiseWatch.Elapsed.TotalMilliseconds,
            matchingWatch.Elapsed.TotalMilliseconds,
            totalWatch.Elapsed.TotalMilliseconds,
            waitingBeforeEvent,
            completedAfterMatchingEvent,
            finalQuestStatusCount,
            (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / 1024d / 1024d,
            GC.GetTotalMemory(false) / 1024d / 1024d,
            process.WorkingSet64 / 1024d / 1024d);
    }

    private static QuestRuntime CreateWaitingRuntime(IDataChannelHub hub, int index)
    {
        var questId = $"stress-quest-{index:D5}";
        var start = new QuestNode(
            "start",
            "Start",
            "Start",
            0,
            0,
            QuestNodeCatalog.CreateSockets("Start", "start"));

        var waitParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eventType"] = "StressPulse"
        };

        var wait = new QuestNode(
            "wait",
            "WaitForEvent",
            "WaitForEvent",
            0,
            0,
            QuestNodeCatalog.CreateSockets("WaitForEvent", "wait", waitParameters))
        {
            Parameters = waitParameters
        };

        var end = new QuestNode(
            "end",
            "End",
            "End",
            0,
            0,
            QuestNodeCatalog.CreateSockets("End", "end"));

        var graph = new QuestGraph(
            questId,
            $"Stress Quest {index:D5}",
            new[] { start, wait, end },
            new[]
            {
                new QuestConnection("start", "start.out", "wait", "wait.in"),
                new QuestConnection("wait", "wait.out", "end", "end.in")
            });

        return new QuestRuntime(new QuestGraphStore(graph), hub);
    }

    private static int[] ReadCounts()
    {
        var raw = Environment.GetEnvironmentVariable("ASSIST_QUEST_STRESS_COUNTS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [100, 250, 500];
        }

        var counts = raw
            .Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Where(value => value > 0 && value <= 10000)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        return counts.Length > 0 ? counts : [100, 250, 500];
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record StressResult(
        int RuntimeCount,
        double CreateMilliseconds,
        double StartMilliseconds,
        double NoiseEventMilliseconds,
        double MatchingEventMilliseconds,
        double TotalMilliseconds,
        int WaitingBeforeEvent,
        int CompletedAfterMatchingEvent,
        int FinalQuestStatusCount,
        double ManagedAllocatedMegabytes,
        double ManagedHeapAfterMegabytes,
        double WorkingSetAfterMegabytes);
}
