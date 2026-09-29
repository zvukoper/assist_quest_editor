using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class CharacterVitalsRegressionTests
{
    private const double Hour = 3600d;

    [Fact]
    public void RuntimeTickAccumulatesSubPercentResourceChanges()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Resilience = PlayerConditionScale.FromPercent(0d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            gameSeconds: 60d,
            realSeconds: 60d,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            update.Vitals.Energy < vitals.Energy,
            "расход энергии не должен теряться из-за округления 250-мс тиков");
        Assert.True(
            update.Vitals.Hydration < vitals.Hydration,
            "расход жидкости не должен теряться из-за округления 250-мс тиков");
        Assert.True(
            update.Vitals.Fatigue > vitals.Fatigue,
            "усталость должна расти в реальном тике симуляции");
        Assert.True(
            update.Vitals.Hygiene < vitals.Hygiene,
            "гигиена должна медленно снижаться даже за короткую сессию");
    }

    [Fact]
    public void RatesAreReportedInVisibleInternalUnitsPerGameMinute()
    {
        var rates = CharacterVitalsEngine.RatesFrom(
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            moving: true,
            sleeping: false,
            gameHourOfDay: 12d);

        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                -CharacterVitalsEngine
                    .EnergyConsumptionPercentPerHour(moving: true) /
                60d),
            rates.EnergyPerGameMinute,
            6);

        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                -CharacterVitalsEngine
                    .HydrationConsumptionPercentPerHour(moving: true) /
                60d),
            rates.HydrationPerGameMinute,
            6);

        Assert.True(rates.EnergyPerGameMinute < -1d);
        Assert.True(rates.HydrationPerGameMinute < -1d);
        Assert.True(rates.FatiguePerGameMinute > 1d);
    }

    [Fact]
    public void CaffeineHasLoadDailyCounterAndSmoothDecay()
    {
        var used = CharacterVitalsEngine.UseItem(
            "drink.coffee",
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            gameHourOfDay: 12d);

        Assert.Equal(95d, used.Conditions.CaffeineLoadMg, 6);
        Assert.Equal(95d, used.Conditions.CaffeineDailyMg, 6);
        Assert.True(used.Conditions.OverchargeEnergy > 0d);

        var afterFiveHours = CharacterVitalsEngine.Advance(
            used.Vitals,
            used.Conditions,
            gameSeconds: 5d * Hour,
            realSeconds: 5d * Hour,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.InRange(
            afterFiveHours.Conditions.CaffeineLoadMg,
            45d,
            50d);
        Assert.Equal(95d, afterFiveHours.Conditions.CaffeineDailyMg, 6);
    }

    [Fact]
    public void CaffeineDailyOveruseAddsTemporaryDebuffWithoutBlockingGameplay()
    {
        var vitals = PlayerVitalsState.Default;
        var conditions = PlayerConditionState.Empty;

        for (var i = 0; i < 5; i++)
        {
            var update = CharacterVitalsEngine.UseItem(
                "drink.coffee",
                vitals,
                conditions,
                gameHourOfDay: 12d);
            vitals = update.Vitals;
            conditions = update.Conditions;
        }

        Assert.Equal(475d, conditions.CaffeineDailyMg, 6);
        Assert.Contains(
            conditions.Effects,
            effect => effect.Id.Equals(
                "caffeine_overuse",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(
            PlayerConditionScale.ToPercent(conditions.Stress) > 0d);
    }

    [Fact]
    public void CatalogContainsMechanicallyImplementedFoodDrinksSupplementsAndSubstances()
    {
        var catalog = ItemCatalogFactory.CreateStarter();
        var ids = catalog
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = new[]
        {
            "drink.coffee",
            "drink.espresso",
            "drink.black_tea",
            "drink.green_tea",
            "drink.cola",
            "drink.energy",
            "food.oatmeal",
            "food.kefir",
            "food.soup",
            "food.dark_chocolate",
            "supplement.multivitamin",
            "mineral.magnesium",
            "adaptogen.ashwagandha",
            "medicine.valerian",
            "smoke.cigarette",
            "drink.beer"
        };

        foreach (var id in expected)
            Assert.Contains(id, ids);

        var knownItems = expected
            .Append("water.bottle")
            .Append("food.milk")
            .Append("drink.lemon_tea")
            .Append("tea.herbal")
            .Append("vitamin.c")
            .Append("adaptogen.tincture")
            .Append("painkiller")
            .Append("soap")
            .Append("skin.ointment");

        foreach (var id in knownItems)
        {
            var result = CharacterVitalsEngine.UseItem(
                id,
                PlayerVitalsState.Default,
                PlayerConditionState.Empty,
                gameHourOfDay: 12d);

            Assert.DoesNotContain(
                result.Events,
                item => item.Kind.Equals(
                    "UnknownItem",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ExcessStressCumulativeDoesNotGrowUnderBull()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(80d),
            CumulativeFatigue = PlayerConditionScale.FromPercent(100d),
            Effects =
            [
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    24d * Hour,
                    false)
            ]
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Resilience = PlayerConditionScale.FromPercent(0d)
            },
            conditions,
            gameSeconds: Hour,
            realSeconds: Hour,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(
            0d,
            update.Conditions.CumulativeStress,
            6);
    }
}
