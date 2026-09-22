namespace AssistQuestEditor.Domain;

public sealed class SceneDocumentSession
{
    public SceneDocumentSession(string sceneId, string? currentPath, string? lastPath)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            throw new ArgumentException("Scene ID обязателен.", nameof(sceneId));

        CurrentSceneId = sceneId;
        CurrentPath = currentPath;
        LastPath = lastPath;
    }

    public string CurrentSceneId { get; private set; }
    public string? CurrentPath { get; private set; }
    public string? LastPath { get; private set; }
    public bool IsDirty { get; private set; }

    public event EventHandler? Changed;

    public void ObserveScene(string sceneId, string? defaultPath)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            throw new ArgumentException("Scene ID обязателен.", nameof(sceneId));

        if (!string.Equals(CurrentSceneId, sceneId, StringComparison.OrdinalIgnoreCase))
        {
            CurrentSceneId = sceneId;
            CurrentPath = defaultPath;
            IsDirty = false;
            RaiseChanged();
            return;
        }

        if (!IsDirty)
        {
            IsDirty = true;
            RaiseChanged();
        }
    }

    public void MarkNew(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            throw new ArgumentException("Scene ID обязателен.", nameof(sceneId));

        SetState(sceneId, null, false);
    }

    public void Opened(string sceneId, string path)
    {
        ValidatePath(sceneId, path);
        LastPath = path;
        SetState(sceneId, path, false);
    }

    public void Saved(string sceneId, string path)
    {
        ValidatePath(sceneId, path);
        LastPath = path;
        SetState(sceneId, path, false);
    }

    public void DiscardChanges()
    {
        if (!IsDirty)
            return;

        IsDirty = false;
        RaiseChanged();
    }

    private void SetState(string sceneId, string? path, bool dirty)
    {
        var changed =
            !string.Equals(CurrentSceneId, sceneId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase) ||
            IsDirty != dirty;

        CurrentSceneId = sceneId;
        CurrentPath = path;
        IsDirty = dirty;

        if (changed)
            RaiseChanged();
    }

    private static void ValidatePath(string sceneId, string path)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            throw new ArgumentException("Scene ID обязателен.", nameof(sceneId));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Путь к Scene resource обязателен.", nameof(path));
    }

    private void RaiseChanged() =>
        Changed?.Invoke(this, EventArgs.Empty);
}