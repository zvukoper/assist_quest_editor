using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Игровой мир — верхняя сущность хранения.
///
/// Аналог «проекта» в традиционных редакторах: у мира свои свойства, лор,
/// кампании и квесты. Квест не может существовать вне кампании, а кампания —
/// вне мира, поэтому мир является корнем дерева ресурсов и владельцем папки на
/// диске.
///
/// <see cref="CommonCampaignId"/> и <see cref="StartupCampaignId"/> не хранятся:
/// общая кампания каждого мира всегда создаётся под фиксированным id
/// (<see cref="WorldDefinition.CommonCampaignIdValue"/>), и это правило кода,
/// а не поле файла — иначе импортированный мир мог бы сослаться на чужой id.
/// </summary>
public sealed record WorldDefinition(
    string Id,
    string Name,
    // Название для человека; если пусто — показывается Name.
    string? FullName = null,
    string? Description = null,
    // Файл изображения внутри папки мира, например «world.png».
    string? ImageFile = null,
    // Кешбастинг и основание для решения о перезаписи при импорте.
    int Version = 1,
    ResourceMetadata? Metadata = null,
    // Id кампании, открытой последней. Пусто — берётся Common.
    string? LastCampaignId = null);

/// <summary>
/// Кампания: сюжет внутри мира — набор квестов плюс свойства мира «на входе».
///
/// Принадлежность миру (<see cref="WorldId"/>) записывается в файл, потому что
/// игрок может физически положить папку кампании не в тот мир. Это НЕ запрет, а
/// сигнал: список покажет оранжевое предупреждение о чужом родителе, но
/// работать не помешает — объект может быть перенесён осознанно.
/// </summary>
public sealed record CampaignWorldDefinition(
    string Id,
    string ParentWorldId,
    string Name,
    string? FullName = null,
    string? Description = null,
    string? ImageFile = null,
    int Version = 1,
    ResourceMetadata? Metadata = null)
{
    /// <summary>
    /// Совпадает ли объявленный родитель с фактическим.
    ///
    /// Сравнение без учёта регистра: id ресурсов в проекте регистронезависимы
    /// везде (сторе, ссылках, критериях), и требовать точного регистра только
    /// здесь означало бы ломать уже работающий контент.
    /// </summary>
    public bool BelongsTo(string worldId) =>
        string.IsNullOrWhiteSpace(ParentWorldId) ||
        ParentWorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Квест в контексте своего мира и кампании.
///
/// Хранится отдельно от <see cref="QuestDefinition"/>, потому что принадлежность
/// — это свойство МЕСТА файла, а не логики квеста. Runtime и граф о родителях не
/// знают и знать не должны.
/// </summary>
public sealed record QuestPlacement(
    string QuestId,
    string WorldId,
    string CampaignId)
{
    public bool BelongsTo(string worldId, string campaignId) =>
        (string.IsNullOrWhiteSpace(WorldId) ||
         WorldId.Equals(worldId, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(CampaignId) ||
         CampaignId.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
}

public sealed record WorldDefinitionDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Format,
    [property: JsonPropertyOrder(2)] WorldDefinition Definition);

public static class WorldDefinitionRules
{
    /// <summary>
    /// Формат документа мира. Отдельная строка, а не литерал в коде: то же
    /// значение читает загрузчик, и разойтись они не должны.
    /// </summary>
    public const string FormatName = "aqworld";

    /// <summary>
    /// Id общей кампании в каждом мире.
    ///
    /// Фиксированный, а не Guid: «Common» — системная сущность, и её адрес
    /// должен быть предсказуем (папка `Common`, ссылки в импорте, поведение по
    /// умолчанию). Разные миры не конфликтуют, потому что кампания лежит внутри
    /// папки своего мира.
    /// </summary>
    public const string CommonCampaignIdValue = "common";

    /// <summary>Id общей кампании у мира.</summary>
    public static string CommonCampaignId(string worldId) => CommonCampaignIdValue;
}

/// <summary>
/// Правила подписей мира и кампании в списках.
///
/// В домене, а не в форме: подпись — часть контракта содержимого (её читают и
/// окно кампаний, и селектор <c>[МИР][КАМПАНИЯ]</c>, и диалог импорта), и
/// собирать её в каждом месте по-своему значило бы получить три разных ответа
/// на вопрос «как называется мир».
/// </summary>
public static class WorldDisplayRules
{
    /// <summary>
    /// Что показывать вместо имени, если оно пустое. Не пустая строка: пустая
    /// подпись читается как «элемент не загрузился», а не как «имени нет».
    /// </summary>
    public const string UnnamedLabel = "(без имени)";

    /// <summary>Отображаемое имя мира: полное имя важнее короткого.</summary>
    public static string DisplayName(WorldDefinition? world)
    {
        if (world is null)
            return UnnamedLabel;

        return DisplayName(world.Name, world.FullName);
    }

    /// <summary>
    /// Отображаемое имя по паре «короткое + полное».
    ///
    /// Отдельная перегрузка не для удобства: у КАМПАНИИ в сторе есть только
    /// эта пара (определение кампании хранится как CampaignDefinition, а не как
    /// CampaignWorldDefinition), и собирать правило заново в вызывающем коде
    /// значило бы получить второе, постепенно расходящееся определение «как
    /// называется кампания».
    /// </summary>
    public static string DisplayName(string? name, string? fullName)
    {
        var full = fullName?.Trim();
        if (!string.IsNullOrEmpty(full))
            return full;

        var shortName = name?.Trim();
        return string.IsNullOrEmpty(shortName) ? UnnamedLabel : shortName;
    }

    /// <summary>Отображаемое имя кампании.</summary>
    public static string DisplayName(CampaignWorldDefinition? campaign)
    {
        if (campaign is null)
            return UnnamedLabel;

        return DisplayName(campaign.Name, campaign.FullName);
    }

    /// <summary>
    /// Строка «кто и когда изменил» для списка.
    ///
    /// Возвращает <c>null</c>, когда подписывать нечем: пустую строку форма
    /// рисовала бы как пустую строку списка, то есть как «данных нет», хотя на
    /// самом деле это «автор не указан» (файлы, созданные до появления подписи).
    /// </summary>
    public static string? Describe(ResourceMetadata? metadata, bool modified = true)
    {
        if (metadata is null)
            return null;

        var author = (modified ? metadata.ModifiedBy : metadata.CreatedBy)?.Trim();
        var moment = modified ? metadata.EffectiveModifiedOn : metadata.CreatedOn;

        var label = modified ? "Modified by:" : "Created by:";
        var stamp = moment?.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        if (!string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(stamp))
            return $"{label} {author} · {stamp}";

        if (!string.IsNullOrEmpty(author))
            return $"{label} {author}";

        return string.IsNullOrEmpty(stamp) ? null : $"{label} — · {stamp}";
    }
}

/// <summary>
/// Правила селектора <c>[МИР][КАМПАНИЯ][&lt;меню&gt;]</c>.
///
/// В домене, потому что селектор живёт в ДВУХ окнах (главная форма и Симулятор)
/// и обязан показывать одно и то же. Разошедшиеся правила дали бы два разных
/// ответа на вопрос «какой мир открыт» — а это определяет, что вообще
/// редактируется.
/// </summary>
public static class ResourceSelectorRules
{
    /// <summary>
    /// Пункт «создать новый», который идёт ПОСЛЕДНИМ в каждом списке.
    ///
    /// Отдельной кнопкой его делать не нужно: создание — это тоже выбор, только
    /// нового элемента, и в списке оно оказывается ровно там, где его ищут.
    /// </summary>
    public const string CreateNewLabel = "＋ Создать…";

    /// <summary>
    /// Какая кампания должна быть открыта в мире.
    ///
    /// Приоритет: запомненная у мира кампания → общая (Common) → первая по
    /// порядку. Запомненная у МИРА, а не в настройках интерфейса: «какая
    /// кампания открыта» — свойство мира, и при передаче его другому автору
    /// контекст должен сохраниться.
    ///
    /// <paramref name="available"/> — id кампаний мира. Пустой список означает
    /// «кампаний нет», и тогда возвращается null: выдумывать id нельзя.
    /// </summary>
    public static string? ResolveCampaignId(string? lastCampaignId, IEnumerable<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var ids = available.ToList();
        if (ids.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(lastCampaignId))
        {
            var remembered = ids.FirstOrDefault(id =>
                id.Equals(lastCampaignId, StringComparison.OrdinalIgnoreCase));
            if (remembered is not null)
                return remembered;
        }

        var common = ids.FirstOrDefault(id =>
            id.Equals(WorldDefinitionRules.CommonCampaignIdValue, StringComparison.OrdinalIgnoreCase));

        return common ?? ids[0];
    }
}


/// <summary>
/// Правило допустимого имени мира/кампании/квеста на диске.
///
/// Отдельно от <see cref="ResourceMetadata.IsValidAuthor"/>: автор — это подпись
/// в списке, а имя — ещё и имя ПАПКИ. Здесь запрещены пробелы по краям и символы
/// пути, иначе папку нельзя будет создать.
/// </summary>
public static class ResourceNaming
{
    public static bool IsValidName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        foreach (var character in value)
        {
            if (Array.IndexOf(invalid, character) >= 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Имя папки по отображаемому имени: недопустимые символы заменяются дефисом.
    ///
    /// Заменяем, а не отвергаем: имя ресурса задаёт человек, и отказ «нельзя,
    /// символ не тот» без объяснения, какой именно, был бы бесполезен.
    /// </summary>
    public static string ToFolderName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return "resource";

        System.Text.StringBuilder? builder = null;
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (Array.IndexOf(invalid, character) < 0)
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new System.Text.StringBuilder(trimmed.Length).Append(trimmed, 0, index);
            builder.Append('-');
        }

        var result = (builder?.ToString() ?? trimmed).Trim().TrimEnd('.');
        return result.Length == 0 ? "resource" : result;
    }
}

/// <summary>
/// Проверка «ресурс лежит не у своего родителя».
///
/// Перенести папку кампании в чужой мир можно файловым менеджером, и это НЕ
/// запрет: объект мог быть перемещён осознанно (например, автор собирает мир из
/// чужих заготовок). Поэтому расхождение не блокирует работу, а показывается
/// предупреждением — но показывается ОБЯЗАТЕЛЬНО: без него квесты кампании
/// выглядят пропавшими, а причина не видна вовсе.
///
/// Правило в домене, потому что о расхождении сообщают ДВА места (список
/// кампаний и селектор <c>[МИР][КАМПАНИЯ]</c>), и разошедшиеся формулировки дали
/// бы два разных ответа на вопрос «эта кампания из этого мира?».
/// </summary>
public static class ResourceParentRules
{
    /// <summary>
    /// Объявленный родитель отличается от фактического.
    ///
    /// Пустое значение расхождением НЕ считается: кампании, созданные до появления
    /// поля, обязаны открываться без предупреждения — иначе весь существующий
    /// контент разом объявился бы «чужим».
    /// </summary>
    public static bool IsForeignParent(string? declaredParentId, string? actualParentId)
    {
        if (string.IsNullOrWhiteSpace(declaredParentId) || string.IsNullOrWhiteSpace(actualParentId))
            return false;

        return !declaredParentId.Trim().Equals(actualParentId.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Текст предупреждения или <c>null</c>, если всё в порядке.
    ///
    /// Называет ОБА идентификатора: «кампания из другого мира» не даёт понять,
    /// куда именно её положить, и автор искал бы нужную папку наугад.
    /// </summary>
    public static string? Describe(string? declaredParentId, string? actualParentId)
    {
        if (!IsForeignParent(declaredParentId, actualParentId))
            return null;

        return "Этот ресурс принадлежит миру «" + declaredParentId!.Trim() +
               "», а лежит в папке мира «" + actualParentId!.Trim() +
               "». Работать это не мешает — но если перенос был случайным, " +
               "переместите папку в правильный мир.";
    }
}
