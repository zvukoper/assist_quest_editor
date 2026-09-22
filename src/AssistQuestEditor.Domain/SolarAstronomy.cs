using System.Globalization;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Фаза светового дня: положение солнца относительно горизонта и доля
/// пройденного светового времени.
///
/// <see cref="DayFraction"/> = 0 в момент восхода, 0.5 в полдень, 1 в момент
/// заката. Отрицательное значение означает «солнце ещё не взошло»,
/// больше единицы — «уже село»; индикатор по этим случаям рисует ночь.
/// </summary>
public sealed record DayPhase(
    DateTimeOffset Sunrise,
    DateTimeOffset Sunset,
    DateTimeOffset SolarNoon,
    double DayFraction,
    double SunAltitudeDegrees)
{
    public bool IsDay => DayFraction is >= 0 and <= 1;

    /// <summary>Длительность светового дня.</summary>
    public TimeSpan DayLength => Sunset - Sunrise;

    /// <summary>
    /// Полярный день: солнце не заходит.
    /// Считается по факту отсутствия пересечения горизонта, а не по широте:
    /// так индикатор корректно работает и на границе полярного круга.
    /// </summary>
    public bool IsPolarDay { get; init; }

    /// <summary>Полярная ночь: солнце не восходит.</summary>
    public bool IsPolarNight { get; init; }
}

/// <summary>
/// Астрономия светового дня: восход, закат, полдень и позиция солнца.
///
/// Реализация намеренно без внешних зависимостей: расчёт идёт по алгоритму
/// NOAA (склонение Солнца, часовой угол, истинный полдень). Точность в пределах
/// минуты для индикатора светового дня избыточна, а предсказуемость важнее:
/// результат не зависит от библиотек, часового пояса машины и версии .NET.
///
/// Все расчёты ведутся в UTC. Это принципиально: долгота уже задаёт смещение
/// солнечного времени, и добавление ещё и пояса машины сдвигало бы восход.
/// </summary>
public static class SolarAstronomy
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    /// <summary>
    /// Астрономические сумерки для восхода и заката. Официальное значение
    /// NOAA: солнечный диск считается взошедшим, когда его центр на 0.833°
    /// ниже горизонта (рефракция + радиус диска).
    /// </summary>
    private const double SunriseAltitudeDegrees = -0.833;

    /// <summary>
    /// Фаза светового дня на заданный момент.
    ///
    /// Возвращает корректный результат всегда: при полярном дне или ночи
    /// <see cref="DayPhase.IsPolarDay"/>/<see cref="DayPhase.IsPolarNight"/>
    /// выставлены, а <see cref="DayPhase.DayFraction"/> равен 0.5 (полярный
    /// день) или -1 (полярная ночь).
    /// </summary>
    public static DayPhase Describe(DateTimeOffset moment, GeoCoordinate location)
    {
        if (location is null || !location.IsValid)
            location = GeoCoordinate.CreateDefault();

        // Игровое время хранится как UTC-подобное «наивное» значение. Для
        // астрономии нужны только календарный день и время суток, поэтому
        // компоненты берутся как есть, без пересчёта пояса.
        var localDay = new DateTime(moment.Year, moment.Month, moment.Day, 0, 0, 0, DateTimeKind.Utc);
        var hours = moment.Hour + moment.Minute / 60.0 + moment.Second / 3600.0;

        var dayOfYear = localDay.DayOfYear;
        var solarDeclination = SolarDeclination(dayOfYear);

        // Часовой угол горизонта: cos(H) = (sin(a) - sin(φ)·sin(δ)) / (cos(φ)·cos(δ)).
        var latitudeRadians = location.Latitude * DegreesToRadians;
        var declinationRadians = solarDeclination * DegreesToRadians;
        var altitudeRadians = SunriseAltitudeDegrees * DegreesToRadians;

        var cosHourAngle =
            (Math.Sin(altitudeRadians) - Math.Sin(latitudeRadians) * Math.Sin(declinationRadians)) /
            (Math.Cos(latitudeRadians) * Math.Cos(declinationRadians));

        // Истинный полдень: момент, когда солнце над меридианом наблюдателя.
        var equationOfTime = EquationOfTimeMinutes(dayOfYear);
        var solarNoonHours = 12.0 - location.Longitude / 15.0 - equationOfTime / 60.0;

        if (cosHourAngle > 1)
        {
            // Солнце не поднимается над горизонтом: полярная ночь.
            var noon = localDay.AddHours(solarNoonHours);
            return new DayPhase(
                noon, noon, noon, -1, SunAltitude(dayOfYear, location, 12))
            {
                IsPolarNight = true
            };
        }

        if (cosHourAngle < -1)
        {
            // Солнце не опускается под горизонт: полярный день.
            var noon = localDay.AddHours(solarNoonHours);
            return new DayPhase(
                noon, noon, noon, 0.5, SunAltitude(dayOfYear, location, 12))
            {
                IsPolarDay = true
            };
        }

        var hourAngleDegrees = Math.Acos(cosHourAngle) * RadiansToDegrees;
        var halfDayHours = hourAngleDegrees / 15.0;

        // Солнечные сутки не совпадают с календарными: при восточной долготе
        // истинный полдень наступает до 12:00 UTC, поэтому восход уходит на
        // предыдущий календарный день (для 61°E это ~23:47 UTC). Выбираем
        // солнечный полдень, БЛИЖАЙШИЙ к текущему моменту, — тогда восход и
        // закат всегда лежат вокруг наблюдаемого момента, и доля светового дня
        // не зависит от того, попала ли полночь между ними.
        var solarNoon = localDay.AddHours(solarNoonHours);
        var offsetHours = (moment - solarNoon).TotalHours;
        if (offsetHours > 12) solarNoon = solarNoon.AddDays(1);
        else if (offsetHours < -12) solarNoon = solarNoon.AddDays(-1);

        var sunrise = solarNoon.AddHours(-halfDayHours);
        var sunset = solarNoon.AddHours(halfDayHours);

        var dayLengthHours = sunset.Subtract(sunrise).TotalHours;
        var fraction = dayLengthHours <= 0
            ? -1
            : moment.Subtract(sunrise).TotalHours / dayLengthHours;

        return new DayPhase(
            sunrise,
            sunset,
            solarNoon,
            fraction,
            SunAltitude(dayOfYear, location, hours));
    }

    /// <summary>Склонение Солнца в градусах (NOAA, по дню года).</summary>
    public static double SolarDeclination(int dayOfYear)
    {
        var gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1);
        var declination =
            0.006918 -
            0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma) -
            0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma) -
            0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
        return declination * RadiansToDegrees;
    }

    /// <summary>Уравнение времени в минутах (NOAA).</summary>
    public static double EquationOfTimeMinutes(int dayOfYear)
    {
        var gamma = 2.0 * Math.PI / 365.0 * (dayOfYear - 1);
        var eot =
            229.18 * (0.000075 +
            0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma) -
            0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        return eot;
    }

    /// <summary>Высота солнца над горизонтом в градусах.</summary>
    public static double SunAltitude(int dayOfYear, GeoCoordinate location, double hours)
    {
        var declination = SolarDeclination(dayOfYear) * DegreesToRadians;
        var latitude = location.Latitude * DegreesToRadians;

        // Часовой угол: 0 в истинный полдень, ±180° в полночь.
        var equationOfTime = EquationOfTimeMinutes(dayOfYear);
        var solarTime = hours - equationOfTime / 60.0 + location.Longitude / 15.0;
        var hourAngle = (solarTime - 12.0) * 15.0 * DegreesToRadians;

        var sinAltitude =
            Math.Sin(latitude) * Math.Sin(declination) +
            Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);

        return Math.Asin(Math.Max(-1, Math.Min(1, sinAltitude))) * RadiansToDegrees;
    }

    /// <summary>
    /// Описание фазы для интерфейса: время восхода, заката, длина дня и сезон.
    /// Строки собираются здесь, а не в web-слое: формат даты и времени должен
    /// быть единым с игровым календарём.
    /// </summary>
    public static string DescribeSunrise(DayPhase phase) =>
        phase.IsPolarNight
            ? "не восходит"
            : phase.Sunrise.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string DescribeSunset(DayPhase phase) =>
        phase.IsPolarDay
            ? "не заходит"
            : phase.Sunset.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string DescribeDayLength(DayPhase phase)
    {
        if (phase.IsPolarNight) return "полярная ночь";
        if (phase.IsPolarDay) return "полярный день";

        var total = phase.DayLength;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)total.TotalHours} ч {total.Minutes:00} мин");
    }
}
