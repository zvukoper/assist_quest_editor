namespace AssistQuestEditor.App;

public static class FileActivationRequest
{
    private static string? _path;

    public static void Set(string? path) =>
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    public static string? Consume()
    {
        var path = _path;
        _path = null;
        return path;
    }
}
