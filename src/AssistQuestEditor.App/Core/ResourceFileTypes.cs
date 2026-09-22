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
    public static readonly IReadOnlyList<ResourceFileType> All =
    [
        new(".aqquest", "Quest", "Assist Quest", "Quest", "aqquest.ico", "quest.aqquest", "Канонический Quest Definition."),
        new(".aqscene", "Scene", "Assist Quest Scene", "Scene", "aqscene.ico", "scene.aqscene", "Канонический Scene Definition."),
        new(".aqdialogue", "Dialogue", "Assist Quest Dialogue", "Dialogue", "aqdialogue.ico", "dialogue.aqdialogue", "Диалоговый ресурс, на который ссылаются Scene."),
        new(".aqcampaign", "Campaign", "Assist Quest Campaign", "Campaign", "aqcampaign.ico", "campaign.aqcampaign", "Контейнер/описание большой сюжетной кампании."),
        new(".aqpoint", "WorldPoint", "Assist Quest World Point", "Point", "aqpoint.ico", "point.aqpoint", "Точка игрового мира с координатами и метаданными."),
        new(".aqcity", "City", "Assist Quest City", "City", "aqcity.ico", "city.aqcity", "Справочный ресурс города игрового мира."),
        new(".aqitem", "Item", "Assist Quest Item", "Item", "aqitem.ico", "item.aqitem", "Определение предмета."),
        new(".aqloc", "Localization", "Assist Quest Localization", "Localization", "aqloc.ico", "localization.aqloc", "Локализуемые строки проекта."),
        new(".aqregistry", "NodeRegistry", "Assist Quest Node Registry", "NodeRegistry", "aqregistry.ico", "nodes.aqregistry", "Схемы и метаданные доступных нод."),
        new(".aqsnapshot", "RuntimeSnapshot", "Assist Quest Runtime Snapshot", "RuntimeSnapshot", "aqsnapshot.ico", "snapshot.aqsnapshot", "Снимок состояния для Simulator/Runtime, не авторский ресурс."),
        new(".aqresource", "Resource", "Assist Quest Resource", "Resource", "aqresource.ico", "resource.aqresource", "Зарезервированный общий тип ресурса.")
    ];

    public static ResourceFileType Get(string extension)
    {
        var normalized = NormalizeExtension(extension);
        return All.FirstOrDefault(item => item.Extension.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Неизвестное расширение Assist Quest: " + extension);
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
        if (value.Length == 0) return string.Empty;
        return value.StartsWith(".", StringComparison.Ordinal) ? value.ToLowerInvariant() : "." + value.ToLowerInvariant();
    }

    public static string Filter(ResourceFileType resource) =>
        resource.FriendlyName + " (*" + resource.Extension + ")|*" + resource.Extension +
        "|Все файлы (*.*)|*.*";
}
