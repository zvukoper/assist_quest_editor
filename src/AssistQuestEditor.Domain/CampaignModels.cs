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
/// </summary>
public sealed record CampaignDefinition(
    string Id,
    string Name,
    int Version,
    bool Active,
    IReadOnlyList<CampaignQuestEntry> Quests,
    IReadOnlyList<string> Files,
    GeoCoordinate? Geo = null,
    DateTimeOffset? StartDate = null);

public sealed record CampaignDefinitionDocument(
    int SchemaVersion,
    string Format,
    CampaignDefinition Definition);
