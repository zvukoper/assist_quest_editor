using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Пользовательская библиотека Location Resources.
///
/// Локации живут отдельно от bundled data и от конкретной Campaign: одна
/// авторская Location может переиспользоваться множеством квестов.
/// </summary>
public sealed class LocationStore
{
    public const string Extension = ".aqlocation";
    private const int SupportedSchemaVersion = 1;

    private readonly string _root;
    private readonly bool _readOnly;
    private readonly Dictionary<string, (LocationDefinition Definition, string Path)> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public LocationStore(string root, bool readOnly = false)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Каталог Location не задан.", nameof(root));

        _root = Path.GetFullPath(root);
        _readOnly = readOnly;
        Reload();
    }

    public bool IsReadOnly => _readOnly;

    public IReadOnlyList<LocationDefinition> Definitions =>
        _items.Values
            .Select(item => item.Definition)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void Reload()
    {
        _items.Clear();
        if (!_readOnly)
            Directory.CreateDirectory(_root);

        if (!Directory.Exists(_root))
            return;

        foreach (var path in Directory.EnumerateFiles(
                     _root,
                     "*" + Extension,
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = ResourceJsonFormat.Deserialize<LocationDefinitionDocument>(
                    File.ReadAllText(path));

                if (document?.Definition is null ||
                    document.SchemaVersion != SupportedSchemaVersion ||
                    !document.Format.Equals("aqlocation", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(document.Definition.Id))
                {
                    AppLogger.Warn("LocationStore: пропущен некорректный Location.", path);
                    continue;
                }

                if (!_items.ContainsKey(document.Definition.Id))
                    _items.Add(document.Definition.Id, (document.Definition, path));
            }
            catch (Exception ex)
            {
                AppLogger.Error("LocationStore: ошибка чтения Location.", ex, path);
            }
        }

        AppLogger.Info(
            "LocationStore: библиотека загружена.",
            $"root={_root}; count={_items.Count}; readOnly={_readOnly}");
    }

    public bool TryGet(string id, out LocationDefinition definition)
    {
        if (_items.TryGetValue(id, out var item))
        {
            definition = item.Definition;
            return true;
        }

        definition = null!;
        return false;
    }

    public string? GetPath(string id) =>
        _items.TryGetValue(id, out var item) ? item.Path : null;

    public string Save(LocationDefinition definition, string? existingPath = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Validate(definition);

        if (_readOnly)
            throw new InvalidOperationException("Location Library доступна только для чтения.");

        Directory.CreateDirectory(_root);

        var path = string.IsNullOrWhiteSpace(existingPath)
            ? Path.Combine(_root, SanitizeFileName(definition.Id) + Extension)
            : Path.GetFullPath(existingPath);

        EnsureInsideRoot(path);

        if (_items.TryGetValue(definition.Id, out var existing) &&
            !string.Equals(Path.GetFullPath(existing.Path), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Location с ID «" + definition.Id + "» уже существует в другом файле.");
        }

        foreach (var stale in _items
                     .Where(item => string.Equals(
                         Path.GetFullPath(item.Value.Path),
                         path,
                         StringComparison.OrdinalIgnoreCase) &&
                         !item.Key.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.Key)
                     .ToArray())
        {
            _items.Remove(stale);
        }

        var document = new LocationDefinitionDocument(
            SupportedSchemaVersion,
            "aqlocation",
            definition);

        File.WriteAllText(path, ResourceJsonFormat.Serialize(document));
        _items[definition.Id] = (definition, path);

        return path;
    }

    public bool Delete(string id)
    {
        if (_readOnly)
            throw new InvalidOperationException("Location Library доступна только для чтения.");

        if (!_items.TryGetValue(id, out var item))
            return false;

        if (File.Exists(item.Path))
            File.Delete(item.Path);

        _items.Remove(id);
        AppLogger.Info("LocationStore: удалена Location.", $"id={id}; path={item.Path}");
        return true;
    }

    private void Validate(LocationDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new InvalidOperationException("Location ID не может быть пустым.");

        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new InvalidOperationException("Название Location не может быть пустым.");

        if (definition.TriggerRadius < 0 || !double.IsFinite(definition.TriggerRadius))
            throw new InvalidOperationException("Радиус Location должен быть конечным неотрицательным числом.");

        if (definition.Mode == LocationMode.Fixed &&
            string.IsNullOrWhiteSpace(definition.WorldPointId))
            throw new InvalidOperationException("Для Fixed Location нужен WorldPoint.");

        if (definition.Mode == LocationMode.Dynamic &&
            definition.Query is null)
            throw new InvalidOperationException("Для Dynamic Location нужен Location Query.");
    }

    private void EnsureInsideRoot(string path)
    {
        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь Location выходит за пределы библиотеки.");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim();

        return string.IsNullOrWhiteSpace(result) ? "location" : result;
    }
}
