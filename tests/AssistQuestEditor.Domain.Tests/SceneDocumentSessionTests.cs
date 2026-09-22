using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class SceneDocumentSessionTests
{
    [Fact]
    public void EditingCurrentSceneMarksSessionDirtyAndSavingClearsIt()
    {
        var session = new SceneDocumentSession(
            "ruslan_start",
            "data/scenes/ruslan_start.aqscene",
            "data/scenes/ruslan_start.aqscene");

        session.ObserveScene("ruslan_start", "data/scenes/ruslan_start.aqscene");

        Assert.True(session.IsDirty);
        Assert.Equal("data/scenes/ruslan_start.aqscene", session.CurrentPath);

        session.Saved("ruslan_start", "data/scenes/ruslan_start.aqscene");

        Assert.False(session.IsDirty);
        Assert.Equal("data/scenes/ruslan_start.aqscene", session.LastPath);
    }

    [Fact]
    public void SwitchingSceneChangesDocumentContextWithoutCarryingDirtyState()
    {
        var session = new SceneDocumentSession(
            "ruslan_start",
            "data/scenes/ruslan_start.aqscene",
            "data/scenes/ruslan_start.aqscene");

        session.ObserveScene("ruslan_start", "data/scenes/ruslan_start.aqscene");
        Assert.True(session.IsDirty);

        session.ObserveScene("gosha_meat", "data/scenes/gosha_meat.aqscene");

        Assert.Equal("gosha_meat", session.CurrentSceneId);
        Assert.Equal("data/scenes/gosha_meat.aqscene", session.CurrentPath);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void NewSceneHasNoCurrentPathButKeepsLastPathForOpenDialog()
    {
        var session = new SceneDocumentSession(
            "ruslan_start",
            "data/scenes/ruslan_start.aqscene",
            "data/scenes/ruslan_start.aqscene");

        session.MarkNew("new_scene");

        Assert.Equal("new_scene", session.CurrentSceneId);
        Assert.Null(session.CurrentPath);
        Assert.Equal("data/scenes/ruslan_start.aqscene", session.LastPath);
        Assert.False(session.IsDirty);
    }
}