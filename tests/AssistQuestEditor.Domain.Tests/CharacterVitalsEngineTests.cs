using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class CharacterVitalsEngineTests
{
    private const double Hour = 3600d;

    /// <summary>Проценты → единицы шкалы без округления (для точных формул).</summary>
    private static double Units(double percent) =>
        percent * PlayerConditionScale.UnitsPerPercent;

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

        // 100% за EnergyConsumptionHours игровых часов. Расход выведен из
        // физических величин шкал (5000 ккал и 15000 мл): 100/30 = 3,33% в час.
        Assert.Equal(
            100d - 100d / CharacterVitalsEngine.EnergyConsumptionHours,
            PlayerConditionScale.ToPercent(
                update.Vitals.Energy),
            2);

        // 100% за HydrationConsumptionHours игровых часов: 100/90 = 1,11% в час.
        Assert.Equal(
            100d - 100d / CharacterVitalsEngine.HydrationConsumptionHours,
            PlayerConditionScale.ToPercent(
                update.Vitals.Hydration),
            2);

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
    public void HotelSleepClearsMostOfStress()
    {
        // ЖАЛОБА АВТОРА: «после ночлега в гостинице метаболизм упал до 0, а
        // стресс остался 44». Обычный стресс обязан сниматься сном, иначе
        // единственным способом его убрать остаются предметы.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Fatigue = PlayerConditionScale.FromPercent(90d),
            Energy = PlayerConditionScale.FromPercent(20d),
            Hydration = PlayerConditionScale.FromPercent(20d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(44d)
        };

        var update = CharacterVitalsEngine.CompleteHotelSleep(vitals, conditions);

        // 100% стресса уходит за FullSleepStressClearHours часов полноценного
        // сна: за 7 часов при 6-часовой норме шкала обнуляется.
        Assert.Equal(0d, update.Conditions.Stress, 3);

        // Кумулятивный стресс неприкосновенен: его снимает только отпуск.
        Assert.Equal(0d, update.Conditions.CumulativeStress, 6);
    }

    [Fact]
    public void FullSleepClearsStressFasterThanRegularSleep()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(30d)
        };

        // Полноценный сон снимает 100% / 6 часов за час, то есть ставкой, а не
        // долей остатка: за 4 часа уходит 2/3 шкалы, и стресс 30% обнуляется.
        var full = CharacterVitalsEngine.Sleep(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            conditions,
            hours: 4,
            fullSleep: true);

        Assert.Equal(
            0d,
            full.Conditions.Stress,
            3);

        // А за один час — ровно шестая часть шкалы.
        var oneHour = CharacterVitalsEngine.Sleep(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            conditions,
            hours: 1,
            fullSleep: true);

        Assert.Equal(
            Units(30d) - Units(100d) / CharacterVitalsEngine.FullSleepStressClearHours,
            oneHour.Conditions.Stress,
            1);
    }

    [Fact]
    public void StressDoesNotFallToZeroFromShortRegularSleep()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(100d)
        };

        // Обычный сон снимает стресс в 2,5 раза медленнее: за 6 часов уйдёт
        // 100 / (6 · 2,5) = 6,67%.
        var regular = CharacterVitalsEngine.Sleep(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            conditions,
            hours: 6,
            fullSleep: false);

        var removed = Units(100d) - regular.Conditions.Stress;

        Assert.Equal(
            Units(100d) / CharacterVitalsEngine.FullSleepStressClearHours /
            CharacterVitalsEngine.RegularSleepStressClearPenalty * 6d,
            removed,
            1);

        Assert.True(
            regular.Conditions.Stress > Units(50d),
            "обычный сон снимает стресс медленно, а не мгновенно: " +
            regular.Conditions.Stress);
    }

    [Fact]
    public void MetabolismDoesNotCollapseWhileOnlyCumulativeStressRemains()
    {
        // ЖАЛОБА АВТОРА: «метаболизм упал до 0 после ночлега». Причина была в
        // том, что гостиница снимает истощение энергии, жидкости и усталости, но
        // НЕ кумулятивный стресс (его снимает только отпуск) — а метаболизм
        // продолжал терять по 2% за КАЖДУЮ шкалу истощения каждые 15 игровых
        // минут, то есть до 32% в час против 1% в час восстановления.
        //
        // Теперь штраф применяется только к шкалам истощения, которые сон
        // реально снимает, поэтому оставшийся кумулятивный стресс метаболизм
        // не обнуляет.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Fatigue = PlayerConditionScale.FromPercent(90d),
            Energy = PlayerConditionScale.FromPercent(20d),
            Hydration = PlayerConditionScale.FromPercent(20d),
            Metabolism = PlayerConditionScale.FromPercent(30d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            CumulativeStress = PlayerConditionScale.FromPercent(5d)
        };

        var update = CharacterVitalsEngine.CompleteHotelSleep(vitals, conditions);

        Assert.Equal(
            PlayerConditionScale.FromPercent(5d),
            update.Conditions.CumulativeStress,
            3);

        // Метаболизм обязан быть ВЫШЕ исходного: он восстанавливается к 60%.
        Assert.True(
            update.Vitals.Metabolism > vitals.Metabolism,
            "после ночлега метаболизм обязан восстанавливаться, а не падать в ноль: " +
            update.Vitals.Metabolism);
    }

    [Fact]
    public void HotelSleepFullyRestoresRequestedResourcesAndAdvancesSevenHours()
    {        var vitals = PlayerVitalsState.Default with
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

        // Энергетик (330 мл, 150 ккал) даёт 3% шкалы энергии и 2,133% шкалы
        // жидкости, но восстановление теперь ПОСТЕПЕННОЕ: единицы уходят в
        // скрытый желудок, а не в шкалу сразу (см. CharacterDigestion). Кофеин
        // добавляет форсаж энергии примерно (80 / 12) = 6,67% мгновенно — он не
        // еда.
        Assert.Equal(
            PlayerConditionScale.FromPercent(50d),
            after.Vitals.Energy,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(150d / 50d),
            after.Conditions.Stomach.EnergyRemaining,
            2);

        // Ставка постоянна и задана физиологией: полный желудок еды уходит за
        // FoodStomachEmptyHours, то есть 5000 единиц в час.
        Assert.Equal(
            PlayerConditionScale.Maximum /
                CharacterDigestion.FoodStomachEmptyHours,
            after.Conditions.Stomach.EnergyPerGameSecond * Hour,
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

    [Fact]
    public void CumulativeFatigueAccrualUsesTripledBaseAndDoubledStepPenalty()
    {
        // Задано автором: база ×3 (было ×2), штраф 0,5 за каждые 5% (было 0,25).
        Assert.Equal(
            CharacterVitalsEngine.CumulativeFatigueAccrualBaseMultiplier,
            CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
                PlayerConditionState.Empty),
            6);
        Assert.Equal(3d, CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
            PlayerConditionState.Empty), 6);

        Assert.Equal(3.5d, CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
            PlayerConditionState.Empty with
            {
                CumulativeFatigue = PlayerConditionScale.FromPercent(5d)
            }), 6);

        Assert.Equal(4d, CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
            PlayerConditionState.Empty with
            {
                CumulativeFatigue = PlayerConditionScale.FromPercent(10d)
            }), 6);
    }

    [Fact]
    public void HealthRecoveryConsumesEnergyOneToOneAndHydrationOneToTwo()
    {
        // Сравниваем ДВА прогона с одинаковыми запасами: здоровье 100% (шкала
        // полна, восстанавливать нечего) и здоровье 50%. Разница в расходе
        // энергии и жидкости — это и есть цена восстановления здоровья, и её
        // отношение обязано быть 1:1 (энергия) и 1:2 (жидкость).
        var full = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Energy = PlayerConditionScale.FromPercent(80d),
            Hydration = PlayerConditionScale.FromPercent(80d),
            Resilience = 0d
        };
        var wounded = full with
        {
            Health = PlayerConditionScale.FromPercent(50d)
        };

        PlayerVitalsState Tick(PlayerVitalsState vitals) =>
            CharacterVitalsEngine.Advance(
                vitals,
                PlayerConditionState.Empty,
                gameSeconds: 900d,
                realSeconds: 900d,
                playerMoving: false,
                sleeping: false,
                traveledMeters: 0d,
                gameHourOfDay: 12d).Vitals;

        var fullAfter = Tick(full);
        var woundedAfter = Tick(wounded);

        var healthGain = woundedAfter.Health - wounded.Health;
        Assert.True(healthGain > 0d, "при 80% энергии и жидкости здоровье обязано расти");

        var energyCost = fullAfter.Energy - woundedAfter.Energy;
        var hydrationCost = fullAfter.Hydration - woundedAfter.Hydration;

        Assert.Equal(
            healthGain * CharacterVitalsEngine.HealthRegenEnergyPerHealthUnit,
            energyCost,
            3);
        Assert.Equal(
            healthGain * CharacterVitalsEngine.HealthRegenHydrationPerHealthUnit,
            hydrationCost,
            3);

        // И сами коэффициенты: 1 единица здоровья = 1 энергия и 2 жидкости.
        Assert.Equal(1d, CharacterVitalsEngine.HealthRegenEnergyPerHealthUnit, 6);
        Assert.Equal(2d, CharacterVitalsEngine.HealthRegenHydrationPerHealthUnit, 6);
    }

    [Fact]
    public void HealthRecoveryRateIsZeroWhenResourcesRunOut()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = 0d,
            Hydration = PlayerConditionScale.FromPercent(80d),
            Resilience = 0d
        };

        var rates = CharacterVitalsEngine.RatesFrom(
            vitals,
            PlayerConditionState.Empty,
            moving: false,
            sleeping: false,
            gameHourOfDay: 12d);

        // Нет энергии — нет и восстановления: «Текущая динамика» обещала бы рост
        // из воздуха, если бы цена ресурсов не была настоящей.
        Assert.Equal(0d, rates.HealthPerGameMinute, 6);
    }

    [Fact]
    public void FullHealthDoesNotConsumeEnergyOrHydrationForRecovery()
    {
        var rates = CharacterVitalsEngine.RatesFrom(
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            moving: true,
            sleeping: false,
            gameHourOfDay: 12d);

        // Здоровье полное — восстанавливать нечего, значит и «платить» не за что:
        // скорости энергии и жидкости остаются чистой жизнедеятельностью.
        //
        // Расход выведен из ФИЗИЧЕСКИХ величин шкал: энергия — 5000 ккал,
        // жидкость — 15000 мл (см. CharacterDigestion), поэтому 100% шкалы
        // тратится за EnergyConsumptionHours / HydrationConsumptionHours часов.
        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                -100d / CharacterVitalsEngine.EnergyConsumptionHours / 60d),
            rates.EnergyPerGameMinute,
            6);
        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                -100d / CharacterVitalsEngine.HydrationConsumptionHours / 60d),
            rates.HydrationPerGameMinute,
            6);
    }
}
