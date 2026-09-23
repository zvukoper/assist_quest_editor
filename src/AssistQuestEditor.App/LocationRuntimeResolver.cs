using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Адаптер пользовательской Location Library для Domain Runtime.
///
/// Runtime знает только ILocationResolver. Файлы и пользовательская библиотека
/// остаются в App, поэтому Domain не зависит от способа хранения ресурсов.
/// </summary>
public sealed class LocationRuntimeResolver : ILocationResolver
{
    private readonly LocationStore _store;
    private readonly IDataChannel<WorldState> _world;
    private readonly LocationResolver _resolver = new();

    public LocationRuntimeResolver(LocationStore store, IDataChannelHub hub)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(hub);
        _world = hub.Get<WorldState>("world");
    }

    public WorldPoint? Resolve(string locationId)
    {
        if (!_store.TryGet(locationId, out var definition))
            return null;

        return _resolver.Resolve(definition, _world.Value.Points).Point;
    }
}
