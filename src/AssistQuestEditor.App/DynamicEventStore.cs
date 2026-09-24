using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Пользовательская библиотека Dynamic Event Definitions.
///
/// Definition отвечает за правило появления, а конкретный экземпляр события
/// живёт только в Runtime State. Поэтому этот Store хранит именно authoring
/// resources, а не созданные точки/события.
/// </summary>
public sealed class DynamicEventStore
{
    public const string Extension = ".aqevent";
    private const int SupportedSchemaVersion = DynamicEventDefinitionRules.CurrentSchemaVersion;

    private readonly string _root;
    private readonly bool _readOnly;
    private IReadOnlyList<string> _additionalRoots = Array.Empty<string>();
    private readonly Dictionary<string, (DynamicEventDefinition Definition, string Path)> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public DynamicEventStore(
        string root,
        bool readOnly = false,
        IEnumerable<string>? additionalRoots = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Каталог Dynamic Events не задан.", nameof(root));

        _root = Path.GetFullPath(root);
        _readOnly = readOnly;
        SetAdditionalRoots(additionalRoots, reload: false);
        Reload();
    }

    /// <summary>
    /// Подключает ресурсы текущего мира к общей библиотеке.
    ///
    /// Global Event Library остаётся основной записываемой библиотекой, а
    /// world-local .aqevent файлы становятся её прозрачным overlay.
    /// </summary>
    public void SetAdditionalRoots(IEnumerable<string>? roots)
    {
        SetAdditionalRoots(roots, reload: true);
    }

    private void SetAdditionalRoots(IEnumerable<string>? roots, bool reload)
    {
        _additionalRoots = (roots ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => !path.Equals(_root, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (reload)
            Reload();
    }

    public bool IsReadOnly => _readOnly;

    public string Root => _root;

    public bool ContainsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            EnsureInsideAnyRoot(Path.GetFullPath(path));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public IReadOnlyList<DynamicEventDefinition> Definitions =>
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

        var roots = new[] { _root }.Concat(_additionalRoots)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "*" + Extension,
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var document = ResourceJsonFormat.Deserialize<DynamicEventDefinitionDocument>(
                        File.ReadAllText(path));

                    if (document?.Definition is null ||
                        document.SchemaVersion != SupportedSchemaVersion ||
                        !document.Format.Equals(DynamicEventDefinitionRules.FormatName, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(document.Definition.Id))
                    {
                        AppLogger.Warn("DynamicEventStore: пропущен некорректный Dynamic Event.", path);
                        continue;
                    }

                    // Primary library wins over a world overlay on id collision.
                    if (!_items.ContainsKey(document.Definition.Id))
                        _items.Add(document.Definition.Id, (document.Definition, path));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("DynamicEventStore: ошибка чтения Dynamic Event.", ex, path);
                }
            }
        }

        AppLogger.Info(
            "DynamicEventStore: библиотека загружена.",
            $"root={_root}; overlays={_additionalRoots.Count}; count={_items.Count}; readOnly={_readOnly}");
    }

    public bool TryGet(string id, out DynamicEventDefinition definition)
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

    public string Save(DynamicEventDefinition definition, string? existingPath = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Validate(definition);

        if (_readOnly)
            throw new InvalidOperationException("Библиотека Dynamic Events доступна только для чтения.");

        Directory.CreateDirectory(_root);

        var path = string.IsNullOrWhiteSpace(existingPath)
            ? Path.Combine(_root, SanitizeFileName(definition.Id) + Extension)
            : Path.GetFullPath(existingPath);

        EnsureInsideAnyRoot(path);

        if (_items.TryGetValue(definition.Id, out var existing) &&
            !string.Equals(Path.GetFullPath(existing.Path), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Dynamic Event с ID «" + definition.Id + "» уже существует в другом файле.");
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

        var document = new DynamicEventDefinitionDocument(
            SupportedSchemaVersion,
            DynamicEventDefinitionRules.FormatName,
            definition);

        File.WriteAllText(path, ResourceJsonFormat.Serialize(document));
        _items[definition.Id] = (definition, path);

        return path;
    }

    public bool Delete(string id)
    {
        if (_readOnly)
            throw new InvalidOperationException("Библиотека Dynamic Events доступна только для чтения.");

        if (!_items.TryGetValue(id, out var item))
            return false;

        if (File.Exists(item.Path))
            File.Delete(item.Path);

        _items.Remove(id);
        AppLogger.Info("DynamicEventStore: удалён Dynamic Event.", $"id={id}; path={item.Path}");
        return true;
    }

    private static void Validate(DynamicEventDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new InvalidOperationException("ID Dynamic Event не может быть пустым.");

        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new InvalidOperationException("Название Dynamic Event не может быть пустым.");

        if (string.IsNullOrWhiteSpace(definition.LocationId))
            throw new InvalidOperationException("Для Dynamic Event нужен Location.");

        if (!double.IsFinite(definition.TriggerRadius) || definition.TriggerRadius < 0)
            throw new InvalidOperationException("Радиус Dynamic Event должен быть конечным неотрицательным числом.");

        if (!DynamicEventDefinitionRules.IsSupportedTrigger(definition.Trigger.Type))
        {
            // Незнакомые provider-trigger типы разрешаем сохранять: это canonical
            // data, а не доказательство, что Sandbox умеет их исполнять.
        }

        if (definition.SpawnPolicy.MaxActiveInstances < 0)
            throw new InvalidOperationException("Максимум активных экземпляров не может быть отрицательным.");

        if (!double.IsFinite(definition.SpawnPolicy.SpawnChance) ||
            definition.SpawnPolicy.SpawnChance < 0 ||
            definition.SpawnPolicy.SpawnChance > 1)
            throw new InvalidOperationException("Вероятность генерации должна быть от 0 до 1.");
    }

    private void EnsureInsideAnyRoot(string path)
    {
        foreach (var root in new[] { _root }.Concat(_additionalRoots))
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return;
        }

        throw new InvalidOperationException(
            "Путь Dynamic Event выходит за пределы библиотеки.");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim();

        return string.IsNullOrWhiteSpace(result) ? "event" : result;
    }
}
