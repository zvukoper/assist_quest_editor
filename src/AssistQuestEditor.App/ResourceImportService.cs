using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>Что получилось при импорте ресурса в родителя.</summary>
public sealed record ResourceImportResult(
    string Kind,
    string Id,
    string DisplayName,
    string TargetPath,
    bool Overwritten);

/// <summary>
/// Импорт кампании и квеста в родителя.
///
/// Мир распаковывается сам в себя (<see cref="WorldStore.ImportWorldFromArchive"/>),
/// а кампания и квест без родителя не имеют адреса: файл кампании лежит внутри
/// папки мира, файл квеста — внутри папки кампании. Правило раскладки берётся из
/// <see cref="WorldPaths"/>: это контракт обмена, и разойтись с ним импорт не
/// имеет права — иначе ресурс лежал бы на месте, но каталог его не видел.
///
/// Всё распаковывается во ВРЕМЕННУЮ папку и только затем переносится на место:
/// прерванный импорт не должен оставлять частично распакованный ресурс, который
/// выглядит рабочим, но не читается.
/// </summary>
public static class ResourceImportService
{
    public const string QuestExtension = ".aqquest";

    public static ResourceImportResult ImportCampaign(
        WorldRecord world,
        ArchiveInspection inspection,
        bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inspection);
        EnsureKind(inspection, WorldArchiveKinds.Campaign);

        var campaignsRoot = WorldPaths.CampaignsRoot(world.FolderPath);
        Directory.CreateDirectory(campaignsRoot);

        var desired = WorldArchiveImportRules.TargetFolderName(inspection.Manifest);
        var targetName = overwrite
            ? desired
            : WorldArchiveImportRules.UniqueFolderName(
                desired,
                name => Directory.Exists(Path.Combine(campaignsRoot, name)));

        var targetFolder = Path.Combine(campaignsRoot, targetName);
        if (overwrite && Directory.Exists(targetFolder))
        {
            var existing = ReadCampaign(targetFolder);
            EnsureOverwriteDiffers(
                inspection.Manifest.Version,
                inspection.Manifest.Metadata?.ModifiedOn,
                existing?.Version,
                existing?.Metadata?.ModifiedOn,
                "Кампания");
        }

        var staging = UnpackToStaging(inspection.ArchivePath, WorldPaths.CampaignFileName);

        try
        {
            var definition = ReadCampaign(staging)
                ?? throw new InvalidDataException("В архиве нет корректного campaign.aqcampaign.");

            if (!overwrite)
            {
                definition = definition with
                {
                    Id = ToResourceId(targetName),
                    Name = targetName,
                    FullName = null,
                    WorldId = world.Definition.Id
                };
                RewriteCampaign(staging, definition);
                RewriteQuestParents(staging, world.Definition.Id, definition.Id);
            }
            else
            {
                definition = definition with { WorldId = world.Definition.Id };
                RewriteCampaign(staging, definition);
            }

            RemoveDependencyPayload(staging);

            if (Directory.Exists(targetFolder))
                Directory.Delete(targetFolder, recursive: true);

            Directory.Move(staging, targetFolder);

            var installed = ReadCampaign(targetFolder)
                ?? throw new InvalidDataException("Кампания распакована, но не читается.");

            var displayName = string.IsNullOrWhiteSpace(installed.FullName)
                ? installed.Name
                : installed.FullName!;

            return new ResourceImportResult(
                WorldArchiveKinds.Campaign,
                installed.Id,
                displayName,
                targetFolder,
                overwrite);
        }
        catch
        {
            Cleanup(staging);
            throw;
        }
    }

    public static ResourceImportResult ImportQuest(
        CampaignStore store,
        CampaignStore.CampaignRecord campaign,
        ArchiveInspection inspection,
        bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(inspection);
        EnsureKind(inspection, WorldArchiveKinds.Quest);

        var questsFolder = WorldPaths.QuestsFolderPath(campaign.FolderPath);
        Directory.CreateDirectory(questsFolder);

        var desired = WorldArchiveImportRules.TargetFolderName(inspection.Manifest);
        var targetName = overwrite
            ? desired
            : WorldArchiveImportRules.UniqueFolderName(
                desired,
                name => File.Exists(Path.Combine(questsFolder, name + QuestExtension)));

        var targetFile = Path.Combine(questsFolder, targetName + QuestExtension);
        var existingEntry = campaign.Definition.Quests.FirstOrDefault(item =>
            item.QuestId.Equals(desired, StringComparison.OrdinalIgnoreCase));

        if (overwrite && existingEntry is not null)
        {
            var existingPath = Path.Combine(campaign.FolderPath, existingEntry.RelativePath);
            if (File.Exists(existingPath))
            {
                var existingDefinition = ReadQuest(existingPath);
                EnsureOverwriteDiffers(
                    inspection.Manifest.Version,
                    inspection.Manifest.Metadata?.ModifiedOn,
                    existingDefinition?.Version ?? existingEntry.Version,
                    existingDefinition?.Metadata?.ModifiedOn,
                    "Квест");
            }
        }

        var staging = UnpackToStaging(inspection.ArchivePath, "*" + QuestExtension);
        try
        {
            var staged = Directory
                .EnumerateFiles(staging, "*" + QuestExtension, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException("В архиве нет файла квеста (.aqquest).");

            var quest = ReadQuest(staged)
                ?? throw new InvalidDataException("Файл квеста повреждён.");

            if (!overwrite)
            {
                var newId = ToResourceId(targetName);
                quest = quest with
                {
                    Id = newId,
                    Title = targetName,
                    WorldId = campaign.Definition.WorldId,
                    CampaignId = campaign.Definition.Id,
                    Graph = quest.Graph with { Id = newId, Name = targetName }
                };
            }
            else
            {
                quest = quest with
                {
                    WorldId = campaign.Definition.WorldId,
                    CampaignId = campaign.Definition.Id
                };
            }

            var tempQuest = Path.Combine(staging, "imported" + QuestExtension);
            File.WriteAllText(
                tempQuest,
                ResourceJsonFormat.Serialize(new QuestDefinitionDocument(
                    1, "aqquest", quest)));

            File.Copy(tempQuest, targetFile, overwrite: true);

            var relativePath = Path.GetRelativePath(campaign.FolderPath, targetFile)
                .Replace(Path.DirectorySeparatorChar, '/');

            store.RegisterQuest(
                campaign,
                quest,
                relativePath,
                CampaignQuestStatus.Enabled);

            RemoveDependencyPayload(staging);

            return new ResourceImportResult(
                WorldArchiveKinds.Quest,
                quest.Id,
                string.IsNullOrWhiteSpace(quest.Title) ? quest.Id : quest.Title,
                targetFile,
                overwrite);
        }
        finally
        {
            Cleanup(staging);
        }
    }

    private static void EnsureOverwriteDiffers(
        int incomingVersion,
        DateTimeOffset? incomingModified,
        int? existingVersion,
        DateTimeOffset? existingModified,
        string kind)
    {
        // Перезапись разрешена только при отличии версии ИЛИ даты изменения.
        // При совпадении обоих признаков архив не содержит более нового ресурса.
        var sameVersion = existingVersion.HasValue && existingVersion.Value == incomingVersion;
        var sameDate = incomingModified.HasValue &&
                       existingModified.HasValue &&
                       incomingModified.Value == existingModified.Value;

        if (sameVersion && sameDate)
        {
            throw new InvalidOperationException(
                $"{kind} с такой же версией ({incomingVersion}) и датой изменения уже существует. " +
                "Перезапись не выполнена: импортируйте ресурс как новый.");
        }
    }

    private static string ToResourceId(string value) =>
        ResourceNaming.ToFolderName(value).Replace(' ', '_').ToLowerInvariant();

    private static void RewriteCampaign(string folder, CampaignDefinition definition)
    {
        File.WriteAllText(
            WorldPaths.CampaignFilePath(folder),
            ResourceJsonFormat.Serialize(new CampaignDefinitionDocument(
                1, "aqcampaign", definition)));
    }

    private static void RewriteQuestParents(string folder, string worldId, string campaignId)
    {
        var quests = Path.Combine(folder, WorldPaths.QuestsFolder);
        if (!Directory.Exists(quests))
            return;

        foreach (var path in Directory.EnumerateFiles(
                     quests, "*" + QuestExtension, SearchOption.AllDirectories))
        {
            var quest = ReadQuest(path);
            if (quest is null)
                continue;

            var next = quest with
            {
                WorldId = worldId,
                CampaignId = campaignId
            };

            File.WriteAllText(
                path,
                ResourceJsonFormat.Serialize(new QuestDefinitionDocument(
                    1, "aqquest", next)));
        }
    }

    private static QuestDefinition? ReadQuest(string path)
    {
        try
        {
            var document = ResourceJsonFormat.Deserialize<QuestDefinitionDocument>(
                File.ReadAllText(path));
            return document?.Definition;
        }
        catch
        {
            return null;
        }
    }

    private static CampaignDefinition? ReadCampaign(string folder)
    {
        var path = WorldPaths.CampaignFilePath(folder);
        if (!File.Exists(path))
            return null;

        try
        {
            var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(
                File.ReadAllText(path));
            return document?.Definition;
        }
        catch
        {
            return null;
        }
    }

    private static string UnpackToStaging(string archivePath, string expectedPattern)
    {
        var staging = Path.Combine(
            Path.GetTempPath(),
            "aq-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            WorldArchiveService.Unpack(archivePath, staging);

            if (Directory.EnumerateFiles(
                    staging, expectedPattern, SearchOption.AllDirectories).Any())
                return staging;

            throw new InvalidDataException(
                "В архиве нет " + expectedPattern + ": это не тот ресурс.");
        }
        catch
        {
            Cleanup(staging);
            throw;
        }
    }

    private static void RemoveDependencyPayload(string staging)
    {
        var dependencies = Path.Combine(staging, "dependencies");
        if (Directory.Exists(dependencies))
            Directory.Delete(dependencies, recursive: true);

        var info = Path.Combine(staging, "DEPENDENCIES.txt");
        if (File.Exists(info))
            File.Delete(info);
    }

    private static void Cleanup(string staging)
    {
        if (!Directory.Exists(staging))
            return;

        try { Directory.Delete(staging, recursive: true); }
        catch { }
    }

    private static void EnsureKind(ArchiveInspection inspection, string expected)
    {
        if (!inspection.Manifest.Kind.Equals(
                expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Архив содержит «" + inspection.Manifest.Kind +
                "», а не «" + expected + "».");
        }
    }
}
