using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Астрономия светового дня.
///
/// Проверяются астрономические факты, а не формулы: на широте Челябинска день
/// длиннее в июне и короче в декабре, восход раньше заката, полдень между ними,
/// а доля светового дня монотонно растёт утром и падает вечером.
/// </summary>
public sealed class SolarAstronomyTests
{
    // Челябинск: широта 55°, часовой пояс UTC+5.
    private static readonly GeoCoordinate Chelyabinsk = new(55.1644, 61.4368);

    private static DateTimeOffset Moment(int month, int day, int hour, int minute = 0) =>
        new(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Utc), TimeSpan.Zero);

    [Fact]
    public void SummerDayIsLongerThanWinterDay()
    {
        var summer = SolarAstronomy.Describe(Moment(6, 21, 12), Chelyabinsk);
        var winter = SolarAstronomy.Describe(Moment(12, 21, 12), Chelyabinsk);

        Assert.False(summer.IsPolarDay);
        Assert.False(summer.IsPolarNight);
        Assert.False(winter.IsPolarDay);
        Assert.False(winter.IsPolarNight);

        Assert.True(summer.DayLength > TimeSpan.FromHours(16),
            $"Летний день на 55° широты должен быть длиннее 16 ч, получено {summer.DayLength}.");
        Assert.True(winter.DayLength < TimeSpan.FromHours(9),
            $"Зимний день на 55° широты должен быть короче 9 ч, получено {winter.DayLength}.");
    }

    [Fact]
    public void SunriseComesBeforeNoonAndSunsetAfter()
    {
        var phase = SolarAstronomy.Describe(Moment(3, 20, 12), Chelyabinsk);

        Assert.True(phase.Sunrise < phase.SolarNoon, "Восход должен быть раньше полудня.");
        Assert.True(phase.SolarNoon < phase.Sunset, "Полдень должен быть раньше заката.");
    }

    [Fact]
    public void DayFractionIsHalfAtSolarNoonAndOutsideRangeAtNight()
    {
        var noon = SolarAstronomy.Describe(Moment(5, 15, 12), Chelyabinsk);

        // Проверка «в полдень ровно 0.5» делается по ВЫЧИСЛЕННОМУ полудню:
        // в Челябинске (долгота 61°E) истинный полдень наступает около 07:50 UTC,
        // поэтому 12:00 по календарю — уже вторая половина дня.
        var atNoon = SolarAstronomy.Describe(
            new DateTimeOffset(noon.SolarNoon.UtcDateTime, TimeSpan.Zero),
            Chelyabinsk);
        Assert.InRange(atNoon.DayFraction, 0.45, 0.55);

        // Доля монотонно растёт от восхода к закату: иначе индикатор светового
        // дня «дёргался» бы назад в течение дня.
        var fractions = new[] { 1, 3, 6, 8, 12 }
            .Select(hour => SolarAstronomy.Describe(Moment(5, 15, hour), Chelyabinsk).DayFraction)
            .ToArray();
        for (var index = 1; index < fractions.Length; index++)
        {
            Assert.True(fractions[index] > fractions[index - 1],
                $"Доля светового дня должна расти: {fractions[index - 1]:F3} → {fractions[index]:F3}.");
        }

        // Ночью доля выходит за пределы [0,1]: после заката > 1, до восхода < 0.
        var afterSunset = SolarAstronomy.Describe(Moment(5, 15, 18), Chelyabinsk);
        Assert.True(afterSunset.DayFraction > 1,
            $"После заката доля должна быть больше 1, получено {afterSunset.DayFraction}.");
        Assert.False(afterSunset.IsDay);
    }

    [Fact]
    public void SunAltitudeIsHighestAtSolarNoon()
    {
        var phase = SolarAstronomy.Describe(Moment(6, 21, 12), Chelyabinsk);
        var noonAltitude = SolarAstronomy.SunAltitude(
            172, Chelyabinsk,
            phase.SolarNoon.Hour + phase.SolarNoon.Minute / 60.0);

        var morningAltitude = SolarAstronomy.SunAltitude(172, Chelyabinsk, 3);

        Assert.True(noonAltitude > morningAltitude,
            "Высота солнца в полдень должна быть выше, чем утром.");
        Assert.True(noonAltitude > 50,
            $"Летом на 55° широты солнце в полдень выше 50°, получено {noonAltitude}.");
    }

    [Fact]
    public void PolarNightAndPolarDayAreReportedWithoutThrowing()
    {
        // Шпицберген: за полярным кругом солнце не восходит зимой и не заходит летом.
        var svalbard = new GeoCoordinate(78.22, 15.65);

        var winter = SolarAstronomy.Describe(Moment(12, 21, 12), svalbard);
        var summer = SolarAstronomy.Describe(Moment(6, 21, 12), svalbard);

        Assert.True(winter.IsPolarNight, "В декабре на 78° широты полярная ночь.");
        Assert.True(summer.IsPolarDay, "В июне на 78° широты полярный день.");
        Assert.Equal(-1, winter.DayFraction);
        Assert.Equal(0.5, summer.DayFraction);
        Assert.Equal("не восходит", SolarAstronomy.DescribeSunrise(winter));
        Assert.Equal("не заходит", SolarAstronomy.DescribeSunset(summer));
    }

    [Fact]
    public void InvalidCoordinateFallsBackToDefaultInsteadOfThrowing()
    {
        var phase = SolarAstronomy.Describe(Moment(6, 21, 12), new GeoCoordinate(999, 999));

        Assert.False(phase.IsPolarDay);
        Assert.False(phase.IsPolarNight);
        Assert.True(phase.DayLength > TimeSpan.Zero);
    }

    [Fact]
    public void DayLengthGrowsUntilJuneSolsticeThenShrinks()
    {
        // Световой день растёт до летнего солнцестояния (21 июня) и убывает
        // после. Проверять рост до сентября нельзя: это противоречит астрономии.
        var months = new[] { 1, 3, 5, 6 };
        var growing = months
            .Select(month => SolarAstronomy.Describe(Moment(month, 21, 12), Chelyabinsk).DayLength)
            .ToArray();

        for (var index = 1; index < growing.Length; index++)
        {
            Assert.True(growing[index] > growing[index - 1],
                $"Световой день должен расти до июня: {growing[index - 1]} → {growing[index]}.");
        }

        var afterSolstice = new[] { 6, 8, 10, 12 }
            .Select(month => SolarAstronomy.Describe(Moment(month, 21, 12), Chelyabinsk).DayLength)
            .ToArray();

        for (var index = 1; index < afterSolstice.Length; index++)
        {
            Assert.True(afterSolstice[index] < afterSolstice[index - 1],
                $"Световой день должен убывать после июня: {afterSolstice[index - 1]} → {afterSolstice[index]}.");
        }
    }
}

/// <summary>Календарь игрового мира.</summary>
public sealed class GameCalendarTests
{
    [Fact]
    public void DefaultStartDateIsFirstOfJanuary2026()
    {
        var start = GameCalendar.DefaultStartDate;

        Assert.Equal(2026, start.Year);
        Assert.Equal(1, start.Month);
        Assert.Equal(1, start.Day);
        Assert.Equal(TimeSpan.Zero, start.Offset);
    }

    [Fact]
    public void NowIsStartDatePlusElapsed()
    {
        var clock = new WorldClockState(GameCalendar.DefaultStartDate, TimeSpan.FromHours(30), true);

        Assert.Equal(new DateTime(2026, 1, 2, 6, 0, 0), clock.Now.DateTime);
    }

    [Theory]
    [InlineData(1, 15, "Зима")]
    [InlineData(4, 15, "Весна")]
    [InlineData(7, 15, "Лето")]
    [InlineData(10, 15, "Осень")]
    public void SeasonFollowsAstronomicalBoundaries(int month, int day, string expected)
    {
        var moment = new DateTimeOffset(
            new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Unspecified),
            TimeSpan.Zero);

        Assert.Equal(expected, GameCalendar.SeasonName(moment));
    }

    [Fact]
    public void FormatsDateAndTimeForUi()
    {
        var moment = new DateTimeOffset(
            new DateTime(2026, 3, 7, 9, 5, 0, DateTimeKind.Unspecified),
            TimeSpan.Zero);

        Assert.Equal("07.03.2026", GameCalendar.FormatDate(moment));
        Assert.Equal("09:05", GameCalendar.FormatTime(moment));
    }
}
