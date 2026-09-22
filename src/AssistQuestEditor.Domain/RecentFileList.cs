namespace AssistQuestEditor.Domain;

/// <summary>
/// Список недавно открытых ресурсов (MRU).
///
/// Существует ради непрерывности работы: пользователь возвращается к тем же
/// квестам и сценам, и путь к ним должен восстанавливаться вместе с проектом.
///
/// Контракт:
///   - свежий путь встаёт в начало, дубликат не размножается, а всплывает наверх;
///   - размер ограничен <see cref="Capacity"/> (по умолчанию 10);
///   - сравнение путей регистронезависимое: Windows не различает регистр;
///   - пустые и невалидные пути молча игнорируются, чтобы список не портился
///     из-за мусора в настройках.
///
/// Нормализация пути — только <see cref="Path.GetFullPath"/> (без обращения к
/// диску). Существование файлов проверяет вызывающая сторона через
/// <see cref="PruneMissing"/>: Domain не должен зависеть от файловой системы.
/// </summary>
public sealed class RecentFileList
{
    public const int DefaultCapacity = 10;

    private readonly List<string> _items = new();

    public RecentFileList(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ёмкость списка должна быть положительной.");

        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Пути от самого свежего к самому старому.</summary>
    public IReadOnlyList<string> Items => _items;

    public int Count => _items.Count;

    public event EventHandler? Changed;

    /// <summary>
    /// Создаёт список из сохранённых путей, сохраняя их порядок и отбрасывая
    /// дубликаты с мусором.
    /// </summary>
    public static RecentFileList FromPaths(IEnumerable<string>? paths, int capacity = DefaultCapacity)
    {
        var list = new RecentFileList(capacity);
        if (paths is null)
            return list;

        foreach (var path in paths)
        {
            // Сохранённый список уже упорядочен (первый элемент — самый свежий),
            // поэтому InsertSilently дописывает в конец и порядок сохраняется.
            list.InsertSilently(path);
        }

        // Файл настроек мог быть исправлен вручную: лишние записи обрезаем,
        // иначе список вернулся бы другого размера, чем допускает редактор.
        list.TrimToCapacity();

        return list;
    }

    /// <summary>
    /// Добавляет путь в начало списка.
    /// Возвращает <c>true</c>, если порядок или состав списка изменились.
    /// </summary>
    public bool Touch(string? path)
    {
        if (!TryNormalize(path, out var normalized))
            return false;

        var existingIndex = IndexOf(normalized);
        if (existingIndex == 0)
            return false;

        if (existingIndex > 0)
            _items.RemoveAt(existingIndex);

        _items.Insert(0, normalized);
        TrimToCapacity();

        RaiseChanged();
        return true;
    }

    public bool Remove(string? path)
    {
        if (!TryNormalize(path, out var normalized))
            return false;

        var index = IndexOf(normalized);
        if (index < 0)
            return false;

        _items.RemoveAt(index);
        RaiseChanged();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0)
            return;

        _items.Clear();
        RaiseChanged();
    }

    /// <summary>
    /// Убирает записи, для которых <paramref name="exists"/> вернуло <c>false</c>.
    /// Предикат передаётся снаружи: так Domain остаётся независимым от диска, а
    /// вызов проверяем тестом без реальных файлов.
    /// </summary>
    public bool PruneMissing(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var removed = _items.RemoveAll(path => !exists(path));
        if (removed == 0)
            return false;

        RaiseChanged();
        return true;
    }

    private void InsertSilently(string? path)
    {
        if (!TryNormalize(path, out var normalized))
            return;

        var existingIndex = IndexOf(normalized);
        if (existingIndex >= 0)
            _items.RemoveAt(existingIndex);

        _items.Add(normalized);
    }

    private int IndexOf(string normalized) =>
        _items.FindIndex(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase));

    private void TrimToCapacity()
    {
        if (_items.Count > Capacity)
            _items.RemoveRange(Capacity, _items.Count - Capacity);
    }

    private static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            normalized = Path.GetFullPath(path.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void RaiseChanged() =>
        Changed?.Invoke(this, EventArgs.Empty);
}
