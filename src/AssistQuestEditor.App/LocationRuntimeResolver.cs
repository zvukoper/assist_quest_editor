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
    private readonly IDataChannel<PlayerState> _player;
    private readonly RoadIndex _roads;
    private readonly JunctionIndex _junctions;
    private readonly ICityBoundarySource _cityBoundaries;
    private readonly LocationResolver _resolver = new();
    private readonly Dictionary<(string LocationId, string? ResolutionKey), WorldPoint> _resolved =
        new();

    /// <summary>Индекс черт, по которому построен кэш разрешений.</summary>
    private CityBoundaryIndex? _lastCities;

    public LocationRuntimeResolver(
        LocationStore store,
        IDataChannelHub hub,
        RoadIndex? roads = null,
        JunctionIndex? junctions = null,
        ICityBoundarySource? cityBoundaries = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(hub);
        _world = hub.Get<WorldState>("world");
        _player = hub.Get<PlayerState>("player");
        _roads = roads ?? new RoadIndex(Array.Empty<RoadSegment>());
        _junctions = junctions ?? new JunctionIndex(Array.Empty<JunctionPoint>());
        _cityBoundaries = cityBoundaries ?? new StaticCityBoundarySource();
    }

    public WorldPoint? Resolve(string locationId) => Resolve(locationId, null);

    public WorldPoint? Resolve(string locationId, string? resolutionKey)
    {
        if (string.IsNullOrWhiteSpace(locationId) ||
            !_store.TryGet(locationId, out var definition))
            return null;

        var key = (
            LocationId: locationId,
            ResolutionKey: string.IsNullOrWhiteSpace(resolutionKey)
                ? null
                : resolutionKey.Trim());

        if (_resolved.TryGetValue(key, out var cached))
            return cached;

        // Черты могли перерисовать уже после того, как часть локаций разрешилась.
        // Кэш разрешений от этого устаревает: он помнит точку, найденную по ПРЕЖНЕЙ
        // черте. Сравнение ссылок достаточно — источник отдаёт новый индекс только
        // после фактического перечитывания файла.
        var cities = _cityBoundaries.Current;
        if (!ReferenceEquals(cities, _lastCities))
        {
            _lastCities = cities;
            _resolved.Clear();
        }

        // Позиция игрока передаётся обязательно: критерий «радиус от игрока» без
        // неё неоценим, а кэш сделал бы первый (возможно, неудачный) результат
        // постоянным — Resolve запоминает только успешное разрешение.
        var point = _resolver.Resolve(
            definition,
            _world.Value.Points,
            _player.Value.Position,
            roads: _roads,
            junctions: _junctions,
            cities: cities).Point;

        if (point is not null)
            _resolved[key] = point;

        return point;
    }

    /// <summary>
    /// Радиус из самой Location.
    ///
    /// Раньше поле радиуса в редакторе локаций ни на что не влияло: Runtime
    /// всегда брал triggerRadius ноды. Теперь при ссылке на Location её радиус
    /// важнее параметра ноды, иначе значение в файле .aqlocation было бы
    /// недостижимо для автора.
    /// </summary>
    public double? ResolveTriggerRadius(string locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId) ||
            !_store.TryGet(locationId, out var definition))
            return null;

        return double.IsFinite(definition.TriggerRadius) ? definition.TriggerRadius : null;
    }

    public void Reset() => _resolved.Clear();

    public void Invalidate(string locationId)
    {
        if (string.IsNullOrWhiteSpace(locationId))
            return;

        var keys = _resolved.Keys
            .Where(key => key.LocationId.Equals(locationId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var key in keys)
            _resolved.Remove(key);
    }

    public void Invalidate(string locationId, string? resolutionKey)
    {
        if (string.IsNullOrWhiteSpace(locationId))
            return;

        var key = (
            LocationId: locationId,
            ResolutionKey: string.IsNullOrWhiteSpace(resolutionKey) ? null : resolutionKey.Trim());

        _resolved.Remove(key);
    }
}
