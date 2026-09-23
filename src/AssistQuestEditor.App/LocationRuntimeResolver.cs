using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Адаптер пользовательской Location Library для Domain Runtime.
///
/// Runtime знает только ILocationResolver. Файлы и пользовательская библиотека
/// остаются в App, поэтому Domain не зависит от способа хранения ресурсов.
/// </summary>
public sealed class LocationRuntimeResolver : ILocationResolver, ILocationResolutionSession
{
    private readonly LocationStore _store;
    private readonly IDataChannel<WorldState> _world;
    private readonly LocationResolver _resolver = new();
    private readonly Dictionary<string, WorldPoint> _resolved =
        new(StringComparer.OrdinalIgnoreCase);

    public LocationRuntimeResolver(LocationStore store, IDataChannelHub hub)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(hub);
        _world = hub.Get<WorldState>("world");
    }

    public WorldPoint? Resolve(string locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId) ||
            !_store.TryGet(locationId, out var definition))
            return null;

        if (_resolved.TryGetValue(locationId, out var cached))
            return cached;

        var point = _resolver.Resolve(definition, _world.Value.Points).Point;
        if (point is not null)
            _resolved[locationId] = point;

        return point;
    }

    public void Reset() => _resolved.Clear();

    public void Invalidate(string locationId)
    {
        if (!string.IsNullOrWhiteSpace(locationId))
            _resolved.Remove(locationId);
    }
}
