using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Одна строка списка сохранений: то, что нужно панели «Сохранения».
/// </summary>
public sealed record SimulationSaveListItem(
    string Path,
    string Name,
    long SizeBytes,
    string SizeLabel,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset GameDate,
    TimeSpan PlayedTime,
    string PlayedLabel,
    string CampaignId);

/// <summary>
/// Файлы сохранений симуляции.
///
/// Хранятся в пользовательском каталоге, а не в репозитории: это результат
/// прохождения, а не контент кампании. Формат — бинарный снимок из Domain
/// (<see cref="SimulationSaveCodec"/>), поэтому хранилище отвечает только за
/// файлы, имена и список, а не за структуру данных.
/// </summary>
public sealed class SimulationSaveStore
{
    private const string Extension = ".aqsave";

    private readonly string _root;

    public SimulationSaveStore()
        : this(AppPaths.SimulationSaveRoot)
    {
    }

    public SimulationSaveStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Каталог сохранений не задан.", nameof(root));

        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    /// <summary>
    /// Список сохранений, новые сверху.
    ///
    /// Читаются только заголовки: состояние не распаковывается. Иначе открытие
    /// панели распаковывало бы все снимки целиком, и при десятках сохранений это
    /// было бы заметно.
    /// </summary>
    public IReadOnlyList<SimulationSaveListItem> List()
    {
        var result = new List<SimulationSaveListItem>();
        if (!Directory.Exists(_root))
            return result;

        foreach (var path in Directory.EnumerateFiles(_root, "*" + Extension))
        {
            // Автосохранение прохождения не показывается среди снимков: у него
            // нет имени пользователя, и перезаписывается оно само.
            if (IsSessionPath(path))
                continue;

            try
            {
                var info = new FileInfo(path);
                var header = SimulationSaveCodec.ReadHeaderOnly(File.ReadAllBytes(path));

                result.Add(new SimulationSaveListItem(
                    path,
                    header.Name,
                    info.Length,
                    SimulationSaveNaming.FormatSize(info.Length),
                    header.CreatedAt,
                    header.UpdatedAt ?? info.LastWriteTimeUtc,
                    header.GameDate,
                    header.PlayedTime,
                    SimulationSaveNaming.FormatGameTime(header.PlayedTime),
                    header.CampaignId));
            }
            catch (Exception ex)
            {
                // Повреждённый файл не должен ломать список: остальные сохранения
                // остаются доступными, а причина попадает в лог.
                AppLogger.Warn("SimulationSaveStore: сохранение пропущено.", $"{path}: {ex.Message}");
            }
        }

        return result
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public long GetSize(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    public SimulationSave Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Сохранение не найдено.", path);

        return SimulationSaveCodec.Decode(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Создаёт новое сохранение.
    ///
    /// Имя по умолчанию — дата и время создания до секунды. Если файл с таким
    /// именем уже есть, добавляется числовой суффикс: перезапись существующего
    /// сохранения обязана быть ЯВНЫМ действием пользователя, а не побочным
    /// эффектом двух снимков в одну секунду.
    /// </summary>
    public SimulationSaveListItem Create(SimulationSave save, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(save);
        Directory.CreateDirectory(_root);

        var name = save.Header.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = SimulationSaveNaming.DefaultName(createdAt);

        var path = UniquePath(name);
        var header = save.Header with
        {
            Name = name,
            CreatedAt = createdAt,
            UpdatedAt = null
        };

        Write(path, new SimulationSave(header, save.State));
        return ToListItem(path, header);
    }

    /// <summary>
    /// Перезаписывает сохранение поверх существующего файла.
    ///
    /// Имя НЕ меняется (требование пользователя): иначе перезапись создавала бы
    /// новую запись вместо замены выбранной. Дата создания тоже сохраняется,
    /// обновляется только UpdatedAt.
    /// </summary>
    public SimulationSaveListItem Overwrite(string path, SimulationSave save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (!File.Exists(path))
            throw new FileNotFoundException("Сохранение не найдено.", path);

        var existing = SimulationSaveCodec.ReadHeaderOnly(File.ReadAllBytes(path));
        var header = save.Header with
        {
            Name = existing.Name,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Write(path, new SimulationSave(header, save.State));
        return ToListItem(path, header);
    }

    public void Delete(string path)
    {
        if (!File.Exists(path))
            return;

        File.Delete(path);
        AppLogger.Info("SimulationSaveStore: сохранение удалено.", "path=" + path);
    }

    /// <summary>
    /// Переименовывает сохранение, не трогая содержимое снимка.
    ///
    /// Меняется и имя файла, и заголовок внутри: имя показывается из заголовка,
    /// а имя файла нужно человеку в проводнике, и расхождение между ними путало бы.
    /// </summary>
    public SimulationSaveListItem Rename(string path, string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Имя сохранения не может быть пустым.");

        if (!File.Exists(path))
            throw new FileNotFoundException("Сохранение не найдено.", path);

        var save = SimulationSaveCodec.Decode(File.ReadAllBytes(path));
        var directory = Path.GetDirectoryName(path)!;
        var target = Path.Combine(directory, SimulationSaveNaming.ToFileName(trimmed) + Extension);

        // Переименование в себя (или в имя, отличающееся только недопустимыми
        // символами) не должно удалять файл.
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(target))
                throw new InvalidOperationException("Сохранение с таким именем уже существует.");

            File.Move(path, target);
        }

        var header = save.Header with { Name = trimmed, UpdatedAt = DateTimeOffset.UtcNow };
        Write(target, new SimulationSave(header, save.State));
        return ToListItem(target, header);
    }

    /// <summary>
    /// Файл текущего прохождения (автосохранение).
    ///
    /// Лежит в том же каталоге, что и снимки пользователя, но с фиксированным
    /// именем и не показывается в списке: он перезаписывается сам.
    /// </summary>
    public string SessionPath => Path.Combine(_root, "session" + Extension);

    public bool HasSession => File.Exists(SessionPath);

    /// <summary>Записывает автосохранение текущего прохождения.</summary>
    public void SaveSession(SimulationSave save) => Write(SessionPath, save);

    /// <summary>Читает автосохранение или null, если прохождения ещё не было.</summary>
    public SimulationSave? LoadSession() =>
        File.Exists(SessionPath)
            ? SimulationSaveCodec.Decode(File.ReadAllBytes(SessionPath))
            : null;

    /// <summary>
    /// Очищает состояние прохождения.
    ///
    /// Удаляется только автосохранение: именованные снимки — это осознанно
    /// созданные точки возврата, и «Сбросить» не должно их стирать.
    /// </summary>
    public void ClearSession()
    {
        if (!File.Exists(SessionPath))
            return;

        File.Delete(SessionPath);
        AppLogger.Info("SimulationSaveStore: прохождение сброшено.", "path=" + SessionPath);
    }

    /// <summary>
    /// Фильтрует список так, чтобы автосохранение не попало в панель.
    /// </summary>
    private bool IsSessionPath(string path) =>
        string.Equals(path, SessionPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Имя файла для снимка с заданным отображаемым именем.
    /// </summary>
    private string UniquePath(string name)
    {
        var baseName = SimulationSaveNaming.ToFileName(name);
        var candidate = Path.Combine(_root, baseName + Extension);
        var index = 2;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(_root, baseName + " (" + index + ")" + Extension);
            index++;
        }

        return candidate;
    }

    private void Write(string path, SimulationSave save)
    {
        // Каталог создаётся ПЕРЕД записью, а не предполагается существующим.
        //
        // Это была не теория: автосохранение падало с DirectoryNotFoundException
        // на `<Документы>\Assist Quest Editor\saves\session.aqsave`, потому что
        // каталог никто не создавал. Проявлялось это как «автосохранение не
        // работает» — молча, в логе, уже ПОСЛЕ того как автор поработал, и при
        // следующем запуске мир снова выглядел новым. Ошибка записи глушится
        // вызывающим кодом (симуляция не должна падать из-за диска), поэтому без
        // этой строки дефект не виден вообще.
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllBytes(path, SimulationSaveCodec.Encode(save));
    }

    private static SimulationSaveListItem ToListItem(string path, SimulationSaveHeader header)
    {
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;
        return new SimulationSaveListItem(
            path,
            header.Name,
            size,
            SimulationSaveNaming.FormatSize(size),
            header.CreatedAt,
            header.UpdatedAt ?? header.CreatedAt,
            header.GameDate,
            header.PlayedTime,
            SimulationSaveNaming.FormatGameTime(header.PlayedTime),
            header.CampaignId);
    }
}
