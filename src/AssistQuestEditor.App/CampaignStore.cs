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
    QuestActivation? Activation,
    int Order);

public sealed record InstalledCampaignView(
    string Id,
    string Name,
    int Version,
    bool Active,
    string FolderPath,
    IReadOnlyList<InstalledQuestView> Quests,
    // Мир, которому кампания СЕБЯ приписывает. Нужен списку, чтобы показать
    // оранжевое предупреждение, если папку перенесли в чужой мир: без него
    // квесты кампании выглядят пропавшими, а причина не видна вовсе.
    string? ParentWorldId = null,
    // Полное имя и описание: показываются в списке и в окне свойств.
    string? FullName = null,
    string? Description = null,
    // Подпись автора и дат: показывается курсивом в списке кампаний.
    ResourceMetadata? Metadata = null);

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
    private readonly string _root;
    private readonly bool _readOnly;
    // Автор хранится в сторе, а не передаётся в каждый метод: подпись проставляется
    // ВСЕМИ правками кампании, и параметр у каждого вызова рано или поздно забыли бы.
    private readonly string? _author;

    public CampaignStore()
        : this(AppPaths.UserQuestRoot, readOnly: false)
    {
    }

    public CampaignStore(string root, bool readOnly, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Campaign root не задан.", nameof(root));

        _root = Path.GetFullPath(root);
        _readOnly = readOnly;
        _author = author;
        Reload();
    }

    /// <summary>
    /// Кампании ТОЛЬКО выбранного мира.
    ///
    /// Мир — это проект, и его содержимое не должно смешиваться с чужим: два
    /// мира могут иметь кампанию с одним и тем же id (например Common), и без
    /// ограничения дерева они бы накладывались друг на друга. Отбор идёт по
    /// пути: кампания принадлежит миру, если лежит внутри его папки.
    ///
    /// <paramref name="worldFolder"/> — пустая строка или null означает «весь
    /// корень»: так работает режим без выбранного мира (CI-прогон, диагностика).
    /// </summary>
    public CampaignStore ScopedTo(string? worldFolder)
    {
        if (string.IsNullOrWhiteSpace(worldFolder))
            return this;

        var scoped = new CampaignStore(worldFolder, _readOnly, _author);
        AppLogger.Info("CampaignStore: каталог ограничен миром.",
            $"world={worldFolder}; campaigns={scoped.Records.Count}");
        return scoped;
    }

    /// <summary>
    /// Имя мира, которому принадлежит каталог.
    ///
    /// Берётся из имени папки, а не из файла мира: стор работает со своей
    /// папкой и не должен читать файлы соседнего уровня. Пусто для всего корня.
    /// </summary>
    public string WorldFolderName =>
        _root.Equals(Path.GetFullPath(AppPaths.UserQuestRoot), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.GetFileName(_root);

    public bool IsReadOnly => _readOnly;

    /// <summary>
    /// Папка, в которой лежат кампании этого стора.
    ///
    /// Стор строится либо на корне кампаний, либо на папке МИРА (`ScopedTo`).
    /// Во втором случае кампании обязаны лежать в подпапке `campaigns`: иначе
    /// созданная кампания оказалась бы рядом с `world.aqworld`, разошлась бы с
    /// раскладкой обмена (архив мира собирает папку `campaigns`) и не попала бы
    /// в архив при выгрузке мира.
    ///
    /// Признак папки мира — файл `world.aqworld`, а не имя папки: имя произвольное
    /// (его задаёт автор), а файл мира есть ровно у папки мира и больше нигде.
    /// </summary>
    private string CampaignsContainer =>
        File.Exists(WorldPaths.WorldFilePath(_root))
            ? WorldPaths.CampaignsRoot(_root)
            : _root;

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
            foreach (var entry in OrderQuests(record.Definition.Quests))
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
                        definition.Activation,
                        entry.Order));
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
                quests,
                record.Definition.WorldId,
                record.Definition.FullName,
                record.Definition.Description,
                record.Definition.Metadata));
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

    /// <summary>
    /// Активная кампания или первая доступная.
    ///
    /// Нужна всем действиям с миром (гео, стартовые условия, время): у песочницы
    /// один активный мир, и определять его должен один метод, а не каждый вызов
    /// по-своему.
    /// </summary>
    public CampaignRecord? ActiveRecord() =>
        Records.FirstOrDefault(record => record.Definition.Active)
        ?? Records.FirstOrDefault();

    /// <summary>
    /// Записывает стартовые условия мира в кампанию.
    ///
    /// Это часть ФАЙЛА кампании, а не симуляции: сохраняются геокоордината (для
    /// астрономии), дата старта мира и погода с видимостью. Действие осознанное,
    /// поэтому пользователь вызывает его кнопкой «Сохранить в кампании».
    /// </summary>
    public void SaveWorldSettings(string campaignId, CampaignDefinition world)
    {
        var record = GetRecord(campaignId);
        record.Replace(record.Definition with
        {
            Geo = world.Geo,
            StartDate = world.StartDate,
            StartConditions = world.StartConditions
        });
        Save(record);

        AppLogger.Info("CampaignStore: стартовые условия мира сохранены.",
            $"campaignId={campaignId}; geo={world.Geo}; startDate={world.StartDate:O}; " +
            $"weather={world.StartConditions?.Weather}");
    }

    /// <summary>
    /// Обновляет свойства кампании из окна свойств.
    ///
    /// Затрагивается ТОЛЬКО описательная часть (имя, описание, изображение).
    /// Квесты, активность, геокоордината и стартовые условия живут в этом же
    /// файле и обязаны остаться нетронутыми: иначе правка описания молча
    /// изменила бы игровые настройки мира.
    ///
    /// <paramref name="parentWorldId"/> не меняется никогда: переносить кампанию
    /// в другой мир через окно свойств нельзя — это файловая операция, и
    /// «редактирование» здесь лишь переписало бы поле, оставив папку на месте.
    /// </summary>
    public void UpdateCampaign(
        string campaignId,
        string name,
        string? fullName,
        string? description,
        string? imageFileName)
    {
        var record = GetRecord(campaignId);

        if (!ResourceNaming.IsValidName(name))
            throw new InvalidOperationException(
                "Недопустимое имя кампании: используйте буквы, цифры, пробел и подчёркивание.");

        var moment = DateTimeOffset.UtcNow;
        record.Replace(record.Definition with
        {
            Name = name.Trim(),
            FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            ImageFile = string.IsNullOrWhiteSpace(imageFileName) ? null : imageFileName,
            Metadata = (record.Definition.Metadata ?? new ResourceMetadata())
                .WithModified(
                    string.IsNullOrWhiteSpace(_author)
                        ? ResourceMetadata.DefaultAuthor(moment)
                        : _author,
                    moment)
        });

        Save(record);
        AppLogger.Info("CampaignStore: свойства кампании обновлены.",
            $"campaignId={campaignId}; name={record.Definition.Name}; " +
            $"image={record.Definition.ImageFile}");
    }

    /// <summary>
    /// Регистрирует импортированный Quest в campaign.aqcampaign.
    ///
    /// Один файл в папке quests недостаточен: каталог строится из Quests в
    /// определении кампании. Метод обновляет и файл, и метаданные кампании,
    /// поэтому импортированный ресурс появляется без перезапуска.
    /// </summary>
    public void RegisterQuest(
        CampaignRecord record,
        QuestDefinition quest,
        string relativePath,
        CampaignQuestStatus status = CampaignQuestStatus.Enabled)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(quest);

        if (_readOnly)
            throw new InvalidOperationException("Каталог кампаний открыт только для чтения.");

        if (string.IsNullOrWhiteSpace(quest.Id))
            throw new InvalidOperationException("Импортируемый Quest не имеет Id.");

        var existing = record.Definition.Quests.FirstOrDefault(item =>
            item.QuestId.Equals(quest.Id, StringComparison.OrdinalIgnoreCase));

        var next = existing is null
            ? record.Definition.Quests.Append(new CampaignQuestEntry(
                quest.Id,
                relativePath,
                quest.Version,
                status,
                NextQuestOrder(record.Definition.Quests)))
                .ToArray()
            : record.Definition.Quests
                .Select(item => item.QuestId.Equals(quest.Id, StringComparison.OrdinalIgnoreCase)
                    ? item with
                    {
                        RelativePath = relativePath,
                        Version = quest.Version,
                        Status = status
                    }
                    : item)
                .ToArray();

        record.Replace(record.Definition with
        {
            Quests = next,
            Metadata = (record.Definition.Metadata ?? new ResourceMetadata())
                .WithModified(
                    string.IsNullOrWhiteSpace(_author)
                        ? ResourceMetadata.DefaultAuthor(DateTimeOffset.UtcNow)
                        : _author,
                    DateTimeOffset.UtcNow)
        });
        Save(record);

        AppLogger.Info("CampaignStore: импортированный Quest зарегистрирован.",
            $"campaignId={record.Definition.Id}; questId={quest.Id}; relativePath={relativePath}; status={status}");
    }

    private static int NextQuestOrder(IReadOnlyList<CampaignQuestEntry> entries)
    {
        var max = entries
            .Select(item => item.Order)
            .Where(order => order > 0)
            .DefaultIfEmpty(0)
            .Max();

        return max + 1;
    }

    public void SetQuestEnabled(string campaignId, string questId, bool enabled)
    {        var record = GetRecord(campaignId);
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

    /// <summary>
    /// Создаёт кампанию в папке своего мира.
    ///
    /// Кампания создаётся СРАЗУ ФАЙЛОМ, а не «пустой заготовкой»: каталог
    /// читается с диска, и кампания без файла просто не существовала бы — автор
    /// увидел бы, что кнопка ничего не сделала.
    ///
    /// Демонстрационный квест НЕ создаётся: в отличие от общей кампании мира
    /// (её отсутствие означает мир, в котором нечего создавать), новая кампания
    /// заводится автором осознанно, и пустой список квестов — нормальное начало.
    /// </summary>
    public CampaignRecord CreateCampaign(
        string worldId,
        string name,
        string? fullName = null,
        string? description = null)
    {
        if (_readOnly)
            throw new InvalidOperationException("Каталог кампаний открыт только для чтения.");

        if (!ResourceNaming.IsValidName(name))
            throw new InvalidOperationException(
                "Недопустимое имя кампании: используйте буквы, цифры, пробел и подчёркивание.");

        var folderName = ResourceNaming.ToFolderName(name);
        var container = CampaignsContainer;
        var folder = Path.Combine(container, folderName);

        // Проверка занятости имени идёт ПО КАТАЛОГУ, а не только по файловой
        // системе: на регистронезависимой ФС «Проверка» и «проверка» — одна
        // папка, а на регистрозависимой — разные, и поведение расходилось бы
        // между машинами. Стор уже знает свои записи, поэтому проверяет их.
        var clash = _records.Values.FirstOrDefault(record =>
            record.Definition.Id.Equals(folderName.Replace(' ', '_').ToLowerInvariant(),
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(record.FolderPath).Equals(folderName,
                StringComparison.OrdinalIgnoreCase));

        if (clash is not null || Directory.Exists(folder))
        {
            throw new InvalidOperationException(
                "Кампания с таким именем уже существует: " + name);
        }

        var moment = DateTimeOffset.UtcNow;
        var author = string.IsNullOrWhiteSpace(_author)
            ? ResourceMetadata.DefaultAuthor(moment)
            : _author;

        var definition = new CampaignDefinition(
            Id: folderName.Replace(' ', '_').ToLowerInvariant(),
            Name: name.Trim(),
            Version: 1,
            // Новая кампания НЕ активна: активная кампания задаёт мир симуляции,
            // и «создал кампанию — сменил мир» было бы неожиданным побочным
            // эффектом создания.
            Active: false,
            Quests: Array.Empty<CampaignQuestEntry>(),
            Files: Array.Empty<string>(),
            Geo: GeoCoordinate.CreateDefault(),
            StartDate: GameCalendar.DefaultStartDate,
            WorldId: worldId,
            FullName: string.IsNullOrWhiteSpace(fullName) ? null : fullName.Trim(),
            Description: string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Metadata: new ResourceMetadata().WithCreated(author, moment));

        // Структура папок создаётся сразу: автор должен видеть, куда класть
        // квесты и сцены, а не создавать каталоги руками.
        foreach (var sub in new[]
                 {
                     WorldPaths.QuestsFolderPath(folder),
                     WorldPaths.ScenesFolderPath(folder)
                 })
        {
            Directory.CreateDirectory(sub);
        }

        WorldContentSeeder.WriteCampaign(folder, definition, worldId);
        Reload();

        var record = GetRecord(definition.Id)
            ?? throw new InvalidOperationException(
                "Кампания создана, но не читается: " + WorldPaths.CampaignFilePath(folder));

        AppLogger.Info("CampaignStore: кампания создана.",
            $"worldId={worldId}; campaignId={definition.Id}; folder={folder}");

        return record;
    }

    public void Reload()
    {
        _records.Clear();

        // Сканируется тот же контейнер, куда пишет создание кампании. Иначе
        // созданная кампания попала бы на диск, но не в каталог: «создал, а её
        // нигде нет» — тот самый симптом, который эта проверка и ловит.
        var container = CampaignsContainer;
        if (!_readOnly)
            Directory.CreateDirectory(container);

        if (!Directory.Exists(container))
        {
            AppLogger.Info("CampaignStore: каталог отсутствует.",
                $"root={_root}; container={container}; readOnly={_readOnly}");
            return;
        }

        foreach (var campaignFile in Directory.EnumerateFiles(
                     container,
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

        AppLogger.Info("CampaignStore: каталог загружен.", $"campaigns={_records.Count}; root={_root}; readOnly={_readOnly}");
    }

    private CampaignRecord GetRecord(string campaignId)
    {
        if (!_records.TryGetValue(campaignId, out var record))
            throw new InvalidOperationException("Кампания не найдена: " + campaignId);

        return record;
    }

    /// <summary>
    /// Канонический порядок квестов внутри кампании.
    ///
    /// Приоритет — явный <see cref="CampaignQuestEntry.Order"/> из файла
    /// кампании. Если номера совпадают (или не заданы), решает имя файла:
    /// иначе порядок зависел бы от порядка строк в JSON.
    /// </summary>
    private static IEnumerable<CampaignQuestEntry> OrderQuests(
        IReadOnlyList<CampaignQuestEntry> quests) =>
        quests
            .OrderBy(entry => entry.Order <= 0 ? int.MaxValue : entry.Order)
            .ThenBy(entry => Path.GetFileName(entry.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.QuestId, StringComparer.OrdinalIgnoreCase);

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

    private void Save(CampaignRecord record)
    {
        if (_readOnly)
        {
            AppLogger.Info("CampaignStore: изменение только в памяти (read-only).",
                $"campaignId={record.Definition.Id}");
            return;
        }

        Directory.CreateDirectory(record.FolderPath);
        var document = new CampaignDefinitionDocument(1, "aqcampaign", record.Definition);
        File.WriteAllText(
            record.CampaignFilePath,
            ResourceJsonFormat.Serialize(document));
    }
}
