using System.IO.Compression;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>Что получилось при упаковке. Проценты нужны интерфейсу, а не логу.</summary>
public sealed record ArchivePackResult(
    string Path,
    long SourceBytes,
    long ArchiveBytes,
    int FileCount)
{
    public int CompressionPercent =>
        WorldArchiveRules.CompressionPercent(SourceBytes, ArchiveBytes);
}

/// <summary>Что найдено в архиве до распаковки: то, что показывает диалог импорта.</summary>
public sealed record ArchiveInspection(
    WorldArchiveManifest Manifest,
    string ArchivePath,
    long ArchiveBytes,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<WorldArchiveEntry> Entries);

/// <summary>
/// Упаковка и распаковка архивов `.aqezip`.
///
/// Один класс на оба направления: формат — это контракт, и упаковщик, живущий
/// отдельно от распаковщика, рано или поздно начнёт писать то, чего второй не
/// понимает. Общий манифест (<see cref="WorldArchiveManifest"/>) читается здесь
/// же, из него же строится и запись.
///
/// Сжатие максимальное: единственная причина существования архива — пересылка
/// одним файлом, и экономить время упаковки за счёт размера здесь бессмысленно.
///
/// Записи получают ФИКСИРОВАННУЮ метку времени. Причина не косметическая: ZIP
/// по умолчанию штампует каждый элемент текущим временем, поэтому один и тот же
/// контент давал бы разные файлы. Тогда «архив в поставке устарел» нельзя было
/// бы проверить никак — приходилось бы сверять содержимое вручную, а
/// расхождение кода и выгруженного файла (именно тот класс дефекта, которым
/// болеет этот проект) оставалось бы незамеченным.
/// </summary>
public static class WorldArchiveService
{
    /// <summary>
    /// Метка времени всех записей архива.
    ///
    /// 1980-01-01 00:00 — минимальное значение, допустимое форматом ZIP (он не
    /// умеет хранить даты раньше 1980 года). Это не «дата создания»: настоящие
    /// даты живут в манифесте, где их видит пользователь.
    /// </summary>
    private static readonly DateTimeOffset EntryTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Упаковывает папку ресурса в архив.
    ///
    /// <paramref name="includeDependencies"/> решает, попадут ли внутрь
    /// зависимые ресурсы (сцены квеста, кампании мира). Без этого флага архив
    /// самодостаточен только для одиночного ресурса, и диалог импорта обязан
    /// об этом сказать — иначе «импортировал квест, а сцен нет».
    /// </summary>
    public static ArchivePackResult Pack(
        string sourceFolder,
        string archivePath,
        WorldArchiveManifest manifest,
        bool includeDependencies)
    {
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException("Нет папки для упаковки: " + sourceFolder);

        var files = Directory
            .EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var directory = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var entries = new List<WorldArchiveEntry>();

        using (var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Пустые каталоги записываются отдельными записями. ZIP сам по себе
            // их не хранит — он знает только файлы, — поэтому без этого шага
            // «пустышка со структурой папок» распаковывалась бы в один файл мира:
            // автор не увидел бы даже, куда класть сцены и сохранения.
            // Запись каталога — это имя с завершающим слэшем и нулевой длиной.
            foreach (var folder in Directory
                         .EnumerateDirectories(sourceFolder, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativeFolder = Path.GetRelativePath(sourceFolder, folder).Replace('\\', '/') + "/";
                if (!WorldArchiveRules.IsSafeEntryPath(relativeFolder))
                    throw new InvalidOperationException(
                        "Путь каталога внутри архива небезопасен: " + relativeFolder);

                archive.CreateEntry(relativeFolder, CompressionLevel.NoCompression).LastWriteTime =
                    EntryTimestamp;
            }

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(sourceFolder, file).Replace('\\', '/');
                if (!WorldArchiveRules.IsSafeEntryPath(relative))
                {
                    // Упаковывать то, что нельзя распаковать, — гарантированная
                    // поломка архива у получателя. Лучше отказаться здесь.
                    throw new InvalidOperationException(
                        "Путь внутри архива небезопасен: " + relative);
                }

                var entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
                entry.LastWriteTime = EntryTimestamp;
                using var target = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(target);

                entries.Add(new WorldArchiveEntry(relative, new FileInfo(file).Length));
            }

            // Манифест пишется ПОСЛЕДНИМ: так в него попадает фактический состав.
            // Иначе пришлось бы считать состав дважды, и он мог разойтись.
            var document = new WorldArchiveManifestDocument(
                WorldArchiveRules.SupportedVersion,
                WorldArchiveRules.FormatName,
                manifest with
                {
                    IncludesDependencies = includeDependencies,
                    Entries = entries
                });

            var manifestEntry = archive.CreateEntry(
                WorldArchiveRules.ManifestFileName, CompressionLevel.SmallestSize);
            manifestEntry.LastWriteTime = EntryTimestamp;
            using var manifestStream = manifestEntry.Open();
            using var writer = new StreamWriter(manifestStream, new System.Text.UTF8Encoding(false));
            writer.Write(ResourceJsonFormat.Serialize(document));
        }

        var sourceBytes = entries.Sum(item => item.Bytes);
        var archiveBytes = new FileInfo(archivePath).Length;

        AppLogger.Info("WorldArchiveService: архив упакован.",
            $"source={sourceFolder}; archive={archivePath}; files={entries.Count}; " +
            $"sourceBytes={sourceBytes}; archiveBytes={archiveBytes}; deps={includeDependencies}");

        return new ArchivePackResult(archivePath, sourceBytes, archiveBytes, entries.Count);
    }

    /// <summary>
    /// Читает манифест и состав архива, НЕ распаковывая его.
    ///
    /// Диалог импорта показывает имя, автора, дату и состав ДО любых записей на
    /// диск: согласие на перезапись, данное до просмотра содержимого, — это
    /// согласие вслепую.
    /// </summary>
    public static ArchiveInspection Inspect(string archivePath)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Архив не найден.", archivePath);

        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry(WorldArchiveRules.ManifestFileName)
            ?? throw new InvalidDataException(
                "В архиве нет " + WorldArchiveRules.ManifestFileName +
                ": это не архив Assist Quest или он собран другой программой.");

        WorldArchiveManifestDocument? document;
        using (var manifestStream = manifestEntry.Open())
        using (var reader = new StreamReader(manifestStream))
        {
            document = ResourceJsonFormat.Deserialize<WorldArchiveManifestDocument>(reader.ReadToEnd());
        }

        if (document is null)
            throw new InvalidDataException("Манифест архива пуст или повреждён.");

        if (document.SchemaVersion != WorldArchiveRules.SupportedVersion)
            throw new InvalidDataException(
                $"Архив версии {document.SchemaVersion} не поддерживается (ожидается " +
                $"{WorldArchiveRules.SupportedVersion}).");

        if (!string.Equals(document.Format, WorldArchiveRules.FormatName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Файл не является архивом Assist Quest: формат «" + document.Format + "».");

        // Состав берётся из ФАКТИЧЕСКИХ записей архива, а не из манифеста:
        // манифест можно отредактировать, а распакуется всё равно то, что лежит
        // внутри. Показывать пользователю нужно второе.
        var entries = new List<WorldArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName, WorldArchiveRules.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Служебные записи каталогов (нулевой длины с завершающим слэшем)
            // показывать смысла нет: пользователь читает состав файлов.
            if (entry.FullName.EndsWith('/'))
                continue;

            if (!WorldArchiveRules.IsSafeEntryPath(entry.FullName))
            {
                // Опасный путь — это уже повод отказаться, а не «пропустить
                // строку в списке»: архив с таким путём распаковывать нельзя.
                throw new InvalidDataException(
                    "Архив содержит небезопасный путь: " + entry.FullName);
            }

            entries.Add(new WorldArchiveEntry(entry.FullName, entry.Length));
        }

        var archiveBytes = new FileInfo(archivePath).Length;
        AppLogger.Info("WorldArchiveService: архив прочитан.",
            $"path={archivePath}; kind={document.Definition.Kind}; id={document.Definition.Id}; " +
            $"files={entries.Count}; bytes={archiveBytes}");

        return new ArchiveInspection(
            document.Definition, archivePath, archiveBytes, entries.Count,
            entries.Sum(item => item.Bytes), entries);
    }

    /// <summary>
    /// Распаковывает архив в папку.
    ///
    /// Каждый путь проверяется перед записью повторно (см.
    /// <see cref="WorldArchiveRules.IsSafeEntryPath"/>): проверка при чтении
    /// манифеста защищает только от того, что мы успели прочитать. Распаковка
    /// идёт по списку записей, и доверять ему без проверки нельзя.
    ///
    /// Папка назначения создаётся, но НЕ очищается: очистку выполняет вызывающий
    /// код, зная, что именно он перезаписывает. Молчаливое удаление содержимого
    /// было бы худшим видом «помощи».
    /// </summary>
    public static void Unpack(string archivePath, string targetFolder)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Directory.CreateDirectory(targetFolder);
        var root = Path.GetFullPath(targetFolder);
        var written = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;

            var relative = entry.FullName.Replace('\\', '/');
            if (!WorldArchiveRules.IsSafeEntryPath(relative))
                throw new InvalidDataException("Архив содержит небезопасный путь: " + entry.FullName);

            var target = Path.GetFullPath(Path.Combine(root, relative));

            // Вторая линия защиты: результат склейки обязан остаться внутри
            // папки мира. Проверка строки не спасает от хитростей вида
            // «C:file» или имён с пробелами, которые Windows нормализует иначе.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Путь архива выходит за пределы папки назначения: " + entry.FullName);
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            entry.ExtractToFile(target, overwrite: true);
            written += 1;
        }

        AppLogger.Info("WorldArchiveService: архив распакован.",
            $"archive={archivePath}; target={targetFolder}; files={written}");
    }
}
