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
    /// Записывает автосохранение демо-мира из поставляемого архива.
    ///
    /// Используется кнопкой «Пропустить»: она импортирует <c>data/DemoWorld.aqezip</c>
    /// — то есть ровно тот же архив, что и любой другой автор. Отдельного
    /// «сгенерировать демо-мир» пути нет намеренно: он неизбежно разошёлся бы с
    /// импортом, и «Пропустить» перестало бы проверять настоящую дорогу.
    /// </summary>
    public WorldRecord ImportBundledDemoWorld(bool overwrite = false)
    {
        var archive = DemoWorldSeeder.BundledArchivePath;
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException(
                "Поставляемый демо-мир не найден. Ожидался файл: " + archive, archive);
        }

        return ImportWorldFromArchive(archive, overwrite);
    }

    public void SaveWorld(WorldRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_readOnly)
            throw new InvalidOperationException("Каталог миров открыт только для чтения.");

        var existing = FindWorld(record.Definition.Id);
        var previous = existing?.Definition.Metadata;
        var next = record.Definition with
        {
            Metadata = (previous ?? new ResourceMetadata()).WithModified(_author, DateTimeOffset.UtcNow)
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
/// Демонстрационный мир, поставляемый рядом с приложением в виде архива
/// <c>data/DemoWorld.aqezip</c>.
///
/// Зачем архив, а не папка: демо-мир должен попадать в пользовательскую папку
/// ровно так же, как любой другой импортируемый мир, — то есть через тот же
/// код импорта. Тогда «Пропустить» проверяет ту самую дорогу, которой пойдут
/// архивы от других авторов, а не отдельную ветку, которая может расходиться.
///
/// Содержимое — намеренно пустая структура: только файл мира, корень campaigns
/// и каталог Saves. Общая кампания и демонстрационный квест здесь НЕ создаются.
/// </summary>
public static class DemoWorldSeeder
{
    /// <summary>Id демо-мира. Фиксированный: по нему «Пропустить» его находит.</summary>
    public const string WorldId = "demo";

    /// <summary>Короткое имя мира (папка и адресация).</summary>
    public const string WorldName = "DemoWorld";

    /// <summary>
    /// Имя для человека. Именно оно становится именем папки при импорте, потому
    /// что пользователь видит папки глазами.
    /// </summary>
    public const string WorldFullName = "Демо Мир";

    /// <summary>Описание честно говорит, что ресурс пока пустой.</summary>
    public const string WorldDescription =
        "Пока пустой ресурс. Создан для знакомства со структурой Assist Quest Editor.";

    /// <summary>
    /// Момент создания демо-мира.
    ///
    /// ФИКСИРОВАННЫЙ, а не текущее время: иначе архив менялся бы при каждой
    /// пересборке, и проверить «тот ли файл лежит в поставке» стало бы
    /// невозможно — пришлось бы сверять содержимое вручную с выгруженным
    /// файлом. Вместе с фиксированными метками записей архива
    /// (<see cref="WorldArchiveService"/>) это даёт побайтово воспроизводимый
    /// результат, а значит проверку «код и файл совпадают» в CI.
    /// </summary>
    public static readonly DateTimeOffset DemoMoment =
        new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Подпись демо-мира. Фиксированная по той же причине, что и момент.</summary>
    public const string DemoAuthor = "Assist Quest Editor";

    /// <summary>Путь поставляемого архива рядом с приложением.</summary>
    public static string BundledArchivePath =>
        Path.Combine(AppPaths.ResourceRoot, WorldArchiveRules.DemoWorldFileName);

    /// <summary>Есть ли поставляемый архив. Его отсутствие — не ошибка: сборка могла быть без демо.</summary>
    public static bool BundledArchiveExists => File.Exists(BundledArchivePath);

    /// <summary>
    /// Собирает демо-мир в папке и упаковывает его в архив.
    ///
    /// Сборка идёт через обычные писатели ресурсов
    /// (<see cref="WorldContentSeeder"/>), а не через отдельные шаблоны: только
    /// так гарантируется, что демо-мир — это валидный мир, а не набор строк,
    /// который придётся поддерживать отдельно.
    /// </summary>
    public static ArchivePackResult BuildArchive(string archivePath, string author, DateTimeOffset moment)
    {
        var staging = Path.Combine(Path.GetTempPath(), "aq-demo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var worldFolder = Path.Combine(staging, ResourceNaming.ToFolderName(WorldFullName));
            Directory.CreateDirectory(worldFolder);

            var world = new WorldDefinition(
                Id: WorldId,
                Name: WorldName,
                FullName: WorldFullName,
                Description: WorldDescription,
                Version: 1,
                Metadata: new ResourceMetadata().WithCreated(author, moment),
                LastCampaignId: null);

            File.WriteAllText(
                WorldPaths.WorldFilePath(worldFolder),
                ResourceJsonFormat.Serialize(new WorldDefinitionDocument(
                    1, WorldDefinitionRules.FormatName, world)));

            // Демо-архив намеренно ОСТАЁТСЯ ПУСТЫМ по содержимому:
            // ни Common-кампании, ни квеста внутри него нет. Сохраняем только
            // будущую структуру каталогов, чтобы первый запуск учил раскладке,
            // но не подсовывал тестовый ресурс как часть нового мира.
            Directory.CreateDirectory(WorldPaths.CampaignsRoot(worldFolder));
            Directory.CreateDirectory(WorldPaths.SavesFolderPath(worldFolder));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);

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
                // Не убирать временную папку неприятно, но уронить из-за этого
                // сборку демо-мира — хуже: результат уже получен.
                AppLogger.Warn("DemoWorldSeeder: не удалось удалить временную папку.", staging + "; " + ex.Message);
            }
        }
    }
}
