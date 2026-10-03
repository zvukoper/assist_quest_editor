using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class Ets2SyncModelsTests
{
    [Fact]
    public void StableLineageDoesNotChangeWhenProfileNameChanges()
    {
        var created = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

        var first = Create("OldName", "41514", created, "414243");
        var renamed = Create("NewName", "41514", created, "444546");

        var a = Ets2ProfileLineage.FromProfile("76561198000000001", first);
        var b = Ets2ProfileLineage.FromProfile("76561198000000001", renamed);

        Assert.Equal(a.Id, b.Id);
        Assert.True(a.IsStable);
        Assert.Contains("414243", a.KnownHexFolders);
    }

    [Fact]
    public void DifferentCreationTimesProduceDifferentStableLineages()
    {
        var a = Ets2ProfileLineage.FromProfile(
            "76561198000000001",
            Create("Same", "41514", new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), "41"));
        var b = Ets2ProfileLineage.FromProfile(
            "76561198000000001",
            Create("Same", "41514", new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero), "41"));

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void SaveFingerprintContentKeyIncludesBothInfoAndGame()
    {
        var one = new Ets2SaveFingerprint(
            "autosave", "A", null, 10, 100, 20, 4, null, "info-a", "game-a");
        var two = one with { GameHash = "game-b" };

        Assert.NotEqual(one.ContentKey, two.ContentKey);
    }

    private static Ets2ProfileData Create(
        string name,
        string hex,
        DateTimeOffset created,
        string alias)
        => new(
            "steam_profiles",
            hex,
            name,
            name,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            created,
            created,
            null,
            Array.Empty<Ets2ModEntry>(),
            Array.Empty<Ets2ModEntry>(),
            Array.Empty<Ets2ModEntry>(),
            Array.Empty<Ets2SaveEntry>(),
            Array.Empty<string>());
}
