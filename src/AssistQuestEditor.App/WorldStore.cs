using AssistQuestEditor.Domain;
using System.Text.Json;

namespace AssistQuestEditor.App;

/// <summary>
/// Мир в списке миров: определение плюс его папка на диске.
/// </summary>
public sealed record WorldRecord(
    WorldDefinition Definition,
    string FolderPath)
{
    public string WorldFilePath => WorldPaths.WorldFilePath(FolderPath);
    public string DisplayName => string.IsNullOrWhiteSpace(Definition.FullName)
        ? Definition.Name
        : Definition.FullName!;
}

/// <summary>
/// Каталог миров и их кампаний.
///
/// Это единственный источник истины о контенте. Раньше каталог кампаний читался
/// из пользовательской папки `quests`, а исходные квесты лежали ещё и рядом с
/// EXE в `data` — две копии одного и того же, между которыми не было
/// однозначного соответствия.
///
/// Раскладка описана в <see cref="WorldPaths"/> (Domain): правила путей —
/// контракт обмена, их нельзя держать в слое файлового доступа, иначе импорт у
/// другого игрока сломает структуру.
/// </summary>
public sealed class WorldStore
{
    private const int SupportedSchemaVersion = 1;

    private readonly string _userRoot;
    private readonly bool _readOnly;
    private readonly string _author;
    private readonly List<WorldRecord> _worlds = new();

    public WorldStore(string userRoot, string author, bool readOnly = false)
    {
        if (string.IsNullOrWhiteSpace(userRoot))
            throw new ArgumentException("Корень пользовательских данных не задан.", nameof(userRoot));

        _userRoot = Path.GetFullPath(userRoot);
        _author = author;
        _readOnly = readOnly;
        Reload();
    }

    public string UserRoot => _userRoot;

    public bool IsReadOnly => _readOnly;

    public IReadOnlyList<WorldRecord> Worlds =>
        _worlds
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Есть ли хотя бы один мир.
    ///
    /// Нужно стартовой фазе приложения: без миров пользователю показывается
    /// уведомление и пульсирующее поле «нужно создать мир», и приложение не
    /// должно пропускать его к работе, пока мир не создан.
    /// </summary>
    public bool HasWorlds => _worlds.Count > 0;

    public WorldRecord? FindWorld(string worldId) =>
        string.IsNullOrWhiteSpace(worldId)
            ? null
            : _worlds.FirstOrDefault(item =>
                item.Definition.Id.Equals(worldId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Создаёт мир вместе с общей кампанией и демонстрационным контентом.
    ///
    /// Общая кампания создаётся ВСЕГДА: квест не может существовать вне
    /// кампании, и мир без кампании был бы миром, в котором нечего создавать.
    /// Демо-контент внутри неё нужен, чтобы новый мир не был пустым экраном:
    /// автор сразу видит пример работающего квеста.
    /// </summary>
    public WorldRecord CreateWorld(string name, string? fullName = null, string? description = null)
    {
        if (_readOnly)
            throw new InvalidOperationException("Каталог миров открыт только для чтения.");

        if (!ResourceNaming.IsValidName(name))
            throw new InvalidOperationException(
                "Недопустимое имя мира: используйте буквы, цифры, пробел и подчёркивание.");

        var folderName = ResourceNaming.ToFolderName(name);
        var folder = WorldPaths.WorldFolder(_userRoot, folderName);
        if (Directory.Exists(folder))
            throw new InvalidOperationException("Мир с таким именем уже существует: " + name);

        var now = DateTimeOffset.UtcNow;
        var world = new WorldDefinition(
            Id: MakeId(folderName),
            Name: name,
            FullName: fullName,
            Description: description,
            Version: 1,
            Metadata: new ResourceMetadata().WithCreated(_author, now),
            LastCampaignId: WorldDefinitionRules.CommonCampaignIdValue);

        Directory.CreateDirectory(folder);
        WriteWorld(new WorldRecord(world, folder));

        // Папка сохранений создаётся сразу. Пустой каталог нельзя создать
        // «когда понадобится»: автосохранение пишет файл при первой остановке, и
        // оно упало бы на отсутствующем каталоге — уже после того, как автор
        // что-то сделал. Плюс автору видно, где лежит прохождение.
        Directory.CreateDirectory(WorldPaths.SavesFolderPath(folder));

        // Общая кампания с демо-контентом: системная сущность мира.
        var campaignFolder = WorldPaths.CampaignFolder(
            folder,
            ResourceNaming.ToFolderName(WorldDefinitionRules.CommonCampaignIdValue));

        WorldContentSeeder.SeedCommonCampaign(campaignFolder, world.Id, _author, now);

        AppLogger.Info("WorldStore: мир создан.",
            $"worldId={world.Id}; folder={folder}; commonCampaign={campaignFolder}");

        Reload();
        return FindWorld(world.Id)!;
    }

    /// <summary>
    /// Импортирует мир из архива в каталог миров.
    ///
    /// <paramref name="overwrite"/> решает судьбу существующего мира с той же
    /// папкой: при <c>true</c> он заменяется целиком, при <c>false</c> архив
    /// распаковывается рядом под свободным именем («Демо Мир (2)»). Молчаливой
    /// перезаписи здесь нет намеренно — импорт приходит от другого человека, и
    /// уничтожить свою работу одним нажатием недопустимо.
    ///
    /// Распаковка идёт во ВРЕМЕННУЮ папку, и только затем переносится на место:
    /// прерванный импорт не должен оставлять полураспакованный мир, который
    /// выглядит рабочим.
    /// </summary>
    public WorldRecord ImportWorldFromArchive(string archivePath, bool overwrite)
    {
        if (_readOnly)
            throw new InvalidOperationException("Каталог миров открыт только для чтения.");

        var inspection = WorldArchiveService.Inspect(archivePath);
        if (!inspection.Manifest.Kind.Equals(WorldArchiveKinds.World, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Архив содержит " + inspection.Manifest.Kind + ", а не мир. " +
                "Кампанию и квест импортируют в их родителя.");
        }

        var desired = WorldArchiveImportRules.TargetFolderName(inspection.Manifest);
        var roots = WorldPaths.WorldsRoot(_userRoot);
        Directory.CreateDirectory(roots);

        var targetName = overwrite
            ? desired
            : WorldArchiveImportRules.UniqueFolderName(
                desired,
                name => Directory.Exists(Path.Combine(roots, name)));

        var targetFolder = Path.Combine(roots, targetName);
        var staging = Path.Combine(Path.GetTempPath(), "aq-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            WorldArchiveService.Unpack(archivePath, staging);

            // Распакованное обязано содержать файл мира: иначе в каталоге миров
            // появилась бы папка, которую WorldStore не увидит, — «импорт прошёл,
            // а мира нет».
            var stagedWorldFile = WorldPaths.WorldFilePath(staging);
            if (!File.Exists(stagedWorldFile))
            {
                // Файл мира может лежать в ОДНОЙ вложенной папке — так архивы и
                // выгружаются (папка мира + содержимое). Это не ошибка упаковки,
                // поэтому подпапка разворачивается: иначе импорт отвергал бы
                // собственные архивы.
                var nested = Directory
                    .EnumerateFiles(staging, WorldPaths.WorldFileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (nested is null)
                {
                    throw new InvalidDataException(
                        "В архиве нет " + WorldPaths.WorldFileName + ": это не мир.");
                }

                var nestedRoot = Path.GetDirectoryName(nested)!;
                if (!nestedRoot.Equals(staging, StringComparison.OrdinalIgnoreCase))
                {
                    // Перенос содержимого подпапки на уровень выше: целевая папка
                    // мира задаётся каталогом миров и именем ресурса, а не именем
                    // папки внутри архива.
                    foreach (var item in Directory.EnumerateFileSystemEntries(nestedRoot))
                    {
                        var destination = Path.Combine(staging, Path.GetFileName(item));
                        if (Directory.Exists(item))
                            Directory.Move(item, destination);
                        else
                            File.Move(item, destination);
                    }

                    Directory.Delete(nestedRoot, recursive: true);
                }
            }

            var stagedDefinitionPath = WorldPaths.WorldFilePath(staging);
            var stagedDefinition = File.Exists(stagedDefinitionPath)
                ? ReadWorldDefinition(staging)
                : null;

            if (stagedDefinition is null)
                throw new InvalidDataException("Импортируемый world.aqworld не читается.");

            if (!overwrite && !string.Equals(targetName, desired, StringComparison.OrdinalIgnoreCase))
            {
                // «Рядом» означает действительно НОВЫЙ ресурс: новый каталог
                // должен получить новый стабильный Id, иначе WorldStore сольёт
                // оригинал и копию по одному Id и одну из них скроет.
                stagedDefinition = stagedDefinition with
                {
                    Id = MakeId(targetName),
                    Name = targetName,
                    FullName = null,
                    LastCampaignId = null
                };

                File.WriteAllText(
                    stagedDefinitionPath,
                    ResourceJsonFormat.Serialize(new WorldDefinitionDocument(
                        1, WorldDefinitionRules.FormatName, stagedDefinition)));
            }

            if (Directory.Exists(targetFolder))
            {
                if (overwrite)
                {
                    var existingDefinition = ReadWorldDefinition(targetFolder);
                    EnsureOverwriteDiffers(
                        inspection.Manifest.Version,
                        inspection.Manifest.Metadata?.ModifiedOn,
                        existingDefinition?.Version,
                        existingDefinition?.Metadata?.ModifiedOn,
                        "Мир");

                    Directory.Delete(targetFolder, recursive: true);
                    AppLogger.Info("WorldStore: существующий мир заменяется при импорте.", targetFolder);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Папка назначения занята: " + targetFolder);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFolder)!);
            Directory.Move(staging, targetFolder);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch { /* уборка не должна скрывать исходную ошибку */ }
            }

            throw;
        }

        Reload();
        var imported = FindWorld(MakeId(targetName))
            ?? Worlds.FirstOrDefault(record =>
                record.FolderPath.Equals(targetFolder, StringComparison.OrdinalIgnoreCase));

        AppLogger.Info("WorldStore: мир импортирован из архива.",
            $"archive={archivePath}; folder={targetFolder}; overwrite={overwrite}; " +
            $"worldId={imported?.Definition.Id ?? "не найден"}");

        return imported ?? throw new InvalidOperationException(
            "Мир распакован, но не читается: проверьте " + WorldPaths.WorldFilePath(targetFolder));
    }

    /// <summary>
    /// Импортирует демо-мир тем же <see cref="ImportWorldFromArchive"/> путём,
    /// что и любой пользовательский архив.
    ///
    /// Старый/повреждённый bundled archive не должен превращаться в пустой или
    /// невалидный первый мир после обновления приложения. Если архив не содержит
    /// актуальный Training/Dispatcher набор, он восстанавливается во временный
    /// файл canonical-упаковщиком DemoWorldSeeder, затем импортируется тем же
    /// обычным кодом архива.
    /// </summary>
    public WorldRecord ImportBundledDemoWorld(bool overwrite = false)
    {
        var archive = DemoWorldSeeder.BundledArchivePath;

        if (File.Exists(archive) && IsCurrentBundledDemoWorld(archive))
            return ImportWorldFromArchive(archive, overwrite);

        var tempArchive = Path.Combine(
            Path.GetTempPath(),
            "aq-demo-import-" + Guid.NewGuid().ToString("N") + WorldArchiveRules.Extension);

        try
        {
            DemoWorldSeeder.BuildArchive(
                tempArchive,
                DemoWorldSeeder.DemoAuthor,
                DemoWorldSeeder.DemoMoment);

            return ImportWorldFromArchive(tempArchive, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchive))
                    File.Delete(tempArchive);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "WorldStore: не удалось удалить временный демо-архив.",
                    tempArchive + "; " + ex.Message);
            }
        }
    }

    private static bool IsCurrentBundledDemoWorld(string archivePath)
    {
        try
        {
            var inspection = WorldArchiveService.Inspect(archivePath);
            var expected = new[]
            {
                "world.aqworld",
                "campaigns/training/campaign.aqcampaign",
                "campaigns/training/quests/test_dynamic_cache.aqquest",
                "locations/training_dynamic_cache_location.aqlocation",
                "events/test_dynamic_cache.aqevent"
            };

            return inspection.Manifest.Id.Equals(
                       DemoWorldSeeder.WorldId,
                       StringComparison.OrdinalIgnoreCase) &&
                   expected.All(path =>
                       inspection.Entries.Any(entry =>
                           entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "WorldStore: bundled DemoWorld нельзя проверить, он будет пересобран.",
                archivePath + "; " + ex.Message);
            return false;
        }
    }

    public void SaveWorld(WorldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_readOnly)
            throw new InvalidOperationException("Каталог миров открыт только для чтения.");

        var existing = FindWorld(record.Definition.Id);
        var previous = existing?.Definition.Metadata;
        var moment = DateTimeOffset.UtcNow;
        var next = record.Definition with
        {
            Version = Math.Max(1, record.Definition.Version) + 1,
            Metadata = (previous ?? new ResourceMetadata()).WithModified(_author, moment)
        };

        WriteWorld(record with { Definition = next });
        Reload();
    }

    /// <summary>
    /// Обновляет свойства мира из окна свойств.
    ///
    /// ДАТЫ СОЗДАНИЯ НЕ ТРОГАЮТСЯ: автор и дата создания — то, по чему решается
    /// перезапись при импорте у получателя, и правка описания не должна
    /// «обновлять» факт создания.
    ///
    /// Идентификатор НЕ пересчитывается от нового имени: на него уже ссылаются
    /// кампании (`worldId`), сохранения текущего прохождения и записи о
    /// последней кампании. Переименование, меняющее id, разом порвало бы все
    /// ссылки — а имя видит только человек.
    /// </summary>
    public WorldRecord UpdateWorld(
        string worldId,
        string name,
        string? fullName,
        string? description,
        string? imageFileName)
    {
        if (_readOnly)
            throw new InvalidOperationException("Каталог миров открыт только для чтения.");

        var record = FindWorld(worldId)
            ?? throw new InvalidOperationException("Мир не найден: " + worldId);

        if (!ResourceNaming.IsValidName(name))
            throw new InvalidOperationException(
                "Недопустимое имя мира: используйте буквы, цифры, пробел и подчёркивание.");

        var moment = DateTimeOffset.UtcNow;
        var definition = record.Definition with
        {
            Name = name.Trim(),
            FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            ImageFile = string.IsNullOrWhiteSpace(imageFileName) ? null : imageFileName,
            Version = Math.Max(1, record.Definition.Version) + 1,
            Metadata = (record.Definition.Metadata ?? new ResourceMetadata())
                .WithModified(_author, moment)
        };

        // Файл мира лежит по имени ПАПКИ, поэтому переименование мира не двигает
        // папку: перенос каталога разорвал бы ссылки из сохранений и ярлыков.
        WriteWorld(record with { Definition = definition });
        Reload();

        AppLogger.Info("WorldStore: свойства мира обновлены.",
            $"worldId={worldId}; name={definition.Name}; image={definition.ImageFile}");

        return FindWorld(worldId) ?? throw new InvalidOperationException(
            "Мир обновлён, но не читается: " + record.WorldFilePath);
    }

    /// <summary>
    /// Запоминает последнюю открытую кампанию мира.
    ///
    /// Хранится в файле мира, а не в настройках интерфейса: «какая кампания
    /// открыта» — свойство мира, и при передаче папки другому игроку он должен
    /// получить тот же контекст.
    /// </summary>
    public void RememberCampaign(string worldId, string campaignId)
    {
        if (_readOnly)
            return;

        var record = FindWorld(worldId);
        if (record is null)
            return;

        if (string.Equals(record.Definition.LastCampaignId, campaignId, StringComparison.OrdinalIgnoreCase))
            return;

        WriteWorld(record with
        {
            Definition = record.Definition with { LastCampaignId = campaignId }
        });
        Reload();
    }

    public void Reload()
    {
        _worlds.Clear();
        var root = WorldPaths.WorldsRoot(_userRoot);
        if (!_readOnly)
            Directory.CreateDirectory(root);

        if (!Directory.Exists(root))
        {
            AppLogger.Info("WorldStore: каталог миров отсутствует.", $"root={root}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, WorldPaths.WorldFileName, SearchOption.AllDirectories))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<WorldDefinitionDocument>(File.ReadAllText(file));
                if (document?.Definition is null ||
                    document.SchemaVersion != SupportedSchemaVersion ||
                    !document.Format.Equals(WorldDefinitionRules.FormatName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var folder = Path.GetDirectoryName(file);
                if (string.IsNullOrWhiteSpace(folder))
                    continue;

                if (!_worlds.Any(item =>
                        item.Definition.Id.Equals(document.Definition.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    _worlds.Add(new WorldRecord(document.Definition, folder));
                }
            }
            catch (Exception ex)
            {
                // Повреждённый мир не должен скрывать остальные: их можно
                // продолжать использовать, а причина попадает в лог.
                AppLogger.Error("WorldStore: ошибка чтения мира.", ex, file);
            }
        }

        AppLogger.Info("WorldStore: каталог миров загружен.",
            $"worlds={_worlds.Count}; root={root}; readOnly={_readOnly}");
    }

    private void WriteWorld(WorldRecord record)
    {
        Directory.CreateDirectory(record.FolderPath);
        var document = new WorldDefinitionDocument(
            SupportedSchemaVersion,
            WorldDefinitionRules.FormatName,
            record.Definition);

        File.WriteAllText(record.WorldFilePath, ResourceJsonFormat.Serialize(document));
    }

    /// <summary>
    /// Id мира по имени папки.
    ///
    /// Не Guid: id должен читаться человеком в файлах кампаний и квестов
    /// («quest.worldId = sibir_map» понятнее, чем 32 hex-символа), а
    /// уникальность обеспечивается именем папки, которое проверяется на
    /// существование до создания.
    /// </summary>
    private static WorldDefinition? ReadWorldDefinition(string folder)
    {
        var path = WorldPaths.WorldFilePath(folder);
        if (!File.Exists(path))
            return null;

        try
        {
            var document = ResourceJsonFormat.Deserialize<WorldDefinitionDocument>(File.ReadAllText(path));
            return document?.Definition;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureOverwriteDiffers(
        int incomingVersion,
        DateTimeOffset? incomingModified,
        int? existingVersion,
        DateTimeOffset? existingModified,
        string kind)
    {
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

    private static string MakeId(string folderName) =>
        folderName.Trim().Replace(' ', '_').ToLowerInvariant();
}

/// <summary>
/// Начальный контент нового мира: общая кампания плюс демонстрационный квест.
///
/// Живёт отдельно от <see cref="WorldStore"/>: это СОДЕРЖАНИЕ, а не файловая
/// операция, и его правят авторы шаблонов, а не тот, кто работает с путями.
/// </summary>
public static class WorldContentSeeder
{
    public const string DemoQuestId = "common_intro";

    public static void SeedCommonCampaign(
        string campaignFolder,
        string worldId,
        string author,
        DateTimeOffset moment)
    {
        Directory.CreateDirectory(campaignFolder);

        var campaign = new CampaignDefinition(
            Id: WorldDefinitionRules.CommonCampaignIdValue,
            Name: "Common",
            Version: 1,
            Active: true,
            Quests: new[]
            {
                new CampaignQuestEntry(
                    DemoQuestId,
                    Path.Combine(WorldPaths.QuestsFolder, DemoQuestId + ".aqquest"),
                    1,
                    CampaignQuestStatus.Enabled,
                    1)
            },
            Files: Array.Empty<string>(),
            Geo: GeoCoordinate.CreateDefault(),
            StartDate: GameCalendar.DefaultStartDate,
            Metadata: new ResourceMetadata().WithCreated(author, moment));

        WriteCampaign(campaignFolder, campaign, worldId);

        var questsFolder = WorldPaths.QuestsFolderPath(campaignFolder);
        Directory.CreateDirectory(questsFolder);
        File.WriteAllText(
            Path.Combine(questsFolder, DemoQuestId + ".aqquest"),
            ResourceJsonFormat.Serialize(QuestDefinitionLoader.CreateDocument(
                QuestGraphFactory.CreateStarter(),
                author,
                moment,
                worldId,
                WorldDefinitionRules.CommonCampaignIdValue)));
    }

    public static void WriteCampaign(
        string campaignFolder,
        CampaignDefinition definition,
        string worldId)
    {
        var document = new CampaignDefinitionDocument(
            1,
            "aqcampaign",
            definition with { WorldId = worldId });

        Directory.CreateDirectory(campaignFolder);
        File.WriteAllText(
            WorldPaths.CampaignFilePath(campaignFolder),
            ResourceJsonFormat.Serialize(document));
    }
}

/// <summary>
/// Демонстрационный мир, поставляемый рядом с приложением в виде архива.
/// Внутри есть минимальный учебный контент: кампания «Обучение», квест
/// «Тестовый динамический тайник», отдельная Location и Dynamic Event.
/// </summary>
public static class DemoWorldSeeder
{
    public const string WorldId = "demo";
    public const string WorldName = "DemoWorld";
    public const string WorldFullName = "Демо Мир";
    public const string WorldDescription =
        "Учебный мир для проверки Location, Dynamic Event Dispatcher и Quest Runtime.";

    public const string TrainingCampaignId = "training";
    public const string CacheQuestId = "test_dynamic_cache";
    public const string CacheLocationId = "training_dynamic_cache_location";
    public const string CacheEventId = "test_dynamic_cache";

    public static readonly DateTimeOffset DemoMoment =
        new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public const string DemoAuthor = "Assist Quest Editor";

    public static string BundledArchivePath =>
        Path.Combine(AppPaths.ResourceRoot, WorldArchiveRules.DemoWorldFileName);

    public static bool BundledArchiveExists => File.Exists(BundledArchivePath);

    /// <summary>
    /// Собирает демо-мир обычным WorldArchiveService. Эти же canonical resource
    /// writers используются в пользовательском контенте.
    /// </summary>
    public static ArchivePackResult BuildArchive(
        string archivePath,
        string author,
        DateTimeOffset moment)
    {
        var staging = Path.Combine(
            Path.GetTempPath(),
            "aq-demo-" + Guid.NewGuid().ToString("N"));

        try
        {
            var worldFolder = Path.Combine(
                staging,
                ResourceNaming.ToFolderName(WorldFullName));
            Directory.CreateDirectory(worldFolder);

            var world = new WorldDefinition(
                Id: WorldId,
                Name: WorldName,
                FullName: WorldFullName,
                Description: WorldDescription,
                Version: 1,
                Metadata: new ResourceMetadata().WithCreated(author, moment),
                LastCampaignId: TrainingCampaignId);

            File.WriteAllText(
                WorldPaths.WorldFilePath(worldFolder),
                ResourceJsonFormat.Serialize(
                    new WorldDefinitionDocument(
                        1,
                        WorldDefinitionRules.FormatName,
                        world)));

            Directory.CreateDirectory(WorldPaths.SavesFolderPath(worldFolder));

            var campaignFolder = WorldPaths.CampaignFolder(
                worldFolder,
                TrainingCampaignId);
            var questsFolder = WorldPaths.QuestsFolderPath(campaignFolder);
            Directory.CreateDirectory(questsFolder);
            Directory.CreateDirectory(WorldPaths.ScenesFolderPath(campaignFolder));

            var campaign = new CampaignDefinition(
                Id: TrainingCampaignId,
                Name: TrainingCampaignId,
                Version: 1,
                Active: true,
                Quests: new[]
                {
                    new CampaignQuestEntry(
                        CacheQuestId,
                        Path.Combine(
                            WorldPaths.QuestsFolder,
                            CacheQuestId + ".aqquest"),
                        1,
                        CampaignQuestStatus.Enabled,
                        1)
                },
                Files: new[]
                {
                    WorldPaths.CampaignFileName,
                    Path.Combine(
                        WorldPaths.QuestsFolder,
                        CacheQuestId + ".aqquest")
                },
                Geo: GeoCoordinate.CreateDefault(),
                StartDate: GameCalendar.DefaultStartDate,
                StartConditions: new WorldStartConditions(
                    Weather: "Ясно",
                    RainPercent: 0,
                    VisibilityMeters: 10000),
                WorldId: WorldId,
                FullName: "Обучение",
                Description: "Учебная кампания для проверки Диспетчера динамических событий.",
                Metadata: new ResourceMetadata().WithCreated(author, moment));

            WorldContentSeeder.WriteCampaign(campaignFolder, campaign, WorldId);

            var quest = CreateTrainingQuest(author, moment);
            File.WriteAllText(
                Path.Combine(questsFolder, CacheQuestId + ".aqquest"),
                ResourceJsonFormat.Serialize(
                    new QuestDefinitionDocument(1, "aqquest", quest)));

            var locationFolder = Path.Combine(
                worldFolder,
                WorldPaths.LocationsFolder);
            Directory.CreateDirectory(locationFolder);

            var location = CreateTrainingLocation();
            File.WriteAllText(
                Path.Combine(
                    locationFolder,
                    CacheLocationId + LocationStore.Extension),
                ResourceJsonFormat.Serialize(
                    new LocationDefinitionDocument(
                        1,
                        "aqlocation",
                        location)));

            var eventFolder = Path.Combine(
                worldFolder,
                WorldPaths.DynamicEventsFolder);
            Directory.CreateDirectory(eventFolder);

            var dynamicEvent = CreateTrainingDynamicEvent();
            File.WriteAllText(
                Path.Combine(
                    eventFolder,
                    CacheEventId + DynamicEventStore.Extension),
                ResourceJsonFormat.Serialize(
                    new DynamicEventDefinitionDocument(
                        1,
                        DynamicEventDefinitionRules.FormatName,
                        dynamicEvent)));

            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(archivePath))!);

            return WorldArchiveService.Pack(
                worldFolder,
                archivePath,
                new WorldArchiveManifest(
                    WorldArchiveKinds.World,
                    WorldId,
                    WorldName,
                    FullName: WorldFullName,
                    Description: WorldDescription,
                    Version: 1,
                    Metadata: world.Metadata),
                includeDependencies: true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "DemoWorldSeeder: не удалось удалить временную папку.",
                    staging + "; " + ex.Message);
            }
        }
    }

    private static QuestDefinition CreateTrainingQuest(
        string author,
        DateTimeOffset moment)
    {
        var nodes = new[]
        {
            Node("start", "Start", "01. Тайник появился", 80, 220),
            Node(
                "notify",
                "Notify",
                "02. Тайник обнаружен",
                340,
                220,
                ("message", "Ты обнаружил динамический тайник. Загляни внутрь и забери находки.")),
            Node(
                "note",
                "GiveItem",
                "03. Записка",
                600,
                170,
                ("itemId", "note"),
                ("count", "1")),
            Node(
                "money",
                "AddMoney",
                "04. 5 000 рублей",
                860,
                170,
                ("amount", "5000")),
            Node(
                "scout",
                "AddSkill",
                "05. Разведчик +50",
                1120,
                170,
                ("skillId", "scout"),
                ("name", "Разведчик"),
                ("description", "Навык поиска и обнаружения скрытых тайников."),
                ("kind", "Levelled"),
                ("amount", "50"),
                ("maxLevel", "100")),
            Node("end", "End", "06. Тайник завершён", 1380, 170)
        };

        var connections = new[]
        {
            Connection("start", "out", "notify", "in"),
            Connection("notify", "out", "note", "in"),
            Connection("note", "out", "money", "in"),
            Connection("money", "out", "scout", "in"),
            Connection("scout", "out", "end", "in")
        };

        return new QuestDefinition(
            CacheQuestId,
            "Тестовый динамический тайник",
            "Демонстрационный квест, запускаемый Dispatcher при обнаружении динамического тайника.",
            new QuestGraph(
                CacheQuestId,
                "Тестовый динамический тайник",
                nodes,
                connections),
            Array.Empty<string>(),
            Activation: new QuestActivation(QuestStartMode.Manual),
            Version: 1,
            WorldId: WorldId,
            CampaignId: TrainingCampaignId,
            Metadata: new ResourceMetadata().WithCreated(author, moment));
    }

    private static LocationDefinition CreateTrainingLocation() =>
        new(
            CacheLocationId,
            "Точки тестовых динамических тайников")
        {
            Description =
                "Кандидаты рядом с дорогой, не ближе 1 км к ближайшему городу, " +
                "в пределах 1–5 км от игрока и только в заданных типах объектов.",
            Mode = LocationMode.Dynamic,
            TriggerRadius = 35,
            Query = new LocationQueryDefinition
            {
                Criteria = new[]
                {
                    new LocationCriterion(
                        "categoryisany",
                        new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["value"] =
                                "ruined_civ|ruins_ind|crashed_car|dead_car|" +
                                "forest_fire|graveyard|road_grave"
                        }),
                    new LocationCriterion(
                        "nearbyroad",
                        new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["meters"] = "150"
                        }),
                    new LocationCriterion(
                        "distancefromnearestcity",
                        new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["meters"] = "1000"
                        }),
                    new LocationCriterion(
                        "distancefromplayer",
                        new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["meters"] = "1000-5000"
                        })
                },
                History = new LocationHistoryConstraints
                {
                    MaxSelectionCount = 0
                }
            }
        };

    private static DynamicEventDefinition CreateTrainingDynamicEvent() =>
        new(
            CacheEventId,
            "Тестовый динамический тайник")
        {
            Description =
                "Тайник для проверки полного цикла Dispatcher: первичное создание, " +
                "автообнаружение, Quest Runtime, задержка 5 минут и пересоздание через 10 минут.",
            LocationId = CacheLocationId,
            QuestId = CacheQuestId,
            TriggerRadius = 35,
            Category = "Тайник",
            Trigger = new DynamicEventTriggerDefinition
            {
                Type = "DynamicEventDiscovery",
                SourceDefinitionId = CacheEventId,
                MinGameHours = 5d / 60d,
                MaxGameHours = 5d / 60d
            },
            SpawnPolicy = new DynamicEventSpawnPolicy
            {
                MaxActiveInstances = 1,
                SpawnChance = 1d,
                LifetimeGameHours = 10d / 60d,
                SpawnOnSimulationStart = true,
                RespawnOnExpired = true,
                RemoveOnCompleted = true
            },
            Presentation = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["discovery"] = "WorldMarker",
                ["category"] = "Тайник"
            }
        };

    private static QuestNode Node(
        string id,
        string type,
        string title,
        double x,
        double y,
        params (string Key, string Value)[] parameters)
    {
        var sockets = type switch
        {
            "Start" => new[]
            {
                new SocketDefinition(id + ".out", "Далее", SocketDirection.Output)
            },
            "End" => new[]
            {
                new SocketDefinition(id + ".in", "Вход", SocketDirection.Input)
            },
            _ => new[]
            {
                new SocketDefinition(id + ".in", "Вход", SocketDirection.Input),
                new SocketDefinition(id + ".out", "Далее", SocketDirection.Output)
            }
        };

        return new QuestNode(id, type, title, x, y, sockets)
        {
            Parameters = parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static QuestConnection Connection(
        string fromNode,
        string fromSocket,
        string toNode,
        string toSocket) =>
        new(
            fromNode,
            fromNode + "." + fromSocket,
            toNode,
            toNode + "." + toSocket);
}
