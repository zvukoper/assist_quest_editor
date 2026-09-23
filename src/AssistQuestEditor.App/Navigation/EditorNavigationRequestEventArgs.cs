namespace AssistQuestEditor.App;

public sealed class EditorNavigationRequestEventArgs : EventArgs
{
    public EditorNavigationRequestEventArgs(
        string action,
        string? nodeId = null,
        string? sceneId = null,
        string? questId = null,
        string? questPath = null,
        string? editor = null)
    {
        Action = action;
        NodeId = nodeId ?? string.Empty;
        SceneId = sceneId;
        QuestId = questId;
        QuestPath = questPath;
        Editor = editor;
    }

    public string Action { get; }
    public string NodeId { get; }
    public string? SceneId { get; }
    public string? QuestId { get; }
    public string? QuestPath { get; }
    public string? Editor { get; }
}
