using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed record CampaignSyncReport(
    IReadOnlyList<string> UserVisibleChanges,
    IReadOnlyList<string> LogOnlyChanges)
{
    public bool HasChanges => UserVisibleChanges.Count > 0 || LogOnlyChanges.Count > 0;
}

/// <summary>
/// Installs and updates the bundled campaign store into the persistent user store.
///
/// Source: published <c>data/campaigns</c>.
/// Destination: %LOCALAPPDATA%\\Assist Quest Editor\\quests.
/// Existing user enable/disable choices are preserved. Removed quests are kept
/// as Disabled entries instead of being silently deleted.
/// </summary>
public static class CampaignInstaller
{
    private const int SupportedSchemaVersion = 1;
    private const string CampaignDirectory = "campaigns";

    public static CampaignSyncReport Synchronize()
    {
        var userRoot = AppPaths.UserQuestRoot;
        var sourceRoot = Path.Combine(AppPaths.ResourceRoot, CampaignDirectory);
        Directory.CreateDirectory(userRoot);

        var visible = new List<string>();
        var logOnly = new List<string>();

        if (!Directory.Exists(sourceRoot))
        {
            AppLogger.Warn("CampaignInstaller: bundled campaign store не найден.", sourceRoot);
            return new CampaignSyncReport(visible, logOnly);
        }

        var sourceRecords = EnumerateCampaigns(sourceRoot);
        var sourceIds = sourceRecords
            .Select(item => item.Definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourceRecords)
        {
            try
            {
                SynchronizeCampaign(source, userRoot, visible, logOnly);
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "CampaignInstaller: ошибка синхронизации кампании.",
                    ex,
                    source.CampaignFilePath);
                logOnly.Add($"Ошибка обновления кампании «{source.Definition.Name}»: {ex.Message}");
            }
        }

        DisableRemovedCampaigns(userRoot, sourceIds, visible, logOnly);

        AppLogger.Info(
            "CampaignInstaller: синхронизация завершена.",
            $"sourceCampaigns={sourceRecords.Count}; userRoot={userRoot}; visibleChanges={visible.Count}");

        return new CampaignSyncReport(visible, logOnly);
    }

    private static void SynchronizeCampaign(
        SourceCampaign source,
        string userRoot,
        ICollection<string> visible,
        ICollection<string> logOnly)
    {
        var destinationFolder =
            FindLocalCampaignFolder(userRoot, source.Definition.Id) ??
            Path.Combine(userRoot, Path.GetFileName(source.FolderPath));
        var destinationFile = Path.Combine(destinationFolder, CampaignStore.CampaignFileName);

        if (!File.Exists(destinationFile))
        {
            CopyDirectory(source.FolderPath, destinationFolder);
            var message = $"Установлена кампания «{source.Definition.Name}» v{source.Definition.Version}.";
            logOnly.Add(message);
            AppLogger.Info("CampaignInstaller: первая установка кампании.", message);
            return;
        }

        CampaignDefinition? localDefinition = null;
        try
        {
            var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(
                File.ReadAllText(destinationFile));

            if (document?.Definition is not null &&
                document.SchemaVersion == SupportedSchemaVersion &&
                document.Format.Equals("aqcampaign", StringComparison.OrdinalIgnoreCase))
            {
                localDefinition = document.Definition;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "CampaignInstaller: локальный Campaign повреждён; будет восстановлен.",
                destinationFile + "; " + ex.Message);
        }

        if (localDefinition is null)
        {
            CopyDirectory(source.FolderPath, destinationFolder);
            var message = $"Восстановлена кампания «{source.Definition.Name}» из установочного хранилища.";
            visible.Add(message);
            AppLogger.Warn("CampaignInstaller: восстановлена повреждённая Campaign.", message);
            return;
        }

        var localByQuest = localDefinition.Quests.ToDictionary(
            item => item.QuestId,
            item => item,
            StringComparer.OrdinalIgnoreCase);

        var merged = new List<CampaignQuestEntry>();
        var changed = false;

        foreach (var sourceQuest in source.Definition.Quests)
        {
            localByQuest.TryGetValue(sourceQuest.QuestId, out var localQuest);
            var targetQuestPath = SafeCombine(destinationFolder, sourceQuest.RelativePath);

            var sourceQuestPath = SafeCombine(source.FolderPath, sourceQuest.RelativePath);
            var sourceVersion = ReadQuestVersion(sourceQuestPath, sourceQuest.Version);
            var localVersion = localQuest?.Version ?? ReadQuestVersion(targetQuestPath, 0);

            var shouldReplace = !File.Exists(targetQuestPath) || sourceVersion > localVersion;

            if (shouldReplace && File.Exists(sourceQuestPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetQuestPath)!);
                File.Copy(sourceQuestPath, targetQuestPath, overwrite: true);

                var message = localQuest is null
                    ? $"Добавлен квест «{sourceQuest.QuestId}» v{sourceVersion} в кампанию «{source.Definition.Name}»."
                    : $"Заменён квест «{sourceQuest.QuestId}»: v{localVersion} → v{sourceVersion}.";

                visible.Add(message);
                AppLogger.Info("CampaignInstaller: Quest обновлён.", message);
                changed = true;

                localVersion = sourceVersion;
            }

            var status = localQuest?.Status ?? sourceQuest.Status;
            merged.Add(sourceQuest with
            {
                Version = Math.Max(1, localVersion),
                Status = status
            });
        }

        foreach (var localQuest in localDefinition.Quests)
        {
            if (source.Definition.Quests.Any(item =>
                    item.QuestId.Equals(localQuest.QuestId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (localQuest.Status != CampaignQuestStatus.Disabled)
            {
                var message =
                    $"Отключён удалённый квест «{localQuest.QuestId}» из кампании «{source.Definition.Name}».";
                visible.Add(message);
                AppLogger.Warn("CampaignInstaller: удалённый Quest отключён.", message);
                changed = true;
            }

            merged.Add(localQuest with { Status = CampaignQuestStatus.Disabled });
        }

        if (source.Definition.Version > localDefinition.Version)
        {
            CopyNonQuestFiles(source.FolderPath, destinationFolder);
            changed = true;
            logOnly.Add(
                $"Обновлена кампания «{source.Definition.Name}»: v{localDefinition.Version} → v{source.Definition.Version}.");
        }

        if (!SequenceEqual(localDefinition.Quests, merged) ||
            source.Definition.Version > localDefinition.Version)
        {
            var next = localDefinition with
            {
                Version = Math.Max(localDefinition.Version, source.Definition.Version),
                Quests = merged,
                Files = source.Definition.Files
            };

            WriteCampaign(destinationFile, next);
            changed = true;
        }

        if (!changed)
        {
            AppLogger.Info(
                "CampaignInstaller: Campaign актуальна.",
                $"campaignId={source.Definition.Id}; version={localDefinition.Version}");
        }
        else
        {
            AppLogger.Info(
                "CampaignInstaller: Campaign синхронизирована.",
                $"campaignId={source.Definition.Id}; version={Math.Max(localDefinition.Version, source.Definition.Version)}");
        }
    }

    private static void DisableRemovedCampaigns(
        string userRoot,
        HashSet<string> sourceIds,
        ICollection<string> visible,
        ICollection<string> logOnly)
    {
        foreach (var file in Directory.EnumerateFiles(
                     userRoot,
                     CampaignStore.CampaignFileName,
                     SearchOption.AllDirectories))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(File.ReadAllText(file));
                var definition = document?.Definition;
                if (definition is null ||
                    sourceIds.Contains(definition.Id) ||
                    !document!.Format.Equals("aqcampaign", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!definition.Active)
                    continue;

                var disabled = definition with
                {
                    Active = false,
                    Quests = definition.Quests
                        .Select(item => item with { Status = CampaignQuestStatus.Disabled })
                        .ToArray()
                };

                WriteCampaign(file, disabled);
                var message = $"Отключена удалённая кампания «{definition.Name}» и все её квесты.";
                visible.Add(message);
                AppLogger.Warn("CampaignInstaller: удалённая Campaign отключена.", message);
            }
            catch (Exception ex)
            {
                logOnly.Add($"Не удалось проверить удалённую кампанию: {file}: {ex.Message}");
            }
        }
    }

    private static IReadOnlyList<SourceCampaign> EnumerateCampaigns(string sourceRoot)
    {
        var result = new List<SourceCampaign>();

        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot,
                     CampaignStore.CampaignFileName,
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(
                    File.ReadAllText(file));

                if (document?.Definition is null ||
                    document.SchemaVersion != SupportedSchemaVersion ||
                    !document.Format.Equals("aqcampaign", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new SourceCampaign(
                    document.Definition,
                    Path.GetDirectoryName(file)!,
                    file));
            }
            catch (Exception ex)
            {
                AppLogger.Error("CampaignInstaller: пропущен некорректный bundled Campaign.", ex, file);
            }
        }

        return result;
    }

    private static string? FindLocalCampaignFolder(string userRoot, string campaignId)
    {
        foreach (var file in Directory.EnumerateFiles(
                     userRoot,
                     CampaignStore.CampaignFileName,
                     SearchOption.AllDirectories))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(
                    File.ReadAllText(file));

                if (document?.Definition?.Id.Equals(
                        campaignId,
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    return Path.GetDirectoryName(file);
                }
            }
            catch
            {
                // Некорректный файл будет обработан отдельной веткой синхронизации.
            }
        }

        return null;
    }

    private static int ReadQuestVersion(string path, int fallback)
    {
        if (!File.Exists(path))
            return Math.Max(0, fallback);

        try
        {
            var document = ResourceJsonFormat.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path));
            return Math.Max(0, document?.Definition?.Version ?? fallback);
        }
        catch
        {
            return Math.Max(0, fallback);
        }
    }

    private static void WriteCampaign(string path, CampaignDefinition definition)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            ResourceJsonFormat.Serialize(
                new CampaignDefinitionDocument(
                    SupportedSchemaVersion,
                    "aqcampaign",
                    definition)));
    }

    private static bool SequenceEqual(
        IReadOnlyList<CampaignQuestEntry> left,
        IReadOnlyList<CampaignQuestEntry> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First == pair.Second);

    private static void CopyNonQuestFiles(string sourceFolder, string destinationFolder)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(
                     sourceFolder,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceFolder, sourceFile);
            if (relative.Equals(CampaignStore.CampaignFileName, StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".aqquest", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(destinationFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, overwrite: true);
        }
    }

    private static void CopyDirectory(string sourceFolder, string destinationFolder)
    {
        Directory.CreateDirectory(destinationFolder);

        foreach (var sourceFile in Directory.EnumerateFiles(
                     sourceFolder,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceFolder, sourceFile);
            var target = Path.Combine(destinationFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourceFile, target, overwrite: true);
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));

        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь ресурса выходит за пределы Campaign: " + relative);

        return full;
    }

    private sealed record SourceCampaign(
        CampaignDefinition Definition,
        string FolderPath,
        string CampaignFilePath);
}
