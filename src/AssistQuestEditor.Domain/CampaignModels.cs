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

public sealed record CampaignDefinition(
    string Id,
    string Name,
    int Version,
    bool Active,
    IReadOnlyList<CampaignQuestEntry> Quests,
    IReadOnlyList<string> Files);

public sealed record CampaignDefinitionDocument(
    int SchemaVersion,
    string Format,
    CampaignDefinition Definition);
