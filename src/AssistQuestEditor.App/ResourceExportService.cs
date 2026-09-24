using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>Что выгружено и куда. Интерфейсу нужны обе величины: путь показывается, размер — для доверия.</summary>
public sealed record ResourceExportResult(
    string Path,
    bool IsArchive,
    long Bytes,
    int FileCount)
{
    /// <summary>Человекочитаемое «куда»: папка или файл — это разные вещи, и путать их нельзя.</summary>
    public string DisplayPath => Path;
}

/// <summary>
/// Выгрузка миров, кампаний и квестов наружу: папкой с файлами либо архивом `.aqezip`.
///
/// Два режима — не украшение. Папкой удобно то, что видно глазами и кладётся в
/// git; архивом — то, что пересылается одним файлом и не теряет структуру.
/// Поэтому выбор режима вынесен в галочку, а не решён за пользователя.
///
/// Общий знаменатель обоих режимов — <see cref="WorldArchiveService"/>: упаковка
/// архива и раскладка папки обязаны давать ОДНО содержимое, иначе «выгрузил
/// папкой, передал, собрал архив» и «выгрузил архивом» разошлись бы, и разбирать
/// это пришлось бы получателю.
/// </summary>
public static class ResourceExportService
{
    /// <summary>
    /// Выгружает папку ресурса в `Документы\...\Exported\&lt;метка времени&gt;\&lt;имя&gt;`.
    ///
    /// Метка времени в пути, а не один общий каталог: две выгрузки подряд
    /// (например «до» и «после» правки) не должны перемешаться — сравнивать их
    /// иначе невозможно.
    /// </summary>
    public static ResourceExportResult ExportFolder(
        string userRoot,
        string sourceFolder,
        string resourceFolderName,
        DateTimeOffset moment)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException("Нет папки для выгрузки: " + sourceFolder);

        var root = WorldPaths.ExportFolder(userRoot, moment);
        Directory.CreateDirectory(root);

        var target = Path.Combine(root, SafeLeaf(resourceFolderName));
        // Проверка ДО нумерации: номер «(2)» к «../побег» ничего не исправляет,
        // он бы лишь отодвинул выход за корень на один шаг.
        EnsureInside(root, target);

        if (PathTaken(target))
        {
            target = Path.Combine(root, WorldArchiveImportRules.UniqueFolderName(
                SafeLeaf(resourceFolderName),
                name => PathTaken(Path.Combine(root, name))));
            EnsureInside(root, target);
        }

        CopyTree(sourceFolder, target);

        var files = Directory
            .EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToArray();

        return new ResourceExportResult(
            target,
            IsArchive: false,
            Bytes: files.Sum(file => new FileInfo(file).Length),
            FileCount: files.Length);
    }

    /// <summary>
    /// Выгружает ресурс архивом `.aqezip`.
    ///
    /// Имя файла строится из имени ресурса, а не берётся от исходной папки: папка
    /// мира уже пронумерована при импорте («Демо Мир (2)»), и тащить эту
    /// случайность в имя файла значило бы пересылать её дальше как имя ресурса.
    /// </summary>
    public static ResourceExportResult ExportArchive(
        string userRoot,
        string sourceFolder,
        string resourceFileName,
        WorldArchiveManifest manifest,
        DateTimeOffset moment)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException("Нет папки для упаковки: " + sourceFolder);

        var root = WorldPaths.ExportFolder(userRoot, moment);
        Directory.CreateDirectory(root);

        var leaf = SafeLeaf(resourceFileName);

        // Расширение гарантируется здесь, а не вызывающим кодом. Архив без
        // .aqezip система не связывает с приложением, и двойной клик по нему не
        // откроет диалог импорта — то есть файл перестаёт быть переносимым, а
        // заметно это только у получателя. Заодно это разводит имена: папка
        // «Мир» и архив «Мир.aqezip» не конфликтуют.
        if (!leaf.EndsWith(WorldArchiveRules.Extension, StringComparison.OrdinalIgnoreCase))
            leaf += WorldArchiveRules.Extension;

        EnsureInside(root, Path.Combine(root, leaf));

        var target = WorldArchiveImportRules.UniqueFolderName(
            leaf,
            name => PathTaken(Path.Combine(root, name)));
        var archivePath = Path.Combine(root, target);
        EnsureInside(root, archivePath);

        // Зависимости включаются ВСЕГДА для архива. Причина в назначении архива:
        // его пересылают одним файлом, и «квест без своих сцен» — это не
        // усечённый ресурс, а неработающий.
        var packed = WorldArchiveService.Pack(
            sourceFolder,
            archivePath,
            manifest,
            includeDependencies: true);

        return new ResourceExportResult(
            packed.Path,
            IsArchive: true,
            Bytes: packed.ArchiveBytes,
            FileCount: packed.FileCount);
    }

    /// <summary>
    /// Собирает манифест мира по фактически лежащему определению.
    ///
    /// Манифест берётся из файла, а не из параметров окна: архив обязан описывать
    /// то, что в нём лежит. Иначе исправленное имя мира попало бы в манифест, но
    /// не в файл — и получатель увидел бы старое имя рядом с новым.
    /// </summary>
    public static WorldArchiveManifest ManifestForWorld(WorldRecord world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var definition = world.Definition;

        return new WorldArchiveManifest(
            Kind: WorldArchiveKinds.World,
            Id: definition.Id,
            Name: definition.Name,
            FullName: definition.FullName,
            Description: definition.Description,
            Version: WorldArchiveRules.SupportedVersion,
            IncludesDependencies: true,
            Entries: Array.Empty<WorldArchiveEntry>(),
            ParentWorldId: null,
            ParentCampaignId: null,
            Metadata: definition.Metadata);
    }

    /// <summary>
    /// Собирает манифест кампании.
    ///
    /// <paramref name="parentWorldId"/> обязателен: кампания без родителя при
    /// импорте некуда класть, и диалог импорта показал бы «родитель неизвестен».
    /// </summary>
    public static WorldArchiveManifest ManifestForCampaign(
        CampaignStore.CampaignRecord campaign,
        string parentWorldId,
        ResourceMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        return new WorldArchiveManifest(
            Kind: WorldArchiveKinds.Campaign,
            Id: campaign.Definition.Id,
            Name: campaign.Definition.Name,
            FullName: campaign.Definition.FullName,
            Description: campaign.Definition.Description,
            Version: campaign.Definition.Version,
            IncludesDependencies: true,
            Entries: Array.Empty<WorldArchiveEntry>(),
            ParentWorldId: parentWorldId,
            ParentCampaignId: null,
            Metadata: metadata);
    }

    /// <summary>
    /// Занято ли имя — файлом ИЛИ каталогом.
    ///
    /// Отдельная проверка на оба вида обязательна. Выгрузка папкой создаёт
    /// каталог, выгрузка архивом — файл, и они называются одинаково. Проверка
    /// только на файл (или только на каталог) пропускала бы «занятое» имя:
    /// архив пытался бы открыть существующий каталог как файл и падал с
    /// «Access to the path ... is denied» — то есть выгрузка ломалась бы при
    /// повторении её в том же режиме и в ту же секунду, ровно там, где
    /// нумерация «(2)» и должна была спасти.
    /// </summary>
    private static bool PathTaken(string path) =>
        File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Копирует дерево папок.
    ///
    /// Пустые каталоги копируются тоже: в мире они несут смысл (куда класть
    /// сцены и сохранения), и «выгрузил папкой, а структуры нет» — это поломка,
    /// видная получателю только после начала работы.
    /// </summary>
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var folder in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, folder);
            if (!WorldArchiveRules.IsSafeEntryPath(relative.Replace('\\', '/') + "/"))
                throw new InvalidOperationException("Путь каталога небезопасен: " + relative);

            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (!WorldArchiveRules.IsSafeEntryPath(relative.Replace('\\', '/')))
                throw new InvalidOperationException("Путь файла небезопасен: " + relative);

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Имя ресурса, пригодное для одного сегмента пути.
    ///
    /// Имя приходит из файла ресурса, то есть из данных, и при импорте может быть
    /// задано чужим автором в обход проверок, которые проходят при создании.
    /// «../побег» или «C:\чужое» увели бы выгрузку за пределы каталога экспорта,
    /// поэтому такие имена ОТВЕРГАЮТСЯ, а не «исправляются»: молчаливая замена
    /// записала бы файлы не туда, куда просили, и разбираться пришлось бы по
    /// содержимому диска.
    ///
    /// Прочие недопустимые в именах файлов символы заменяются так же, как при
    /// создании ресурса (<see cref="ResourceNaming.ToFolderName"/>): «Демо Мир:
    /// глава 1» — обычное имя, и отказывать в выгрузке из-за двоеточия нельзя.
    /// </summary>
    private static string SafeLeaf(string name)
    {
        var leaf = (name ?? string.Empty).Trim();

        if (leaf.Length == 0)
            throw new InvalidOperationException("Пустое имя ресурса для выгрузки.");

        // Разделители и точка-как-сегмент — единственное, что меняет адрес
        // записи. Двоеточие ловит «C:\чужое» и потоки NTFS.
        if (leaf is "." or ".." || leaf.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            throw new InvalidOperationException(
                "Недопустимое имя ресурса для выгрузки: «" + name + "». " +
                "Имя не может содержать разделители пути или быть точкой.");
        }

        var sanitized = ResourceNaming.ToFolderName(leaf);
        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            throw new InvalidOperationException(
                "Недопустимое имя ресурса для выгрузки: «" + name + "».");
        }

        return sanitized;
    }

    /// <summary>
    /// Убеждается, что путь остался внутри каталога выгрузки.
    ///
    /// Вторая линия защиты после <see cref="SafeLeaf"/>, как и при распаковке
    /// архива: имя может быть безопасным по отдельности, но собранный из него
    /// путь — нет. Проверяется ФАКТИЧЕСКИЙ путь, а не намерение.
    /// </summary>
    private static void EnsureInside(string root, string target)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedTarget = Path.GetFullPath(target);
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!normalizedTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Выгрузка вышла за пределы каталога экспорта: " + target);
        }
    }
}
