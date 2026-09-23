namespace AssistQuestEditor.App;

/// <summary>
/// Запрос режима визуализации на карте Симулятора.
///
/// Редактор локаций отбирает точки по критериям, но его собственная карта не
/// имеет ориентиров — на ней нет городов, дорог и прочих признаков мира, поэтому
/// непонятно, ГДЕ именно оказались отобранные точки. Основная карта Симулятора
/// такие ориентиры имеет, поэтому отобранный набор показывается поверх неё.
/// </summary>
/// <param name="Title">Заголовок для подписи режима (имя Location или «тест»).</param>
/// <param name="Points">Отобранные точки в порядке показа (нумерация 1..N).</param>
/// <param name="Diagnostics">Пояснение к набору: раунды, повторы, предупреждения.</param>
public sealed record LocationVisualisationRequest(
    string Title,
    IReadOnlyList<LocationVisualisationPoint> Points,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// Одна отобранная точка в режиме визуализации.
/// </summary>
/// <param name="PointId">Стабильный ID WorldPoint (для подсветки и подписи).</param>
/// <param name="Index">Номер показа (1..N): совпадает с нумерацией тестовой карты.</param>
public sealed record LocationVisualisationPoint(
    string PointId,
    int Index);
