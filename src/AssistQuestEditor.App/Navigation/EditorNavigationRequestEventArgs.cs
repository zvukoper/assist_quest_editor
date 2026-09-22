namespace AssistQuestEditor.App;

public sealed class EditorNavigationRequestEventArgs : EventArgs
{
    public EditorNavigationRequestEventArgs(string action, string nodeId, string? sceneId = null)
    {
        Action = action;
        NodeId = nodeId;
        SceneId = sceneId;
    }

    public string Action { get; }
    public string NodeId { get; }
    public string? SceneId { get; }
}
