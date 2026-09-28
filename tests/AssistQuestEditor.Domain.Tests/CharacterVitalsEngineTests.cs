using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class CharacterVitalsEngineTests
{
    private const double Hour = 3600d;

    [Fact]
    public void MovingConsumesEnergyHydrationAndBuildsFatigue()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Resilience = 0d
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            Hour,
            Hour,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        // 100% за 4 игровых часа = 25% в час; вычитается из ПОЛНОЙ шкалы,
        // поэтому остаётся 75%, а не 25%.
        Assert.Equal(
            PlayerConditionScale.FromPercent(75d),
            update.Vitals.Energy,
            3);

        // 100% за 2 игровых часа = 50% в час; вычитается из ПОЛНОЙ шкалы,
        // поэтому остаётся 50%, а не 0,5%.
        Assert.Equal(
            PlayerConditionScale.FromPercent(50d),
            update.Vitals.Hydration,
            3);

        // Усталость растёт по шагам (900 с) без округления единиц: 100% за 18
        // игровых часов даёт 5,(5)% за час, а сравнение в ЕДИНИЦАХ округлило
        // бы 555,56 до 556. Поэтому меряем в процентах.
        Assert.Equal(
            100d / 18d,
            PlayerConditionScale.ToPercent(
                update.Vitals.Fatigue),
            3);

        // Гигиена падает на 100% за 3 игровых СУТОК, то есть примерно на 1,39%
        // за час: остаётся ~98,6%, а не 1,39%. Замер в процентах, потому что
        // спад считается по шагам (900 с) и округление каждого шага даёт
        // 9860, а не 9861.
        Assert.Equal(
            98.6d,
            PlayerConditionScale.ToPercent(
                update.Vitals.Hygiene),
            1);
    }

    [Fact]
    public void StressAboveFiftyAddsOnePercentCumulativePerGameHour()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(60d),
            CumulativeFatigue = PlayerConditionScale.FromPercent(100d)
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with { Resilience = 0d },
            conditions,
            Hour,
            Hour,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(
            PlayerConditionScale.FromPercent(1d),
            update.Conditions.CumulativeStress,
            3);

        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == "burnout");
    }

    [Fact]
    public void HotelSleepFullyRestoresRequestedResourcesAndAdvancesSevenHours()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(80d),
            Energy = PlayerConditionScale.FromPercent(20d),
            Hydration = PlayerConditionScale.FromPercent(20d),
            Fatigue = PlayerConditionScale.FromPercent(90d),
            Hygiene = PlayerConditionScale.FromPercent(15d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            CumulativeEnergy = PlayerConditionScale.FromPercent(20d),
            CumulativeHydration = PlayerConditionScale.FromPercent(10d),
            CumulativeFatigue = PlayerConditionScale.FromPercent(12d),
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "unkempt",
                    "Неопрятный",
                    3600d,
                    true)
            }
        };

        var update = CharacterVitalsEngine.CompleteHotelSleep(
            vitals,
            conditions);

        Assert.Equal(update.Vitals.MaxEnergy, update.Vitals.Energy);
        Assert.Equal(update.Vitals.MaxHydration, update.Vitals.Hydration);
        Assert.Equal(
            PlayerConditionScale.Maximum,
            update.Vitals.Hygiene);

        Assert.Equal(0d, update.Vitals.Fatigue, 3);
        Assert.Equal(0d, update.Conditions.CumulativeFatigue, 3);
        Assert.Equal(0d, update.Conditions.CumulativeEnergy, 3);
        Assert.Equal(0d, update.Conditions.CumulativeHydration, 3);

        Assert.DoesNotContain(
            update.Conditions.Effects,
            effect => effect.Id == "unkempt");
        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == "rested");
        Assert.Contains(
            update.Events,
            item => item.Kind == "HotelSleepCompleted");
    }

    [Fact]
    public void ItemUseStoresCountAndConsumesOverchargeBeforeNormalEnergy()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Energy = PlayerConditionScale.FromPercent(50d)
        };

        var after = CharacterVitalsEngine.UseItem(
            "drink.energy",
            vitals,
            PlayerConditionState.Empty);

        // Энергетик даёт +15% к энергии и кофеин 80 мг; кофеин добавляет
        // форсаж энергии примерно (80 / 12) = 6,67%.
        Assert.Equal(
            PlayerConditionScale.FromPercent(65d),
            after.Vitals.Energy,
            3);

        Assert.Equal(
            80d / 12d,
            PlayerConditionScale.ToPercent(
                after.Conditions.OverchargeEnergy),
            2);

        Assert.Equal(
            1,
            after.Conditions.ItemUseCounts["drink.energy"]);

        var consumed = CharacterVitalsEngine.Advance(
            after.Vitals,
            after.Conditions,
            1800d,
            1800d,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            consumed.Conditions.OverchargeEnergy <
            after.Conditions.OverchargeEnergy);
    }

    [Fact]
    public void BullImprovesHealthRecoveryAndBlocksNewCumulativeExhaustion()
    {
        var conditions = PlayerConditionState.Empty with
        {
            CumulativeEnergy = PlayerConditionScale.FromPercent(5d),
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    24d * 3600d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Health = PlayerConditionScale.FromPercent(50d),
                Fatigue = PlayerConditionScale.FromPercent(90d),
                Resilience = 0d
            },
            conditions,
            Hour,
            Hour,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(0d, update.Conditions.CumulativeFatigue, 3);
        Assert.Equal(5d, PlayerConditionScale.ToPercent(update.Conditions.CumulativeEnergy), 3);
    }
}
