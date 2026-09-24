namespace AssistQuestEditor.Domain;

public enum CampaignQuestStatus
{
    Enabled,
    Disabled
}

/// <summary>
/// Quest внутри кампании.
///
/// <see cref="Order"/> — порядковый номер для сортировки в UI и на карте
/// Simulator. Ноль означает «порядок не задан»: такие квесты идут после
/// нумерованных и сортируются по имени файла. Именно поэтому это int, а не
/// int?: в JSON отсутствие поля читается как 0 без дополнительного состояния.
/// </summary>
public sealed record CampaignQuestEntry(
    string QuestId,
    string RelativePath,
    int Version,
    CampaignQuestStatus Status,
    int Order = 0);

/// <summary>
/// Стартовые условия игрового мира.
///
/// Это то, что кампания задаёт о мире «на входе»: погода, дождь, видимость.
/// Отдельная запись, а не поля рядом с <see cref="GeoCoordinate"/>: их всегда
/// читают и пишут вместе, и так «мир на старте» виден одним блоком.
///
/// Null-поля означают «не задано» и заменяются значениями симулятора: кампания
/// не обязана описывать погоду, и пустой объект не должен её обнулять.
/// </summary>
public sealed record WorldStartConditions(
    string? Weather = null,
    double? RainPercent = null,
    double? VisibilityMeters = null);

/// <summary>
/// Campaign в песочнице: набор квестов плюс свойства игрового мира.
///
/// <see cref="Geo"/> — реальная географическая координата мира. Она НЕ связана
/// с игровыми координатами ETS2 (X/Y/Z) и нужна только астрономии: расчёту
/// восхода, заката и длины светового дня. Поэтому это свойство кампании, а не
/// характеристика точки мира.
///
/// <see cref="StartDate"/> — дата старта игрового мира. Null означает
/// <see cref="GameCalendar.DefaultStartDate"/> (01.01.2026): значение по
/// умолчанию задаётся кодом, а не проставляется в файл при первом чтении,
/// иначе чтение молча переписывало бы чужой документ.
///
/// <see cref="StartConditions"/> — погода, дождь и видимость на старте мира.
/// </summary>
public sealed record CampaignDefinition(
    string Id,
    string Name,
    int Version,
    bool Active,
    IReadOnlyList<CampaignQuestEntry> Quests,
    IReadOnlyList<string> Files,
    GeoCoordinate? Geo = null,
    DateTimeOffset? StartDate = null,
    WorldStartConditions? StartConditions = null,
    // Id мира-родителя. Пусто у старых файлов: принадлежность проверяется
    // толерантно (см. CampaignWorldDefinition.BelongsTo), иначе уже
    // существующие кампании перестали бы открываться.
    string? WorldId = null,
    // Название для человека и описание: показываются в списке, в окне
    // редактирования и в диалоге импорта.
    string? FullName = null,
    string? Description = null,
    // Файл изображения внутри папки кампании.
    string? ImageFile = null,
    // Авторство и даты: список показывает его курсивом, импорт по ним решает,
    // перезаписывать ли существующее.
    ResourceMetadata? Metadata = null);

public sealed record CampaignDefinitionDocument(
    int SchemaVersion,
    string Format,
    CampaignDefinition Definition);
