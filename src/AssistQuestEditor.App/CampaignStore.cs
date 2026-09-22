using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed record InstalledQuestView(
    string CampaignId,
    string CampaignName,
    string CampaignPath,
    bool CampaignActive,
    string QuestId,
    string Title,
    int Version,
    CampaignQuestStatus Status,
    string RelativePath,
    string FullPath,
    QuestActivation? Activation);

public sealed record InstalledCampaignView(
    string Id,
    string Name,
    int Version,
    bool Active,
    string FolderPath,
    IReadOnlyList<InstalledQuestView> Quests);

/// <summary>
/// Canonical installed campaign catalog.
///
/// Campaign files are the source of truth for campaign activation and per-quest
/// availability. The Runtime receives only the effective enabled quests from here.
/// </summary>
public sealed class CampaignStore
{
    public const string CampaignFileName = "campaign.aqcampaign";

    private readonly Dictionary<string, CampaignRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);

    public CampaignStore()
    {
        Reload();
    }

    public IReadOnlyList<CampaignRecord> Records =>
        _records.Values
            .OrderBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<InstalledCampaignView> BuildSimulatorCatalog()
    {
        var result = new List<InstalledCampaignView>();

        foreach (var record in Records)
        {
            var quests = new List<InstalledQuestView>();
            foreach (var entry in record.Definition.Quests)
            {
                var path = SafeCombine(record.FolderPath, entry.RelativePath);
                if (!File.Exists(path))
                    continue;

                try
                {
                    var definition = LoadQuest(path);
                    quests.Add(new InstalledQuestView(
                        record.Definition.Id,
                        record.Definition.Name,
                        record.FolderPath,
                        record.Definition.Active,
                        definition.Id,
                        definition.Title,
                        definition.Version,
                        entry.Status,
                        entry.RelativePath,
                        path,
                        definition.Activation));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("CampaignStore: не удалось загрузить Quest для каталога.", ex, path);
                }
            }

            result.Add(new InstalledCampaignView(
                record.Definition.Id,
                record.Definition.Name,
                record.Definition.Version,
                record.Definition.Active,
                record.FolderPath,
                quests));
        }

        return result;
    }

    public IReadOnlyList<QuestDefinition> LoadEnabledQuestDefinitions()
    {
        var quests = new List<QuestDefinition>();

        foreach (var record in Records.Where(item => item.Definition.Active))
        {
            foreach (var entry in record.Definition.Quests.Where(item => item.Status == CampaignQuestStatus.Enabled))
            {
                var path = SafeCombine(record.FolderPath, entry.RelativePath);
                if (!File.Exists(path))
                    continue;

                try
                {
                    quests.Add(LoadQuest(path));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("CampaignStore: ошибка загрузки включённого Quest.", ex, path);
                }
            }
        }

        return quests
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public string? FindQuestPath(string questId)
    {
        if (string.IsNullOrWhiteSpace(questId))
            return null;

        foreach (var record in Records)
        {
            var entry = record.Definition.Quests.FirstOrDefault(item =>
                item.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;

            var path = SafeCombine(record.FolderPath, entry.RelativePath);
            return File.Exists(path) ? path : null;
        }

        return null;
    }

    public string? FindScenePath(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            return null;

        foreach (var record in Records)
        {
            var candidates = new[]
            {
                Path.Combine(record.FolderPath, "scenes", sceneId + ".aqscene"),
                Path.Combine(record.FolderPath, sceneId + ".aqscene")
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path is not null)
                return path;
        }

        return null;
    }

    public void SetCampaignActive(string campaignId, bool active)
    {
        var record = GetRecord(campaignId);
        if (record.Definition.Active == active)
            return;

        record.Replace(record.Definition with { Active = active });
        Save(record);
        AppLogger.Info(
            "CampaignStore: изменена активность кампании.",
            $"campaignId={campaignId}; active={active}");
    }

    public void SetQuestEnabled(string campaignId, string questId, bool enabled)
    {
        var record = GetRecord(campaignId);
        var entry = record.Definition.Quests.FirstOrDefault(item =>
            item.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidOperationException(
                $"Quest «{questId}» отсутствует в кампании «{campaignId}».");

        var nextStatus = enabled ? CampaignQuestStatus.Enabled : CampaignQuestStatus.Disabled;
        if (entry.Status == nextStatus)
            return;

        var quests = record.Definition.Quests
            .Select(item => item.QuestId.Equals(entry.QuestId, StringComparison.OrdinalIgnoreCase)
                ? item with { Status = nextStatus }
                : item)
            .ToArray();

        record.Replace(record.Definition with { Quests = quests });
        Save(record);

        AppLogger.Info(
            "CampaignStore: изменён статус Quest.",
            $"campaignId={campaignId}; questId={questId}; enabled={enabled}");
    }

    public void Reload()
    {
        _records.Clear();
        Directory.CreateDirectory(AppPaths.UserQuestRoot);

        foreach (var campaignFile in Directory.EnumerateFiles(
                     AppPaths.UserQuestRoot,
                     CampaignFileName,
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(
                    File.ReadAllText(campaignFile));

                if (document?.Definition is null ||
                    document.SchemaVersion != 1 ||
                    !document.Format.Equals("aqcampaign", StringComparison.OrdinalIgnoreCase))
                    continue;

                var folder = Path.GetDirectoryName(campaignFile);
                if (string.IsNullOrWhiteSpace(folder))
                    continue;

                var record = new CampaignRecord(document.Definition, folder, campaignFile);
                if (!_records.ContainsKey(record.Definition.Id))
                    _records.Add(record.Definition.Id, record);
            }
            catch (Exception ex)
            {
                AppLogger.Error("CampaignStore: ошибка чтения Campaign.", ex, campaignFile);
            }
        }

        AppLogger.Info("CampaignStore: каталог загружен.", $"campaigns={_records.Count}; root={AppPaths.UserQuestRoot}");
    }

    private CampaignRecord GetRecord(string campaignId)
    {
        if (!_records.TryGetValue(campaignId, out var record))
            throw new InvalidOperationException("Кампания не найдена: " + campaignId);

        return record;
    }

    private static QuestDefinition LoadQuest(string path)
    {
        var document = ResourceJsonFormat.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path));
        if (document?.Definition?.Graph is null ||
            document.SchemaVersion != 1 ||
            !document.Format.Equals("aqquest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Неподдерживаемый формат Quest: " + path);

        return document.Definition;
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, normalized));
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Campaign resource path выходит за пределы кампании: " + relativePath);

        return full;
    }

    public sealed class CampaignRecord
    {
        public CampaignDefinition Definition { get; private set; }
        public string FolderPath { get; }
        public string CampaignFilePath { get; }

        internal CampaignRecord(
            CampaignDefinition definition,
            string folderPath,
            string campaignFilePath)
        {
            Definition = definition;
            FolderPath = folderPath;
            CampaignFilePath = campaignFilePath;
        }

        internal void Replace(CampaignDefinition definition) =>
            Definition = definition;
    }

    private static void Save(CampaignRecord record)
    {
        Directory.CreateDirectory(record.FolderPath);
        var document = new CampaignDefinitionDocument(1, "aqcampaign", record.Definition);
        File.WriteAllText(
            record.CampaignFilePath,
            ResourceJsonFormat.Serialize(document));
    }
}
