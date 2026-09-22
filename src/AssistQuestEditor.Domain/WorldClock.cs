using System.Globalization;
using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Игровое время симулятора.
///
/// Хранится отдельным каналом, а не в EnvironmentState: EnvironmentState — это
/// погода и видимость (визуальные данные конкретного кадра), а игровое время —
/// часть сохраняемого состояния мира. Смешивая их, сохранение тянуло бы за
/// собой погодные поля, а «ход времени» зависел бы от UI-канала.
///
/// <see cref="Elapsed"/> — сколько игрового времени прошло с момента старта
/// мира. Именно длительность, а не абсолютная отметка: абсолютную дату строит
/// календарь (<see cref="GameCalendar"/>), а она зависит от стартовой даты
/// кампании. Так сохранение остаётся корректным при смене стартовой даты.
/// </summary>
public sealed record WorldClockState(
    DateTimeOffset StartDate,
    TimeSpan Elapsed,
    bool Running)
{
    /// <summary>Дата и время мира на текущий момент.</summary>
    public DateTimeOffset Now => StartDate + Elapsed;

    public static WorldClockState CreateDefault() =>
        new(GameCalendar.DefaultStartDate, TimeSpan.Zero, false);
}

/// <summary>
/// Календарь игрового мира.
/// </summary>
public static class GameCalendar
{
    /// <summary>
    /// Дата мира по умолчанию. Задана явно, а не «сегодня»: иначе одинаковое
    /// сохранение открывалось бы в разные игровые даты, и астрономия (восход,
    /// закат, длина дня) менялась бы от запуска к запуску.
    /// </summary>
    public static DateTimeOffset DefaultStartDate =>
        new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero);

    /// <summary>
    /// Название игрового сезона по календарной дате (северное полушарие).
    ///
    /// Считается по астрономическим границам, а не «зима = декабрь-февраль»:
    /// так сезон согласован с продолжительностью дня, которую использует
    /// астрономия светового индикатора.
    /// </summary>
    public static string SeasonName(DateTimeOffset moment)
    {
        var day = moment.DayOfYear;
        return day switch
        {
            < 80 or >= 356 => "Зима",
            < 172 => "Весна",
            < 266 => "Лето",
            _ => "Осень"
        };
    }

    /// <summary>Игровая дата в формате день.месяц.год.</summary>
    public static string FormatDate(DateTimeOffset moment) =>
        moment.ToString("dd.MM.yyyy");

    /// <summary>Игровое время в формате чч:мм.</summary>
    public static string FormatTime(DateTimeOffset moment) =>
        moment.ToString("HH:mm");

    /// <summary>
    /// Игровое время с секундами (чч:мм:сс) — для индикатора хода времени.
    ///
    /// Отдельный формат, а не замена <see cref="FormatTime"/>: время с секундами
    /// нужно только там, где важно ВИДЕТЬ движение (часы в шапке). Поля ввода и
    /// время восхода с секундами были бы неудобны и не нужны.
    /// </summary>
    public static string FormatClock(DateTimeOffset moment) =>
        moment.ToString("HH:mm:ss");
}

/// <summary>
/// Реальная географическая координата мира.
///
/// Не совпадает с игровыми координатами ETS2 и нужна только для астрономии:
/// восхода, заката, полудня и длины светового дня. Поэтому живёт в свойствах
/// кампании, а не у точек мира.
/// </summary>
public sealed record GeoCoordinate(double Latitude, double Longitude)
{
    /// <summary>
    /// Геокоордината по умолчанию для песочницы SibirMap: Челябинск.
    /// Значения по умолчанию нужны, чтобы астрономия работала до того, как в
    /// файле кампании появится явная координата.
    /// </summary>
    public static GeoCoordinate CreateDefault() => new(55.1644, 61.4368);

    /// <summary>
    /// Проверка допустимости координаты.
    ///
    /// Вычисляемое свойство, и это важно подчеркнуть: без <see cref="JsonIgnoreAttribute"/>
    /// сериализатор запишет его в файл кампании как обычное свойство (в файле
    /// появлялось <c>"isValid": true</c>). Данные, а не разметка, в файле быть не должно.
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180;
}
