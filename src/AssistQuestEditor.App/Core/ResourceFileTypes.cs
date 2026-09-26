namespace AssistQuestEditor.App;

using AssistQuestEditor.Domain;

/// <summary>
/// Зарегистрированный в системе тип файла Assist Quest.
///
/// Собственного имени иконки здесь НЕТ: специконки для внутренних расширений
/// не рисуются, и все типы получают иконку приложения (см.
/// <see cref="AppIconService"/>). Прежнее поле <c>IconFileName</c> было бы
/// полем, которое никто не читает, — а такой "бездействующий" параметр
/// приглашает снова нарисовать отдельную иконку и снова разойтись с логотипом.
/// </summary>
public sealed record ResourceFileType(
    string Extension,
    string Kind,
    string FriendlyName,
    string ProgIdPart,
    string DefaultFileName,
    string Description);

public static class ResourceFileTypes
{
    // Канонические standalone resources редактора.
    // Dialogue/Choice сейчас являются частью .aqscene и отдельными файлами не являются.
    public static readonly IReadOnlyList<ResourceFileType> All =
    [
        // Мир — ресурс ПЕРВОГО уровня: без него нет ни кампании, ни квеста.
        // Прежде он был исключён из списка как "контейнер, а не файл", но
        // `world.aqworld` — такой же канонический файл ресурса, как остальные, и
        // без регистрации он открывался как "неизвестный тип".
        new(
            ".aqworld",
            "World",
            "Assist Quest World",
            "World",
            WorldPaths.WorldFileName,
            "Канонический World Definition. Внутри мира живут кампании, в кампаниях — квесты."),
        new(
            ".aqcampaign",
            "Campaign",
            "Assist Quest Campaign",
            "Campaign",
            "campaign.aqcampaign",
            "Канонический Campaign Definition. Содержит состав кампании и статусы квестов."),
        new(
            ".aqquest",
            "Quest",
            "Assist Quest",
            "Quest",
            "quest.aqquest",
            "Канонический Quest Definition. Открывается в Нодовом редакторе."),
        new(
            ".aqlocation",
            "Location",
            "Assist Quest Location",
            "Location",
            "location.aqlocation",
            "Канонический Location Definition. Может быть фиксированным или динамически разрешаться по пространственным критериям."),
        new(
            ".aqevent",
            "DynamicEvent",
            "Assist Quest Dynamic Event",
            "DynamicEvent",
            "event.aqevent",
            "Каноническое правило появления динамического события: триггер, политика генерации и ссылка на Location."),
        new(
            ".aqscene",
            "Scene",
            "Assist Quest Scene",
            "Scene",
            "scene.aqscene",
            "Канонический Scene Definition. Открывается в Редакторе сцен и диалогов."),
        new(
            ".aqewaypoints",
            "Route",
            "Assist Quest Route",
            "Route",
            "route.aqewaypoints",
            "Маршрут путевых точек и построенных дорожных линий Симулятора."),
        // Архив — не ресурс, а УПАКОВКА ресурсов: его не открывают в редакторе,
        // а импортируют. Вид "Archive" и обрабатывается отдельно (диалог импорта
        // вместо открытия документа), иначе двойной клик пытался бы прочитать
        // zip как определение квеста.
        new(
            WorldArchiveRules.Extension,
            "Archive",
            "Assist Quest Archive",
            "Archive",
            WorldArchiveRules.DemoWorldFileName,
            "Архив Assist Quest: сжатый пакет мира, кампании или квеста. " +
            "Открывается диалогом импорта.")
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
