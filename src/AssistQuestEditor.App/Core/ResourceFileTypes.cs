namespace AssistQuestEditor.App;

public sealed record ResourceFileType(
    string Extension,
    string Kind,
    string FriendlyName,
    string ProgIdPart,
    string IconFileName,
    string DefaultFileName,
    string Description);

public static class ResourceFileTypes
{
    // Только реально существующие в текущем прототипе standalone resources.
    // Dialogue/Choice сейчас являются частью .aqscene и отдельными файлами не являются.
    public static readonly IReadOnlyList<ResourceFileType> All =
    [
        new(
            ".aqquest",
            "Quest",
            "Assist Quest",
            "Quest",
            "aqquest.ico",
            "quest.aqquest",
            "Канонический Quest Definition. Открывается в Нодовом редакторе."),
        new(
            ".aqscene",
            "Scene",
            "Assist Quest Scene",
            "Scene",
            "aqscene.ico",
            "scene.aqscene",
            "Канонический Scene Definition. Открывается в Редакторе сцен и диалогов.")
    ];

    public static ResourceFileType Get(string extension)
    {
        var normalized = NormalizeExtension(extension);
        return All.FirstOrDefault(item =>
                   item.Extension.Equals(normalized, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   "Неизвестное расширение Assist Quest: " + extension);
    }

    public static bool TryGet(string extension, out ResourceFileType resource)
    {
        var normalized = NormalizeExtension(extension);
        resource = All.FirstOrDefault(item =>
            item.Extension.Equals(normalized, StringComparison.OrdinalIgnoreCase))!;
        return resource is not null;
    }

    public static string NormalizeExtension(string extension)
    {
        var value = (extension ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        return value.StartsWith(".", StringComparison.Ordinal)
            ? value.ToLowerInvariant()
            : "." + value.ToLowerInvariant();
    }

    public static string Filter(ResourceFileType resource) =>
        resource.FriendlyName + " (*" + resource.Extension + ")|*" + resource.Extension +
        "|Все файлы (*.*)|*.*";
}
