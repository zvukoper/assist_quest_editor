using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Начисление усталости. Раньше она зависела ТОЛЬКО от игрового времени, а оно
/// идёт 1:1 с реальным: 100% за 18 часов — это 5.6% в час, то есть в пределах
/// сессии незаметно (значение показывается целым числом). Пользователь видел
/// «усталость не изменяется со временем и движением».
/// </summary>
public sealed class PlayerConditionEngineTests
{
    private const double OneGameHourSeconds = 3600d;

    private static PlayerVitalsState Vitals() =>
        new(100, 100, 100, 100, 100, 100, 0, 100);

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
    public void TraveledDistanceAddsFatigueBeyondElapsedTime()
    {
        var timeOnly = Advance(hours: 1);
        var withDistance = Advance(hours: 1, traveledMeters: 450_000);

        Assert.True(
            withDistance.Vitals.Fatigue > timeOnly.Vitals.Fatigue + 40d,
            $"дистанция должна давать прирост: время={timeOnly.Vitals.Fatigue:0.##}; " +
            $"с дистанцией={withDistance.Vitals.Fatigue:0.##}");

        // 450 км — половина пути до 100%: вместе с часом езды получается 5.6 + 50.
        Assert.Equal(100d / 18d + 50d, withDistance.Vitals.Fatigue, 3);
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

        // 450 км из 900 = 50%; три часа из восемнадцати = 16.7%.
        Assert.Equal(50d + 100d / 18d * 3d, vitals.Fatigue, 3);
    }

    /// <summary>
    /// Сон по-прежнему снимает усталость полностью: дистанционная составляющая
    /// не должна его сломать.
    /// </summary>
    [Fact]
    public void FullSleepStillResetsFatigue()
    {
        var tired = Advance(hours: 3, traveledMeters: 600_000).Vitals;

        Assert.True(tired.Fatigue > 50d, "подготовка теста: ожидалась заметная усталость");

        var sleep = PlayerConditionEngine.Sleep(
            tired,
            PlayerConditionState.Empty,
            hours: 4,
            fullSleep: true);

        Assert.Equal(0d, sleep.Vitals.Fatigue, 3);
    }
}
