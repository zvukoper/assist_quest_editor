using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Начисление усталости. Раньше она зависела ТОЛЬКО от игрового времени, а оно
/// идёт 1:1 с реальным: 100% за 18 часов — это 5.6% в час, то есть в пределах
/// сессии незаметно (значение показывается целым числом). Пользователь видел
/// «усталость не изменяется со временем и движением».
///
/// Ожидаемые значения записаны В ПРОЦЕНТАХ и переводятся в единицы шкалы
/// (<see cref="PlayerConditionScale"/>) тем же методом, что и сам движок: тест
/// проверяет правила («100% за 18 часов», «100% за 900 км»), а не арифметику
/// пересчёта шкалы, иначе при изменении размера шкалы пришлось бы править все
/// ожидания вместо одной константы.
/// </summary>
public sealed class PlayerConditionEngineTests
{
    private const double OneGameHourSeconds = 3600d;

    private static PlayerVitalsState Vitals() => PlayerVitalsState.Default;

    private static PlayerConditionUpdate Advance(
        double hours,
        bool moving = true,
        double traveledMeters = 0d,
        PlayerVitalsState? vitals = null)
    {
        return PlayerConditionEngine.Advance(
            vitals ?? Vitals(),
            PlayerConditionState.Empty,
            hours * OneGameHourSeconds,
            realSeconds: hours * OneGameHourSeconds,
            playerMoving: moving,
            sleeping: false,
            traveledMeters);
    }

    /// <summary>
    /// Дистанция утомляет НАРАВНЕ со временем: 450 км (половина от
    /// FatigueBuildKilometers) за час дают заметный прирост, которого от одного
    /// лишь часа ожидания не будет.
    /// </summary>
    [Fact]
    public void AcceleratedGameTimeIncreasesTimeBasedFatigue()
    {
        var vitals = PlayerVitalsState.Default;
        var conditions = PlayerConditionState.Empty;
        var normal = PlayerConditionEngine.Advance(
            vitals,
            conditions,
            3600,
            3600,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0);

        var accelerated = PlayerConditionEngine.Advance(
            vitals,
            conditions,
            8 * 3600,
            3600,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0);

        Assert.Equal(
            PlayerConditionScale.FromPercent(100d / PlayerConditionEngine.FatigueBuildHours),
            normal.Vitals.Fatigue,
            6);

        // Движок начисляет ЧАСОВЫМИ шагами, и каждый шаг округляется до целой
        // единицы шкалы. Восемь шагов дают восемь округлённых слагаемых, а не одно
        // округление от суммы: шкала целочисленная, и шаг — её естественная
        // гранулярность. Расхождение — единицы из 10000, то есть 0.04%.
        Assert.Equal(
            8d * PlayerConditionScale.FromPercent(
                100d / PlayerConditionEngine.FatigueBuildHours),
            accelerated.Vitals.Fatigue,
            6);
    }

    [Fact]
    public void TraveledDistanceAddsFatigueBeyondElapsedTime()
    {
        var timeOnly = Advance(hours: 1);
        var withDistance = Advance(hours: 1, traveledMeters: 450_000);

        Assert.True(
            withDistance.Vitals.Fatigue > timeOnly.Vitals.Fatigue + 4000d,
            $"дистанция должна давать прирост: время={timeOnly.Vitals.Fatigue:0.##}; " +
            $"с дистанцией={withDistance.Vitals.Fatigue:0.##}");

        // 450 км — половина пути до 100%: вместе с часом езды получается 5.6 + 50.
        Assert.Equal(
            PlayerConditionScale.FromPercent(100d / 18d + 50d),
            withDistance.Vitals.Fatigue,
            3);
    }

    /// <summary>
    /// Игрок стоит на месте: дистанции нет, усталость только восстанавливается —
    /// даже если вызывающий по ошибке передал непустой путь.
    /// </summary>
    [Fact]
    public void StandingStillRecoversAndIgnoresTraveledDistance()
    {
        var resting = Advance(hours: 1, moving: false, traveledMeters: 450_000);

        Assert.True(resting.Vitals.Fatigue <= 0.0001d,
            "стоящий на месте игрок не должен уставать, получено " +
            resting.Vitals.Fatigue.ToString("0.##"));
    }

    /// <summary>
    /// Дробление интервала не меняет результат: дистанция распределяется по
    /// шагам пропорционально их длительности.
    /// </summary>
    [Fact]
    public void DistanceFatigueIsSplitProportionallyAcrossSteps()
    {
        var single = Advance(hours: 2.5, traveledMeters: 300_000);

        var first = Advance(hours: 1, traveledMeters: 120_000);
        var second = PlayerConditionEngine.Advance(
            first.Vitals,
            first.Conditions,
            OneGameHourSeconds,
            OneGameHourSeconds,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 120_000);
        var third = PlayerConditionEngine.Advance(
            second.Vitals,
            second.Conditions,
            0.5d * OneGameHourSeconds,
            0.5d * OneGameHourSeconds,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 60_000);

        Assert.Equal(single.Vitals.Fatigue, third.Vitals.Fatigue, 3);
    }

    /// <summary>
    /// Дистанция складывается из НЕСКОЛЬКИХ шагов: 150 км за час на каждом из
    /// трёх шагов дают ровно 50% от километров и 16.7% от часов. Проверяется до
    /// критической отметки (80%), чтобы не мешало мягкое ограничение шкалы.
    /// </summary>
    [Fact]
    public void DistanceFatigueAccumulatesAcrossSteps()
    {
        var vitals = Vitals();
        var conditions = PlayerConditionState.Empty;

        for (var step = 0; step < 3; step++)
        {
            var update = PlayerConditionEngine.Advance(
                vitals,
                conditions,
                OneGameHourSeconds,
                OneGameHourSeconds,
                playerMoving: true,
                sleeping: false,
                traveledMeters: 150_000);

            vitals = update.Vitals;
            conditions = update.Conditions;
        }

        // 450 км из 900 = 50%; три часа из восемнадцати = 16.7%. Каждый из трёх
        // часовых шагов округляется до целой единицы шкалы — отсюда множитель.
        Assert.Equal(
            3d * PlayerConditionScale.FromPercent(100d / 18d + 100d / 900d * 150d),
            vitals.Fatigue,
            3);
    }

    /// <summary>
    /// Сон по-прежнему снимает усталость полностью: дистанционная составляющая
    /// не должна его сломать.
    /// </summary>
    [Fact]
    public void FullSleepStillResetsFatigue()
    {
        var tired = Advance(hours: 3, traveledMeters: 600_000).Vitals;

        Assert.True(
            tired.Fatigue > PlayerConditionScale.FromPercent(50d),
            "подготовка теста: ожидалась заметная усталость");

        var sleep = PlayerConditionEngine.Sleep(
            tired,
            PlayerConditionState.Empty,
            hours: 4,
            fullSleep: true);

        Assert.Equal(0d, sleep.Vitals.Fatigue, 3);
    }

    /// <summary>
    /// Подсказка шкалы показывает скорость изменения за ИГРОВУЮ МИНУТУ, и она
    /// обязана быть целыми единицами: в процентах та же скорость равнялась 0.09
    /// и округлялась до нуля, то есть игрок видел «шкала не движется».
    /// </summary>
    [Fact]
    public void RatesShowVisibleUnitsPerGameMinute()
    {
        var moving = PlayerConditionRates.From(
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            playerMoving: true,
            sleeping: false);

        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                100d / PlayerConditionEngine.FatigueBuildHours / 60d),
            moving.FatiguePerGameMinute,
            6);

        Assert.True(
            moving.FatiguePerGameMinute >= 1d,
            "скорость усталости обязана быть видимой: " +
            moving.FatiguePerGameMinute.ToString("0.###"));

        // Восстановление идёт в минус — знак нужен подсказке, чтобы игрок видел
        // направление, а не только величину.
        var resting = PlayerConditionRates.From(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            PlayerConditionState.Empty,
            playerMoving: false,
            sleeping: false);

        Assert.True(
            resting.FatiguePerGameMinute < 0d,
            "стоящий игрок восстанавливает усталость: " +
            resting.FatiguePerGameMinute.ToString("0.###"));

        // Во сне шкала уходит вниз быстрее, чем при отдыхе на месте, — иначе
        // подсказка не объясняла бы, зачем игроку спать.
        var sleeping = PlayerConditionRates.From(
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            playerMoving: false,
            sleeping: true);

        Assert.True(
            sleeping.FatiguePerGameMinute < moving.FatiguePerGameMinute,
            "сон обязан снимать усталость");
    }

    /// <summary>
    /// Шкала целочисленная и кратна проценту: перевод туда-обратно не должен
    /// накапливать погрешность, иначе интерфейс показывал бы «99%» вместо «100%».
    /// </summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(37d)]
    [InlineData(80d)]
    [InlineData(100d)]
    public void ScaleRoundTripsThroughPercent(double percent)
    {
        var units = PlayerConditionScale.FromPercent(percent);

        Assert.Equal(units, Math.Round(units), 6);
        Assert.Equal(percent, PlayerConditionScale.ToPercent(units), 6);
    }
}
