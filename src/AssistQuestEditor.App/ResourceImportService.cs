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
    /// <summary>Каноническое расширение файла квеста.</summary>
    public const string QuestExtension = ".aqquest";

    /// <summary>
    /// Устанавливает кампанию в папку мира.
    ///
    /// Имя папки — из манифеста (id ресурса), потому что кампанию адресуют по id:
    /// ссылки в квестах и сохранениях указывают на него, а не на отображаемое имя.
    /// </summary>
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
        var staging = UnpackToStaging(inspection.ArchivePath, WorldPaths.CampaignFileName);

        try
        {
            if (Directory.Exists(targetFolder))
                Directory.Delete(targetFolder, recursive: true);

            Directory.CreateDirectory(campaignsRoot);
            Directory.Move(staging, targetFolder);
        }
        catch
        {
            Cleanup(staging);
            throw;
        }

        var definition = ReadCampaign(targetFolder)
            ?? throw new InvalidDataException(
                "Кампания распакована, но не читается: проверьте " +
                WorldPaths.CampaignFilePath(targetFolder));

        var displayName = string.IsNullOrWhiteSpace(definition.FullName)
            ? definition.Name
            : definition.FullName!;

        AppLogger.Info("ResourceImportService: кампания импортирована.",
            $"world={world.Definition.Id}; folder={targetFolder}; id={definition.Id}; " +
            $"overwrite={overwrite}");

        return new ResourceImportResult(
            WorldArchiveKinds.Campaign, definition.Id, displayName, targetFolder, overwrite);
    }

    /// <summary>
    /// Устанавливает квест в папку кампании.
    ///
    /// Имя файла — id ресурса плюс каноническое расширение: квесты перечисляются
    /// в файле кампании по относительному пути, и произвольное имя файла сделало
    /// бы ресурс невидимым для каталога, хотя сам файл лежал бы на месте.
    /// </summary>
    public static ResourceImportResult ImportQuest(
        CampaignStore.CampaignRecord campaign,
        ArchiveInspection inspection,
        bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(inspection);
        EnsureKind(inspection, WorldArchiveKinds.Quest);

        var questsFolder = WorldPaths.QuestsFolderPath(campaign.FolderPath);
        Directory.CreateDirectory(questsFolder);

        var desired = WorldArchiveImportRules.TargetFolderName(inspection.Manifest);
        var fileName = desired + QuestExtension;

        if (!overwrite)
        {
            fileName = WorldArchiveImportRules.UniqueFolderName(
                desired,
                name => File.Exists(Path.Combine(questsFolder, name + QuestExtension))) + QuestExtension;
        }

        var targetFile = Path.Combine(questsFolder, fileName);
        var staging = UnpackToStaging(inspection.ArchivePath, "*" + QuestExtension);

        try
        {
            // Переносится ОДИН файл, а не папка: квесты кампании лежат в общей
            // папке `quests`, и удаление её целиком стёрло бы соседние квесты.
            var staged = Directory
                .EnumerateFiles(staging, "*" + QuestExtension, SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException("В архиве нет файла квеста (.aqquest).");

            // Каталог назначения уже создан выше, поэтому Move не подменит
            // целевой файл папкой с тем же именем.
            File.Move(staged, targetFile, overwrite: true);
        }
        finally
        {
            Cleanup(staging);
        }

        var displayName = string.IsNullOrWhiteSpace(inspection.Manifest.FullName)
            ? inspection.Manifest.Name
            : inspection.Manifest.FullName!;

        AppLogger.Info("ResourceImportService: квест импортирован.",
            $"campaign={campaign.Definition.Id}; file={targetFile}; " +
            $"id={inspection.Manifest.Id}; overwrite={overwrite}");

        return new ResourceImportResult(
            WorldArchiveKinds.Quest, inspection.Manifest.Id, displayName, targetFile, overwrite);
    }

    /// <summary>
    /// Распаковывает архив во временную папку и требует наличия файла ресурса.
    ///
    /// Требование проверяется ДО переноса: без него на месте появился бы ресурс,
    /// который стор не увидит, — «импорт прошёл, а кампании нет».
    /// </summary>
    private static string UnpackToStaging(string archivePath, string expectedPattern)
    {
        var staging = Path.Combine(Path.GetTempPath(), "aq-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            WorldArchiveService.Unpack(archivePath, staging);

            if (Directory.EnumerateFiles(staging, expectedPattern, SearchOption.AllDirectories).Any())
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

    private static void Cleanup(string staging)
    {
        if (!Directory.Exists(staging))
            return;

        try { Directory.Delete(staging, recursive: true); }
        catch { /* уборка не должна скрывать исходную ошибку */ }
    }

    /// <summary>
    /// Отвергает архив другого вида.
    ///
    /// Импорт «наугад» (что бы ни лежало в архиве) положил бы папку мира внутрь
    /// кампании, и разбираться пришлось бы по содержимому диска.
    /// </summary>
    private static void EnsureKind(ArchiveInspection inspection, string expected)
    {
        if (!inspection.Manifest.Kind.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Архив содержит «" + inspection.Manifest.Kind + "», а не «" + expected + "».");
        }
    }

    /// <summary>
    /// Читает файл кампании из распакованной папки.
    ///
    /// Возвращает <c>null</c> вместо исключения: файл уже перенесён на место, и
    /// «не читается» нужно объяснить с путём, а не выбросить наружу на середине.
    /// </summary>
    private static CampaignDefinition? ReadCampaign(string folder)
    {
        var path = WorldPaths.CampaignFilePath(folder);
        if (!File.Exists(path))
            return null;

        try
        {
            // Десериализатор возвращает nullable: пустой или чужой документ
            // разбирается без исключения, поэтому null проверяется явно.
            var document = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(
                File.ReadAllText(path));
            return document?.Definition;
        }
        catch
        {
            return null;
        }
    }
}
