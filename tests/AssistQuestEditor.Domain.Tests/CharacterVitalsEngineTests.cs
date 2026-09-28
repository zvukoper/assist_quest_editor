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

        Assert.Equal(
            PlayerConditionScale.FromPercent(100d / 4d),
            update.Vitals.Energy,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(100d - 50d),
            update.Vitals.Hydration,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(100d / 18d),
            update.Vitals.Fatigue,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(
                100d / 72d),
            update.Vitals.Hygiene,
            3);
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

        Assert.Equal(
            PlayerConditionScale.FromPercent(70d),
            after.Vitals.Energy,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(10d),
            after.Conditions.OverchargeEnergy,
            3);

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
