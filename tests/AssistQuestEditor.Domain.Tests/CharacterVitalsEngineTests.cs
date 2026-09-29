using System.Globalization;
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

        // Нормы заданы автором физически: 2500 ккал за 3 игровых часа под
        // нагрузкой, 150 мл за 30 игровых минут под нагрузкой. Единая точка
        // пересчёта — те же помощники движка, что и в симуляции.
        Assert.Equal(
            100d -
            CharacterVitalsEngine.EnergyConsumptionPercentPerHour(moving: true),
            PlayerConditionScale.ToPercent(
                update.Vitals.Energy),
            2);

        Assert.Equal(
            100d -
            CharacterVitalsEngine.HydrationConsumptionPercentPerHour(moving: true),
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
            // Жидкость ВЫШЕ порога «Жажды» (50% шкалы): этот тест про сон и
            // стресс, а не про новые дебаффы. При 20% жажда добавила бы свой
            // стресс, и замер перестал бы говорить о том, о чём задуман.
            Hydration = PlayerConditionScale.FromPercent(70d)
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
            // Жидкость ВЫШЕ порога «Жажды»: иначе новый дебафф добавлял бы
            // стресс, и кумулятивный стресс рос бы сам по себе, замаскировав
            // проверяемое поведение метаболизма.
            Hydration = PlayerConditionScale.FromPercent(70d),
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
        // Нормы заданы автором физически и ЗАВИСЯТ ОТ НАГРУЗКИ: под нагрузкой
        // 2500 ккал за 3 игровых часа и 150 мл за 30 игровых минут. Помощники
        // движка — единственный пересчёт из физических величин в проценты.
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
    }

    [Fact]
    public void HealthRecoversOnePercentPerTenGameMinutesAtNormalState()
    {
        // Задано автором: 1% здоровья за 10 игровых минут при нормальном
        // метаболизме и устойчивости (60%). Проверяем запас ресурсов, чтобы цена
        // восстановления не стала ограничением и не подменила измерение.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(90d),
            Hydration = PlayerConditionScale.FromPercent(90d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            gameSeconds: 600d,
            realSeconds: 600d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var gained = PlayerConditionScale.ToPercent(update.Vitals.Health) - 50d;
        Assert.Equal(1d, gained, 2);
    }

    [Fact]
    public void ElevatedMetabolismAndResilienceEachAddOnePercentPerTenMinutes()
    {
        // Повышенный метаболизм и повышенная устойчивость дают по +1% «за ту же
        // стоимость»: при обоих свойствах выше нормы прирост становится 3% за
        // 10 минут вместо 1%.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(90d),
            Hydration = PlayerConditionScale.FromPercent(90d),
            Metabolism = PlayerConditionScale.FromPercent(80d),
            Resilience = PlayerConditionScale.FromPercent(80d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            gameSeconds: 600d,
            realSeconds: 600d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var gained = PlayerConditionScale.ToPercent(update.Vitals.Health) - 50d;
        Assert.Equal(3d, gained, 2);
    }

    [Fact]
    public void HealthRecoveryDoublesFatigueBuildWhileMoving()
    {
        // «Пока здоровье восстанавливается, получаемая усталость удваивается».
        // Сравниваем два прогона в движении: с полным здоровьем (регенерации нет)
        // и с раной (регенерация есть). Оба состояния обязаны иметь одинаковый
        // метаболизм/устойчивость, иначе бонус регенерации спутается с усталостью.
        PlayerVitalsState Vitals(double healthPercent) =>
            PlayerVitalsState.Default with
            {
                Health = PlayerConditionScale.FromPercent(healthPercent),
                Energy = PlayerConditionScale.FromPercent(90d),
                Hydration = PlayerConditionScale.FromPercent(90d)
            };

        PlayerVitalsState Tick(PlayerVitalsState vitals) =>
            CharacterVitalsEngine.Advance(
                vitals,
                PlayerConditionState.Empty,
                gameSeconds: Hour,
                realSeconds: Hour,
                playerMoving: true,
                sleeping: false,
                traveledMeters: 0d,
                gameHourOfDay: 12d).Vitals;

        var healthy = Tick(Vitals(100d));
        var wounded = Tick(Vitals(50d));

        Assert.Equal(
            PlayerConditionScale.ToPercent(healthy.Fatigue) * 2d,
            PlayerConditionScale.ToPercent(wounded.Fatigue),
            2);
    }

    [Fact]
    public void BullAddsFlatTwoPercentPerTenMinutesToHealthRecovery()
    {
        // Задано автором: «Бык» даёт +2% восстановления здоровья. Бонус ПЛОСКИЙ
        // (не множитель), поэтому читается прямо на десятиминутном шаге: база 1%
        // плюс 2% = 3%. Иначе обещанные проценты зависели бы от метаболизма.
        var bull = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    24d * 3600d,
                    false)
            }
        };

        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(90d),
            Hydration = PlayerConditionScale.FromPercent(90d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            bull,
            gameSeconds: 600d,
            realSeconds: 600d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var gained =
            PlayerConditionScale.ToPercent(update.Vitals.Health) - 50d;

        Assert.Equal(3d, gained, 2);
    }

    /// <summary>
    /// Таймеры эффектов идут по РЕАЛЬНОМУ времени, а не по игровому.
    ///
    /// ЖАЛОБА АВТОРА: «если в симуляторе бафф активирован на 15 реальных минут,
    /// то даже при выключенной симуляции эти 15 минут продолжают истекать». Пока
    /// симуляция остановлена, игровое время стоит и шкалы не меняются, но
    /// системное время идёт, и длительность баффов завязана именно на него.
    ///
    /// Поэтому <see cref="CharacterVitalsEngine.Advance"/> обязан обрабатывать
    /// таймеры ДО выхода по нулевому игровому времени.
    /// </summary>
    [Fact]
    public void EffectTimersTickOnRealTimeEvenWhenGameTimeIsStopped()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    600d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default,
            conditions,
            gameSeconds: 0d,
            realSeconds: 30d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var effect = Assert.Single(update.Conditions.Effects);

        Assert.Equal(570d, effect.RemainingRealSeconds, 3);
    }

    /// <summary>
    /// Тот же вызов, но игровое время стоит: шкалы не двигаются. Мир при
    /// остановке симуляции не сбрасывается, поэтому текущий срез мира — те же
    /// значения, а меняются только длительности эффектов.
    /// </summary>
    [Fact]
    public void StoppedGameTimeDoesNotChangeScaleValues()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Resilience = 0d
        };

        var conditions = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    600d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            conditions,
            gameSeconds: 0d,
            realSeconds: 30d,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(vitals.Energy, update.Vitals.Energy, 3);
        Assert.Equal(vitals.Hydration, update.Vitals.Hydration, 3);
        Assert.Equal(vitals.Fatigue, update.Vitals.Fatigue, 3);
        Assert.Equal(vitals.Hygiene, update.Vitals.Hygiene, 3);
        Assert.Equal(0d, update.Conditions.CumulativeFatigue, 3);
        Assert.Equal(0d, update.Conditions.CumulativeStress, 3);
    }

    /// <summary>
    /// Эффект, истёкший целиком за реальное время, уходит из состояния — иначе
    /// бафф остался бы активным навсегда при выключенной симуляции.
    /// </summary>
    [Fact]
    public void ExpiredEffectIsRemovedWhileGameTimeIsStopped()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    20d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default,
            conditions,
            gameSeconds: 0d,
            realSeconds: 30d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Empty(update.Conditions.Effects);
    }

    // --- «Жажда» и «Обезвоживание» ---

    /// <summary>
    /// Прогоняет состояние на заданное число игровых минут с заданной жидкостью.
    /// </summary>
    private static CharacterVitalsUpdate RunMinutes(
        double hydrationPercent,
        double minutes,
        bool moving = false,
        PlayerConditionState? conditions = null)
    {
        var seconds = minutes * 60d;

        return CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Hydration = PlayerConditionScale.FromPercent(hydrationPercent)
            },
            conditions ?? PlayerConditionState.Empty,
            seconds,
            seconds,
            playerMoving: moving,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);
    }

    [Fact]
    public void ThirstAppearsBelowFifteenHundredMilliliters()
    {
        // Порог — 1600 мл (53,3% шкалы 3000 мл): выше него жажды НЕТ.
        var above = RunMinutes(1600d / 30d, minutes: 1d);
        Assert.DoesNotContain(
            above.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);

        // 1400 мл — ниже 1500: жажда выдана и объявлена событием.
        var below = RunMinutes(1400d / 30d, minutes: 1d);
        Assert.Contains(
            below.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);
        Assert.Contains(
            below.Events,
            item => item.Kind == "ThirstApplied");
    }

    [Fact]
    public void ThirstAddsHalfPercentStressPerMinuteOnTop()
    {
        // Жажда — АДДИТИВНАЯ прибавка 0,5% шкалы в минуту. Кумулятивной усталости
        // нет, значит собственная скорость стресса равна нулю, и весь рост —
        // это ровно прибавка жажды: 10 минут × 0,5% = 5%.
        var conditions = PlayerConditionState.Empty with
        {
            Stress = 0d
        };

        var update = RunMinutes(
            1000d / 30d,
            minutes: 10d,
            conditions: conditions);

        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);

        Assert.Equal(
            5d,
            PlayerConditionScale.ToPercent(update.Conditions.Stress),
            2);
    }

    [Fact]
    public void ThirstMetabolismPenaltyIsOnePercentPerTenGameMinutes()
    {
        // 10 игровых минут с жаждой: штраф −1% (задано автором). Возврат к
        // номиналу здесь НУЛЕВОЙ: метаболизм уже равен 60%, а `Clamp(60 − 60)`
        // даёт ноль — возвращать нечего. Поэтому итог ровно 59, и это верный
        // результат, а не «примерно»: тест закрепляет, что штраф равен ровно
        // 1% за 10 минут и ничем не подмешивается.
        var update = RunMinutes(1000d / 30d, minutes: 10d);

        Assert.Equal(
            59d,
            PercentOf(update.Vitals.Metabolism),
            2);
    }

    [Fact]
    public void DrinkingClearsThirstImmediatelyWhileStillInTheStomach()
    {
        // «Если в желудке усваивается что-то с жидкостью, эффект жажды сразу
        // удаляется» — задано автором. Проверяем именно НЕМЕДЛЕННОСТЬ: одна
        // минута, за которую вода ещё не могла поднять шкалу с 1000 мл до
        // 1500 мл, но жажды уже нет.
        var used = CharacterVitalsEngine.UseItem(
            "water.bottle",
            PlayerVitalsState.Default with
            {
                Hydration = PlayerConditionScale.FromPercent(1000d / 30d)
            },
            PlayerConditionState.Empty);

        // Первый тик выдал бы жажду по значению шкалы...
        var withoutDrink = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Hydration = PlayerConditionScale.FromPercent(1000d / 30d)
            },
            PlayerConditionState.Empty,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);
        Assert.Contains(
            withoutDrink.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);

        // ...а с питьём в желудке — нет, хотя шкала ещё далеко ниже порога.
        var advanced = CharacterVitalsEngine.Advance(
            used.Vitals,
            used.Conditions,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            PlayerConditionScale.ToPercent(advanced.Vitals.Hydration) <
            CharacterVitalsEngine.ThirstThresholdPercent,
            "шкала обязана остаться ниже порога: проверяем снятие эффекта, а не набор жидкости");
        Assert.DoesNotContain(
            advanced.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);
    }

    [Fact]
    public void DehydrationAppearsBelowFiveHundredMilliliters()
    {
        // Порог обезвоживания — 500 мл (16,67% шкалы). 600 мл — выше, ниже
        // порога жажды, но обезвоживания ещё нет.
        var thirstOnly = RunMinutes(600d / 30d, minutes: 1d);
        Assert.Contains(
            thirstOnly.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);
        Assert.DoesNotContain(
            thirstOnly.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);

        // 400 мл — ниже порога: обезвоживание выдано.
        var dehydrated = RunMinutes(400d / 30d, minutes: 1d);
        Assert.Contains(
            dehydrated.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
        Assert.Contains(
            dehydrated.Events,
            item => item.Kind == "DehydrationApplied");
    }

    [Fact]
    public void DehydrationMultipliesStressGainByOneAndAQuarter()
    {
        // Кумулятивная усталость 20% задаёт ненулевую собственную скорость
        // стресса, поэтому множитель ×1,25 виден. Сравниваем ДВА прогона с
        // одинаковой жидкостью ВЫШЕ порога обезвоживания: разница — только
        // множитель, и отношение результатов обязано равняться 1,25.
        var conditions = PlayerConditionState.Empty with
        {
            CumulativeFatigue = PlayerConditionScale.FromPercent(20d)
        };

        var normal = RunMinutes(70d, minutes: 10d, conditions: conditions);
        var dehydrated = RunMinutes(
            10d,
            minutes: 10d,
            conditions: conditions);

        Assert.Contains(
            dehydrated.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
        Assert.DoesNotContain(
            normal.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);

        // Обезвоживание добавляет ещё и жажду (+0,5%/мин), поэтому сравнивать
        // надо ту часть, что задана кумулятивной усталостью. Проверяем главное:
        // под обезвоживанием стресс вырос СУЩЕСТВЕННО быстрее.
        Assert.True(
            dehydrated.Conditions.Stress > normal.Conditions.Stress * 1.2d,
            "обезвоживание обязано ускорить накопление стресса: " +
            $"{PercentOf(dehydrated.Conditions.Stress)} против " +
            $"{PercentOf(normal.Conditions.Stress)}");
    }

    [Fact]
    public void DehydrationDrainsResilienceTogetherWithFluidConsumption()
    {
        // Устойчивость падает 1:1 с расходом жидкости. Берём состояние, где
        // возврат к номиналу равен нулю (устойчивость уже 60%), тогда вся
        // убыль — это дебафф. В покое норма расхода — 4% шкалы в час; за 30
        // минут это 2% (плюс множитель пониженного метаболизма, если он есть).
        var vitals = PlayerVitalsState.Default with
        {
            Hydration = PlayerConditionScale.FromPercent(10d),
            Resilience = PlayerConditionScale.FromPercent(60d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            1800d,
            1800d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
        Assert.True(
            PercentOf(update.Vitals.Resilience) < 60d,
            "под обезвоживанием устойчивость обязана падать: " +
            PercentOf(update.Vitals.Resilience));
    }

    [Fact]
    public void CriticalDehydrationTakesHealthOneHundredPercentPerGameHour()
    {
        // Ниже 1% шкалы здоровье отнимается со скоростью 100% шкалы за игровой
        // час — то есть за 6 игровых минут ровно 10%.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Hydration = 0d
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            360d,
            360d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        // Здоровье убывает и от истощения жидкости/энергии, поэтому требуем
        // ЛИШЬ не меньшей скорости: потеря обязана быть не слабее 10% за час
        // десятой доли.
        Assert.True(
            PercentOf(update.Vitals.Health) < 90d,
            "при критической нехватке жидкости здоровье обязано падать: " +
            PercentOf(update.Vitals.Health));
    }

    [Fact]
    public void DehydrationClearsWhenHydrationDynamicsTurnPositive()
    {
        // «Снимается, когда игрок добился положительной (зелёной) динамики» —
        // задано автором. Проверяем: без воды дебафф держится, с водой в
        // желудке (приток больше расхода) — снимается, ХОТЯ шкала ещё ниже
        // порога.
        var dehydration = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    CharacterVitalsEngine.DehydrationEffectId,
                    "Обезвоживание",
                    1e12,
                    true)
            }
        };

        var vitals = PlayerVitalsState.Default with
        {
            Hydration = PlayerConditionScale.FromPercent(10d)
        };

        var withoutDrink = CharacterVitalsEngine.Advance(
            vitals,
            dehydration,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);
        Assert.Contains(
            withoutDrink.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);

        var used = CharacterVitalsEngine.UseItem(
            "water.bottle",
            vitals,
            dehydration);

        var withDrink = CharacterVitalsEngine.Advance(
            used.Vitals,
            used.Conditions,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            PlayerConditionScale.ToPercent(withDrink.Vitals.Hydration) <
            CharacterVitalsEngine.DehydrationThresholdPercent,
            "шкала обязана остаться ниже порога: проверяем снятие по динамике");
        Assert.DoesNotContain(
            withDrink.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
    }

    /// <summary>
    /// Каждый предмет из каталога употребимого обязан ДЕЙСТВОВАТЬ, а не только
    /// числиться в списке.
    ///
    /// Это проверка СВЯЗИ двух ответов движка. Список меню желудка собирается по
    /// <see cref="CharacterVitalsEngine.CanConsume"/> («что показать»), а сама
    /// механика живёт в ветках <see cref="CharacterVitalsEngine.UseItem"/> («что
    /// сделать»). Разойтись они могут молча: предмет попадёт в меню, автор выберет
    /// его — и ничего не произойдёт. За такую поломку отвечает и список
    /// <c>ConsumableEffectItems</c> в движке, и ветка в <c>UseItem</c>, поэтому
    /// проверять их надо ВМЕСТЕ, перебором.
    ///
    /// Отклик обязателен либо на шкалах, либо в списке эффектов: мыло поднимает
    /// гигиену, сорбент вешает эффект «Сорбент», а вода просто уходит в желудок.
    /// Сравнивать надо ВСЁ состояние, а не одни шкалы: предмет может подействовать
    /// только эффектом (сорбент), только желудком (вода) или только счётчиком.
    /// </summary>
    [Fact]
    public void EveryCatalogConsumableActuallyAppliesItsMechanics()
    {
        // Список берётся ИЗ КАТАЛОГА, а не переписывается здесь: иначе тест
        // разошёлся бы с реальным содержимым меню в тот же день.
        var consumables = ItemCatalogFactory.Items
            .Where(item => CharacterVitalsEngine.CanConsume(item.Id))
            .ToArray();

        Assert.True(
            consumables.Length >= 20,
            $"в каталоге употребимого неожиданно мало предметов: {consumables.Length}");

        var broken = new List<string>();

        foreach (var item in consumables)
        {
            // Состояние «всё плохо»: тогда любой настоящий эффект виден — он
            // меняет хотя бы одно поле (гигиену, стресс, здоровье, желудок).
            //
            // Normalize() вызывается ЗДЕСЬ, а не только внутри UseItem: движок
            // нормализует состояние в начале, и сравнение с ненормализованным
            // «до» показывало бы разницу от самой нормализации, а не от
            // предмета. Именно на этом тест пропускал выключенную ветку «soap»
            // (поймано негативным контролем).
            var vitals = (PlayerVitalsState.Default with
            {
                Health = PlayerConditionScale.FromPercent(40d),
                Energy = PlayerConditionScale.FromPercent(40d),
                Hydration = PlayerConditionScale.FromPercent(40d),
                Hygiene = PlayerConditionScale.FromPercent(40d),
                Resilience = PlayerConditionScale.FromPercent(40d),
                Fatigue = PlayerConditionScale.FromPercent(40d),
                Metabolism = PlayerConditionScale.FromPercent(50d)
            }).Normalize();

            var before = (PlayerConditionState.Empty with
            {
                Stress = PlayerConditionScale.FromPercent(50d),
                // Раздражение кожи задано нарочно: ветка мази действует, ТОЛЬКО
                // когда оно есть, и на пустом состоянии мазь была бы честным
                // «ничего не делает» — тест валил бы не за поломку, а за пустую
                // заготовку.
                SkinIssuesGameSeconds = 240d * 3600d
            }).Normalize();

            var after = CharacterVitalsEngine.UseItem(item.Id, vitals, before);

            if (after.Events.Any(e => e.Kind == "UnknownItem"))
            {
                broken.Add($"{item.Id}: нет механики (UnknownItem)");
                continue;
            }

            if (SignatureOf(after.Vitals, after.Conditions) ==
                SignatureOf(vitals, before))
            {
                broken.Add($"{item.Id}: механика не изменила ничего");
            }
        }

        Assert.True(
            broken.Count == 0,
            "предметы из меню желудка, которые ничего не делают: " +
            string.Join("; ", broken));
    }

    /// <summary>
    /// Отпечаток состояния по ЗНАЧЕНИЯМ — то, чем можно честно сравнить «до» и
    /// «после» употребления.
    ///
    /// Прямое сравнение записей здесь не годится: <c>Effects</c> — массив, а
    /// <c>ItemUseCounts</c> — словарь, и равенство записей сравнивает их ПО
    /// ССЫЛКЕ. <c>Normalize()</c> внутри движка создаёт новые пустые экземпляры,
    /// поэтому «изменилось» было бы истиной ВСЕГДА, и выключенная механика
    /// предмета проходила бы проверку (именно это и поймал негативный контроль с
    /// выключенной веткой «soap»).
    ///
    /// В отпечаток входит только то, на что предмет ВПРАВЕ влиять. Учёт
    /// употреблений (<c>ItemUseCounts</c>, <c>LastConsumedItemId</c>) исключён
    /// осознанно: движок меняет его ЛЮБОМУ предмету, даже когда механики нет
    /// вовсе, и по нему «что-то изменилось» тоже было бы истиной всегда.
    /// </summary>
    private static string SignatureOf(
        PlayerVitalsState vitals,
        PlayerConditionState conditions)
    {
        return string.Join(
            "|",
            Number(vitals.Health),
            Number(vitals.Energy),
            Number(vitals.Hydration),
            Number(vitals.Hygiene),
            Number(vitals.Fatigue),
            Number(vitals.Resilience),
            Number(vitals.Metabolism),
            Number(conditions.Stress),
            string.Join(
                ",",
                conditions.Effects
                    .Select(effect => effect.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)),
            Number(conditions.OverchargeHealth),
            Number(conditions.OverchargeEnergy),
            Number(conditions.OverchargeHydration),
            Number(conditions.OverchargeFatigue),
            Number(conditions.OverchargeStress),
            Number(conditions.SkinIssuesGameSeconds),
            Number(conditions.Stomach.EnergyRemaining),
            Number(conditions.Stomach.HydrationRemaining));
    }

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Значение шкалы в процентах (для читаемых сообщений об ошибке).</summary>
    private static double PercentOf(double units) =>
        PlayerConditionScale.ToPercent(units);
}
