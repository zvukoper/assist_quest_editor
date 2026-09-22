namespace AssistQuestEditor.App;

public sealed class EditorNavigationRequestEventArgs : EventArgs
{
    public EditorNavigationRequestEventArgs(
        string action,
        string nodeId,
        string? sceneId = null,
        string? questId = null,
        string? questPath = null)
    {
        Action = action;
        NodeId = nodeId;
        SceneId = sceneId;
        QuestId = questId;
        QuestPath = questPath;
    }

    public string Action { get; }
    public string NodeId { get; }
    public string? SceneId { get; }
    public string? QuestId { get; }
    public string? QuestPath { get; }
}
