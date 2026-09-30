Маршруты: скрытая лишняя работа

В RoutePlan:

public IReadOnlyList<RoutePoint> Points =>
    BuildPoints(Legs, WaypointSource);

public IReadOnlyList<RouteSegment> Segments =>
    BuildSegments(Points);

public double TotalDistanceMeters =>
    Segments.Sum(...);

Это означает, что:

_routePlan.Points.Count
_routePlan.Points[index]
_routePlan.Segments.Count
_routePlan.TotalDistanceMeters

каждый раз заново строят списки.

Причём комментарий рядом говорит об «идентичности» Points и Segments, но фактической identity объектов как раз нет: каждый getter создаёт новый массив/list.

Для маленького маршрута это незаметно, но сейчас route plan читается много раз при перестроении, движении и создании snapshot. После уже проведённой борьбы с DOM-jank это выглядит как следующая очевидная точка оптимизации: RoutePlan должен материализовать Points/Segments один раз.

Особенно если в будущем маршруты станут длиннее.