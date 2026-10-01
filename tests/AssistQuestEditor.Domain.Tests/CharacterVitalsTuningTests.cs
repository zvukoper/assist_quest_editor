using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Проверки ФАЙЛА КАЛИБРОВКИ <see cref="CharacterVitalsTuning"/>.
///
/// Зачем они нужны. Калибровка существует ради одной цели: «все числа состояния
/// персонажа меняются в ОДНОМ файле». Стоит кому-то снова написать число прямо в
/// <see cref="CharacterVitalsEngine"/> — и цель потеряна, а расхождение будет
/// незаметным: движок продолжит работать, просто правка калибровки перестанет
/// влиять на поведение.
///
/// Поэтому тесты проверяют не «правильные ли числа», а СВЯЗЬ: псевдоним движка
/// обязан быть равен константе калибровки, а производные проценты — считаться от
/// размера шкалы, а не быть записанными вторым числом.
/// </summary>
public sealed class CharacterVitalsTuningTests
{
    [Fact]
    public void EngineConstantsAreAliasesOfTheTuningFile()
    {
        // Если тест упал — в движке появилось собственное число вместо ссылки на
        // калибровку. Менять баланс нужно в CharacterVitalsTuning.cs.
        Assert.Equal(
            CharacterVitalsTuning.FatigueCriticalPercent,
            CharacterVitalsEngine.FatigueCriticalPercent);
        Assert.Equal(
            CharacterVitalsTuning.StressCriticalPercent,
            CharacterVitalsEngine.StressCriticalPercent);
        Assert.Equal(
            CharacterVitalsTuning.EnergyKilocaloriesMovingPerHour,
            CharacterVitalsEngine.EnergyKilocaloriesMovingPerHour);
        Assert.Equal(
            CharacterVitalsTuning.EnergyKilocaloriesRestingPerHour,
            CharacterVitalsEngine.EnergyKilocaloriesRestingPerHour);
        Assert.Equal(
            CharacterVitalsTuning.HydrationMillilitersMovingPerHour,
            CharacterVitalsEngine.HydrationMillilitersMovingPerHour);
        Assert.Equal(
            CharacterVitalsTuning.HydrationMillilitersRestingPerHour,
            CharacterVitalsEngine.HydrationMillilitersRestingPerHour);
        Assert.Equal(
            CharacterVitalsTuning.SleepConsumptionMultiplier,
            CharacterVitalsEngine.SleepConsumptionMultiplier);
        Assert.Equal(
            CharacterVitalsTuning.ThirstThresholdMilliliters,
            CharacterVitalsEngine.ThirstThresholdMilliliters);
        Assert.Equal(
            CharacterVitalsTuning.DehydrationThresholdMilliliters,
            CharacterVitalsEngine.DehydrationThresholdMilliliters);
        Assert.Equal(
            CharacterVitalsTuning.ThirstStressPercentPerMinute,
            CharacterVitalsEngine.ThirstStressPercentPerMinute);
        Assert.Equal(
            CharacterVitalsTuning.DehydrationStressMultiplier,
            CharacterVitalsEngine.DehydrationStressMultiplier);
        Assert.Equal(
            CharacterVitalsTuning.CriticalHydrationHealthLossPercentPerHour,
            CharacterVitalsEngine.CriticalHydrationHealthLossPercentPerHour);
        Assert.Equal(
            CharacterVitalsTuning.DefaultMetabolismPercent,
            CharacterVitalsEngine.DefaultMetabolismPercent);
        Assert.Equal(
            CharacterVitalsTuning.MetabolismExhaustionPenaltyPerQuarterHour,
            CharacterVitalsEngine.MetabolismExhaustionPenaltyPerQuarterHour);
        Assert.Equal(
            CharacterVitalsTuning.HotelSleepHours,
            CharacterVitalsEngine.HotelSleepHours);
        // Сон: эти два числа однажды уже были ЗАБЫТЫ в движке (остались
        // литералами), поэтому правка калибровки их не меняла. Держим проверку.
        Assert.Equal(
            CharacterVitalsTuning.FullSleepStressClearHours,
            CharacterVitalsEngine.FullSleepStressClearHours);
        Assert.Equal(
            CharacterVitalsTuning.RegularSleepStressClearPenalty,
            CharacterVitalsEngine.RegularSleepStressClearPenalty);
        Assert.Equal(
            CharacterVitalsTuning.FatigueBuildHours,
            CharacterVitalsEngine.FatigueBuildHours);
        Assert.Equal(
            CharacterVitalsTuning.FatigueRestHours,
            CharacterVitalsEngine.FatigueRestHours);
        Assert.Equal(
            CharacterVitalsTuning.FatigueBuildKilometers,
            CharacterVitalsEngine.FatigueBuildKilometers);
    }

    [Fact]
    public void ConditionEngineConstantsAreAliasesOfTheTuningFile()
    {
        // Второй движок условий (PlayerConditionEngine) раньше держал СОБСТВЕННЫЕ
        // копии чисел. Теперь источник один, иначе правка калибровки меняла бы
        // только CharacterVitalsEngine, а подсказки и опыт считались бы по старому.
        Assert.Equal(
            CharacterVitalsTuning.FatigueCriticalPercent,
            PlayerConditionEngine.FatigueCriticalPercent);
        Assert.Equal(
            CharacterVitalsTuning.StressCriticalPercent,
            PlayerConditionEngine.StressCriticalPercent);
        Assert.Equal(
            CharacterVitalsTuning.FatigueBuildHours,
            PlayerConditionEngine.FatigueBuildHours);
        Assert.Equal(
            CharacterVitalsTuning.FatigueRestHours,
            PlayerConditionEngine.FatigueRestHours);
        Assert.Equal(
            CharacterVitalsTuning.FatigueBuildKilometers,
            PlayerConditionEngine.FatigueBuildKilometers);
        Assert.Equal(
            CharacterVitalsTuning.StressPerCumulativeFatiguePercentPerHour,
            PlayerConditionEngine.StressPerCumulativeFatiguePerHour);
        // Истощение копится ДОЛЕЙ ПРИРОСТА шкалы, а не ставкой «% в час»:
        // почасовая ставка начисляла истощение даже при стоящей на месте шкале
        // и пропускала разовые рывки вверх.
        Assert.Equal(
            CharacterVitalsTuning.FatigueExhaustionShare,
            PlayerConditionEngine.FatigueExhaustionShare);
        Assert.Equal(
            CharacterVitalsTuning.FatigueExhaustionBelowFullShare,
            PlayerConditionEngine.FatigueExhaustionBelowFullShare);
        Assert.Equal(
            CharacterVitalsTuning.StressExhaustionShare,
            PlayerConditionEngine.StressExhaustionShare);
        Assert.Equal(
            CharacterVitalsTuning.BurnoutFatigueBuildMultiplier,
            PlayerConditionEngine.BurnoutFatigueBuildMultiplier);
        Assert.Equal(
            CharacterVitalsTuning.BurnoutInstantPenaltyPercent,
            PlayerConditionEngine.BurnoutPenaltyPercent);
        Assert.Equal(
            CharacterVitalsTuning.BurnoutDurationRealSeconds,
            PlayerConditionEngine.BurnoutDurationRealSeconds);
        Assert.Equal(
            CharacterVitalsTuning.RelaxationExperienceMultiplier,
            PlayerConditionEngine.RelaxationExperienceMultiplier);
        Assert.Equal(
            CharacterVitalsTuning.RelaxationDurationRealSeconds,
            PlayerConditionEngine.RelaxationDurationRealSeconds);
        Assert.Equal(
            CharacterVitalsTuning.RestedExperienceMultiplier,
            PlayerConditionEngine.RestedExperienceMultiplier);
        Assert.Equal(
            CharacterVitalsTuning.RestedDurationRealSeconds,
            PlayerConditionEngine.RestedDurationRealSeconds);
    }

    [Fact]
    public void DerivedFatigueRatesAreComputedFromTheTuningFile()
    {
        // Скорости «процент в час» и «процент на километр» — ПРОИЗВОДНЫЕ: если
        // записать их вторым числом, при смене FatigueBuildHours они разъедутся с
        // накоплением в движке.
        Assert.Equal(
            100d / CharacterVitalsTuning.FatigueBuildHours,
            CharacterVitalsTuning.FatigueBuildPercentPerHour,
            10);
        Assert.Equal(
            100d / CharacterVitalsTuning.FatigueRestHours,
            CharacterVitalsTuning.FatigueRestPercentPerHour,
            10);
        Assert.Equal(
            100d / CharacterVitalsTuning.FatigueBuildKilometers,
            CharacterVitalsTuning.FatigueBuildPercentPerKilometer,
            10);
    }

    [Fact]
    public void SleepHoursStayConsistentBetweenHosts()
    {
        // Три числа сна живут в одном месте: полноценный сон, полевой сон и
        // гостиница. Меню игры и движок обязаны брать их ОТТУДА, иначе журнал
        // напишет одно, а симулятор прокрутит другое.
        Assert.Equal(4, CharacterVitalsTuning.FullSleepHours);
        Assert.Equal(6, CharacterVitalsTuning.FieldSleepHours);
        Assert.Equal(
            CharacterVitalsTuning.HotelSleepHours,
            CharacterVitalsEngine.HotelSleepHours);
        Assert.True(
            CharacterVitalsTuning.FullSleepHours <
            CharacterVitalsTuning.FieldSleepHours);
    }

    [Fact]
    public void PlayerVitalsDefaultsComeFromTheTuningFile()
    {
        // «Норма» устойчивости и метаболизма — одно и то же число и в калибровке,
        // и в состоянии персонажа по умолчанию. Иначе новый игрок начинал бы не с
        // той нормы, к которой возвращают дебаффы.
        Assert.Equal(
            CharacterVitalsTuning.DefaultResiliencePercent,
            PlayerConditionScale.ToPercent(
                PlayerVitalsState.Default.Resilience),
            10);
        Assert.Equal(
            CharacterVitalsTuning.DefaultMetabolismPercent,
            PlayerConditionScale.ToPercent(
                PlayerVitalsState.Default.Metabolism),
            10);
    }

    [Fact]
    public void CaffeineDosesLiveInTheTuningFile()
    {
        // Дозы кофеина — часть баланса, а не свойства каталога предметов. Тест
        // ловит возврат таблицы в CharacterConsumableCatalog: тогда правка
        // CharacterItemTuning перестала бы менять эффект напитков.
        Assert.Equal(
            CharacterItemTuning.CaffeineMgCoffee,
            CharacterConsumableCatalog.GetCaffeineMg("drink.coffee"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgEspresso,
            CharacterConsumableCatalog.GetCaffeineMg("drink.espresso"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgBlackTea,
            CharacterConsumableCatalog.GetCaffeineMg("drink.black_tea"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgGreenTea,
            CharacterConsumableCatalog.GetCaffeineMg("drink.green_tea"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgCola,
            CharacterConsumableCatalog.GetCaffeineMg("drink.cola"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgEnergyDrink,
            CharacterConsumableCatalog.GetCaffeineMg("drink.energy"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgDarkChocolate,
            CharacterConsumableCatalog.GetCaffeineMg("food.dark_chocolate"));
        Assert.Equal(
            CharacterItemTuning.CaffeineMgDecafCoffee,
            CharacterConsumableCatalog.GetCaffeineMg("drink.decaf_coffee"));

        // «Кофе без кофеина» слабее кофе — читаемое правило автора.
        Assert.True(
            CharacterItemTuning.CaffeineMgDecafCoffee <
            CharacterItemTuning.CaffeineMgCoffee);
    }

    [Fact]
    public void CaffeineThresholdsAreOrdered()
    {
        // Пороги суточной дозы обязаны идти по возрастанию, иначе «избыток»
        // срабатывал бы раньше «перенапряжения».
        Assert.True(
            CharacterVitalsTuning.CaffeineOveruseDailyMg <
            CharacterVitalsTuning.CaffeineStressDailyMg);
        Assert.True(
            CharacterVitalsTuning.CaffeineStressDailyMg <
            CharacterVitalsTuning.CaffeineExcessDailyMg);
        Assert.True(
            CharacterVitalsTuning.CaffeineHalfLifeGameHours > 0d);
        Assert.True(
            CharacterVitalsTuning.CaffeineWithdrawalLoadAfterMg <
            CharacterVitalsTuning.CaffeineWithdrawalLoadBeforeMg);
    }

    [Fact]
    public void DigestionConstantsAreAliasesOfTheTuningFile()
    {
        Assert.Equal(
            CharacterVitalsTuning.EnergyScaleKilocalories,
            CharacterDigestion.EnergyScaleKilocalories);
        Assert.Equal(
            CharacterVitalsTuning.HydrationScaleMilliliters,
            CharacterDigestion.HydrationScaleMilliliters);
        Assert.Equal(
            CharacterVitalsTuning.StomachVolumeLiters,
            CharacterDigestion.StomachVolumeLiters);
        Assert.Equal(
            CharacterVitalsTuning.HydrationAbsorptionLitersPerHour,
            CharacterDigestion.HydrationAbsorptionLitersPerHour);
        Assert.Equal(
            CharacterVitalsTuning.FoodStomachEmptyHours,
            CharacterDigestion.FoodStomachEmptyHours);
        Assert.Equal(
            CharacterVitalsTuning.ReducedMetabolismSpeedFactor,
            CharacterDigestion.ReducedMetabolismSpeedFactor);
    }

    [Fact]
    public void PhysicalScaleSizesMatchTheAuthorsRequest()
    {
        // Задано автором: 5000 ккал и 3000 мл. Эти числа показываются игроку как
        // потолок шкалы, поэтому они закреплены отдельной проверкой.
        Assert.Equal(5000d, CharacterVitalsTuning.EnergyScaleKilocalories);
        Assert.Equal(3000d, CharacterVitalsTuning.HydrationScaleMilliliters);
    }

    [Fact]
    public void ThirstAndDehydrationThresholdsAreDerivedFromTheScale()
    {
        // Пороги заданы в МИЛЛИЛИТРАХ, а движок работает в процентах: процент
        // обязан быть ВЫВЕДЕН, а не записан вторым числом — иначе при смене
        // размера шкалы дебафф включался бы не на том уровне.
        Assert.Equal(
            CharacterVitalsTuning.ThirstThresholdMilliliters /
                CharacterVitalsTuning.HydrationScaleMilliliters *
                100d,
            CharacterVitalsTuning.ThirstThresholdPercent);
        Assert.Equal(
            CharacterVitalsTuning.DehydrationThresholdMilliliters /
                CharacterVitalsTuning.HydrationScaleMilliliters *
                100d,
            CharacterVitalsTuning.DehydrationThresholdPercent);
        Assert.Equal(
            CharacterVitalsTuning.ThirstThresholdPercent,
            CharacterVitalsEngine.ThirstThresholdPercent);
        Assert.Equal(
            CharacterVitalsTuning.DehydrationThresholdPercent,
            CharacterVitalsEngine.DehydrationThresholdPercent);

        // При шкале 3000 мл: 1500 мл — ровно половина, 500 мл — шестая часть.
        // Сравнение с точностью: 500/3000×100 и 100/6 — одно и то же число, но
        // записанное двумя способами, и последний бит double у них разный.
        Assert.Equal(50d, CharacterVitalsTuning.ThirstThresholdPercent, 10);
        Assert.Equal(
            100d / 6d,
            CharacterVitalsTuning.DehydrationThresholdPercent,
            10);
    }

    [Fact]
    public void ConsumptionNormsProduceThePromisedPercentPerHour()
    {
        // Нормы заданы КОЭФФИЦИЕНТАМИ над эталоном (2500 ккал за 3 часа, 300 мл
        // за час, 2500 ккал за 8 часов, 120 мл за час): менять расход удобно
        // коэффициентом, а не переписыванием нормы. Проверяется вся цепочка
        // «коэффициент → норма → процент шкалы».
        Assert.Equal(
            2500d * CharacterVitalsTuning.EnergyLoadCoefficient / 3d,
            CharacterVitalsTuning.EnergyKilocaloriesMovingPerHour);
        Assert.Equal(
            2500d * CharacterVitalsTuning.EnergyRestCoefficient / 8d,
            CharacterVitalsTuning.EnergyKilocaloriesRestingPerHour);
        Assert.Equal(
            300d * CharacterVitalsTuning.HydrationLoadCoefficient,
            CharacterVitalsTuning.HydrationMillilitersMovingPerHour);
        Assert.Equal(
            120d * CharacterVitalsTuning.HydrationRestCoefficient,
            CharacterVitalsTuning.HydrationMillilitersRestingPerHour);

        // Норма в физических величинах переводится в проценты шкалы тем же
        // отношением, что и в движке: норма / размер шкалы × 100.
        //
        // Ожидания ВЫВОДЯТСЯ из калибровки, а не выписаны числами: сами
        // коэффициенты — предмет правки баланса («1,5» → «1,6» меняет расход без
        // единой строки в движке), и замороженный литерал делал бы тест красным
        // после каждой честной правки норм. Тест проверяет ПЕРЕСЧЁТ, а не значение.
        Assert.Equal(
            100d * CharacterVitalsTuning.EnergyKilocaloriesMovingPerHour /
            CharacterVitalsTuning.EnergyScaleKilocalories,
            CharacterVitalsEngine.EnergyConsumptionPercentPerHour(moving: true),
            9);        Assert.Equal(
            100d * CharacterVitalsTuning.EnergyKilocaloriesRestingPerHour /
            CharacterVitalsTuning.EnergyScaleKilocalories,
            CharacterVitalsEngine.EnergyConsumptionPercentPerHour(moving: false),
            9);
        Assert.Equal(
            100d * CharacterVitalsTuning.HydrationMillilitersMovingPerHour /
            CharacterVitalsTuning.HydrationScaleMilliliters,
            CharacterVitalsEngine.HydrationConsumptionPercentPerHour(moving: true),
            9);
        Assert.Equal(
            100d * CharacterVitalsTuning.HydrationMillilitersRestingPerHour /
            CharacterVitalsTuning.HydrationScaleMilliliters,
            CharacterVitalsEngine.HydrationConsumptionPercentPerHour(moving: false),
            9);

        // Обещания автора в читаемом виде: под нагрузкой расход быстрее покоя.
        Assert.True(
            CharacterVitalsEngine.EnergyConsumptionPercentPerHour(true) >
            CharacterVitalsEngine.EnergyConsumptionPercentPerHour(false));
        Assert.True(
            CharacterVitalsEngine.HydrationConsumptionPercentPerHour(true) >
            CharacterVitalsEngine.HydrationConsumptionPercentPerHour(false));
    }
}
