using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class RecentFileListTests
{
    private static string P(string name) =>
        Path.Combine(Path.GetTempPath(), "aqrecent", name);

    [Fact]
    public void TouchPutsNewestFirst()
    {
        var list = new RecentFileList();

        Assert.True(list.Touch(P("a.aqquest")));
        Assert.True(list.Touch(P("b.aqscene")));
        Assert.True(list.Touch(P("c.aqquest")));

        Assert.Equal(
            new[] { Path.GetFullPath(P("c.aqquest")), Path.GetFullPath(P("b.aqscene")), Path.GetFullPath(P("a.aqquest")) },
            list.Items);
    }

    [Fact]
    public void TouchExistingPathMovesItToTopWithoutDuplicating()
    {
        var list = new RecentFileList();
        list.Touch(P("a.aqquest"));
        list.Touch(P("b.aqquest"));
        list.Touch(P("c.aqquest"));

        Assert.True(list.Touch(P("a.aqquest")));

        Assert.Equal(3, list.Count);
        Assert.Equal(Path.GetFullPath(P("a.aqquest")), list.Items[0]);
        Assert.Single(list.Items, path => path.Equals(Path.GetFullPath(P("a.aqquest")), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TouchNewestAgainReportsNoChange()
    {
        var list = new RecentFileList();
        list.Touch(P("a.aqquest"));

        // Повторное открытие текущего файла не должно дёргать UI и настройки.
        Assert.False(list.Touch(P("a.aqquest")));
    }

    [Fact]
    public void PathComparisonIsCaseInsensitive()
    {
        var list = new RecentFileList();
        var path = Path.Combine(Path.GetTempPath(), "AqRecent", "Mixed.aqscene");
        list.Touch(path);

        Assert.False(list.Touch(path.ToUpperInvariant()));
        Assert.Single(list.Items);
    }

    [Fact]
    public void KeepsOnlyConfiguredCapacity()
    {
        var list = new RecentFileList(capacity: 10);

        for (var i = 0; i < 15; i++)
            list.Touch(P($"file{i}.aqquest"));

        Assert.Equal(10, list.Count);
        Assert.Equal(Path.GetFullPath(P("file14.aqquest")), list.Items[0]);
        // Старейшие вытесняются.
        Assert.DoesNotContain(list.Items, path => path.EndsWith("file4.aqquest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultCapacityIsTen()
    {
        var list = new RecentFileList();

        for (var i = 0; i < 20; i++)
            list.Touch(P($"f{i}.aqquest"));

        Assert.Equal(RecentFileList.DefaultCapacity, list.Capacity);
        Assert.Equal(10, list.Count);
    }

    [Fact]
    public void BlankAndInvalidPathsAreIgnored()
    {
        var list = new RecentFileList();

        Assert.False(list.Touch(null));
        Assert.False(list.Touch(""));
        Assert.False(list.Touch("   "));
        Assert.False(list.Touch("bad\0name.aqquest"));

        Assert.Empty(list.Items);
    }

    [Fact]
    public void FromPathsKeepsSavedOrderAndDropsDuplicates()
    {
        var saved = new[] { P("newest.aqquest"), P("middle.aqscene"), P("middle.aqscene"), P("oldest.aqquest") };

        var list = RecentFileList.FromPaths(saved);

        Assert.Equal(3, list.Count);
        Assert.Equal(Path.GetFullPath(P("newest.aqquest")), list.Items[0]);
        Assert.Equal(Path.GetFullPath(P("oldest.aqquest")), list.Items[2]);
    }

    [Fact]
    public void FromPathsRespectsCapacity()
    {
        var saved = Enumerable.Range(0, 30).Select(i => P($"f{i}.aqquest")).ToArray();

        var list = RecentFileList.FromPaths(saved, capacity: 10);

        Assert.Equal(10, list.Count);
    }

    [Fact]
    public void RemoveDropsEntryAndRaisesChanged()
    {
        var list = new RecentFileList();
        list.Touch(P("a.aqquest"));
        list.Touch(P("b.aqquest"));

        var raised = 0;
        list.Changed += (_, _) => raised++;

        Assert.True(list.Remove(P("a.aqquest")));
        Assert.Equal(1, raised);
        Assert.Single(list.Items);

        // Повторное удаление ничего не меняет.
        Assert.False(list.Remove(P("a.aqquest")));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void PruneMissingUsesInjectedPredicate()
    {
        var list = new RecentFileList();
        list.Touch(P("keep.aqquest"));
        list.Touch(P("gone.aqquest"));

        Assert.True(list.PruneMissing(path => path.EndsWith("keep.aqquest", StringComparison.OrdinalIgnoreCase)));

        Assert.Single(list.Items);
        Assert.Equal(Path.GetFullPath(P("keep.aqquest")), list.Items[0]);
    }

    [Fact]
    public void PruneMissingReportsNoChangeWhenEverythingExists()
    {
        var list = new RecentFileList();
        list.Touch(P("a.aqquest"));

        Assert.False(list.PruneMissing(_ => true));
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenListActuallyChanges()
    {
        var list = new RecentFileList();
        var raised = 0;
        list.Changed += (_, _) => raised++;

        list.Touch(P("a.aqquest"));
        Assert.Equal(1, raised);

        list.Touch(P("a.aqquest"));
        Assert.Equal(1, raised);

        list.Clear();
        Assert.Equal(2, raised);

        list.Clear();
        Assert.Equal(2, raised);
    }

    [Fact]
    public void CapacityMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentFileList(0));
    }
}
