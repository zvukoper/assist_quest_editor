namespace AssistQuestEditor.Domain;

public enum CampaignQuestStatus
{
    Enabled,
    Disabled
}

public sealed record CampaignQuestEntry(
    string QuestId,
    string RelativePath,
    int Version,
    CampaignQuestStatus Status);

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
