namespace AssistQuestEditor.Domain;

public sealed record CharacterVitalsRates(
    double HealthPerGameMinute,
    double EnergyPerGameMinute,
    double HydrationPerGameMinute,
    double FatiguePerGameMinute,
    double StressPerGameMinute,
    double ResiliencePerGameMinute,
    double MetabolismPerGameMinute);

public static class CharacterVitalsRatesDefaults
{
    public static CharacterVitalsRates Zero => new(0d, 0d, 0d, 0d, 0d, 0d, 0d);
}

/// <summary>
/// Результат шага шкал персонажа: новые значения шкал, новое долгосрочное
/// состояние (кумулятивные шкалы, перегрузки, эффекты) и события шага.
/// Форма совпадает с <see cref="PlayerConditionUpdate"/>, чтобы потребители
/// (Host, кодек сохранений) читали ответ одинаково.
/// </summary>
public sealed record CharacterVitalsUpdate(
    PlayerVitalsState Vitals,
    PlayerConditionState Conditions,
    IReadOnlyList<PlayerConditionEvent> Events);

public static class CharacterVitalsEngine
{
    private const double SecondsPerGameHour = 3600d;
    private const double SecondsPerGameQuarterHour = 900d;

    // ── ЧИСЛА КАЛИБРОВКИ ────────────────────────────────────────────────────
    //
    // Все значения ниже — ПСЕВДОНИМЫ констант из <see cref="CharacterVitalsTuning"/>,
    // а не отдельные числа. Так одна и та же величина не может существовать в
    // двух экземплярах: правка баланса делается в ОДНОМ файле калибровки, а
    // движок и подсказки интерфейса читают одно значение. Здесь остаётся только
    // ЯВНАЯ связь «имя движка → имя в калибровке».
    //
    // Движок отвечает за ЛОГИКУ (когда применить штраф, как посчитать шаг) —
    // числа живут в CharacterVitalsTuning.cs.

    public const double FatigueCriticalPercent =
        CharacterVitalsTuning.FatigueCriticalPercent;

    public const double FatigueExhaustionPercent =
        CharacterVitalsTuning.FatigueExhaustionPercent;

    public const double FatigueBuildHours =
        CharacterVitalsTuning.FatigueBuildHours;

    public const double FatigueRestHours =
        CharacterVitalsTuning.FatigueRestHours;

    public const double FatigueBuildKilometers =
        CharacterVitalsTuning.FatigueBuildKilometers;

    public const double StressCriticalPercent =
        CharacterVitalsTuning.StressCriticalPercent;

    /// <summary>
    /// Норма расхода ЭНЕРГИИ под нагрузкой: 2500 ккал за 3 игровых часа (задано
    /// автором), то есть ≈833,3 ккал/ч.
    ///
    /// Расход теперь ЗАВИСИТ ОТ НАГРУЗКИ, а не задан одним «временем опустошения
    /// шкалы»: в покое организм тратит заметно меньше. Прежние
    /// <c>EnergyConsumptionHours = 30</c> (167 ккал/ч) делали расход почти
    /// незаметным и НЕ различали покой и движение — автор задал обе нормы явно.
    /// </summary>
    public const double CumulativeFatigueAccrualBaseMultiplier =
        CharacterVitalsTuning.CumulativeFatigueAccrualBaseMultiplier;

    /// <summary>За каждые накопленные 5% кумулятивной усталости коэффициент растёт.</summary>
    public const double CumulativeFatigueAccrualStepPercent =
        CharacterVitalsTuning.CumulativeFatigueAccrualStepPercent;

    /// <summary>
    /// Прирост коэффициента за каждые накопленные 5% (задано автором): штраф
    /// удвоен — было 0,25, стало 0,5. Вместе с базой ×3 истощение усталости
    /// превращается в «лавину» заметно быстрее прежнего.
    /// </summary>
    public const double CumulativeFatigueAccrualStepBonus =
        CharacterVitalsTuning.CumulativeFatigueAccrualStepBonus;

    /// <summary>
    /// Норма расхода ЭНЕРГИИ под нагрузкой: 2500 ккал за 3 игровых часа (задано
    /// автором), то есть ≈833,3 ккал/ч.
    ///
    /// Расход ЗАВИСИТ ОТ НАГРУЗКИ, а не задан одним «временем опустошения
    /// шкалы»: в покое организм тратит заметно меньше. Обе нормы — в
    /// <see cref="CharacterVitalsTuning"/>.
    /// </summary>
    public const double EnergyKilocaloriesMovingPerHour =
        CharacterVitalsTuning.EnergyKilocaloriesMovingPerHour;

    /// <summary>
    /// Норма расхода ЭНЕРГИИ в покое: 2500 ккал за 8 игровых часов (задано
    /// автором), то есть 312,5 ккал/ч.
    /// </summary>
    public const double EnergyKilocaloriesRestingPerHour =
        CharacterVitalsTuning.EnergyKilocaloriesRestingPerHour;

    /// <summary>
    /// Норма расхода ЖИДКОСТИ под нагрузкой: 150 мл за 30 игровых минут (задано
    /// автором), то есть 300 мл/ч.
    /// </summary>
    public const double HydrationMillilitersMovingPerHour =
        CharacterVitalsTuning.HydrationMillilitersMovingPerHour;

    /// <summary>
    /// Норма расхода ЖИДКОСТИ в покое: 60 мл за те же 30 игровых минут (задано
    /// автором), то есть 120 мл/ч.
    /// </summary>
    public const double HydrationMillilitersRestingPerHour =
        CharacterVitalsTuning.HydrationMillilitersRestingPerHour;

    /// <summary>
    /// Расход энергии в ПРОЦЕНТАХ шкалы за игровой час при данной нагрузке.
    ///
    /// Единая точка пересчёта «физическая норма → проценты шкалы»: и движок, и
    /// подсказка монитора обязаны брать одно число. При нагрузке это 16,67%/ч,
    /// в покое — 6,25%/ч.
    /// </summary>
    public static double EnergyConsumptionPercentPerHour(bool moving) =>
        (moving
            ? EnergyKilocaloriesMovingPerHour
            : EnergyKilocaloriesRestingPerHour) /
        CharacterDigestion.EnergyScaleKilocalories *
        100d;

    /// <summary>
    /// Расход жидкости в ПРОЦЕНТАХ шкалы за игровой час при данной нагрузке
    /// (нагрузка — 10%/ч, покой — 4%/ч при шкале 3000 мл).
    /// </summary>
    public static double HydrationConsumptionPercentPerHour(bool moving) =>
        (moving
            ? HydrationMillilitersMovingPerHour
            : HydrationMillilitersRestingPerHour) /
        CharacterDigestion.HydrationScaleMilliliters *
        100d;

    public const double SleepConsumptionMultiplier =
        CharacterVitalsTuning.SleepConsumptionMultiplier;

    // ── «Жажда» и «Обезвоживание» ──────────────────────────────────────────
    //
    // Пороги заданы автором в МИЛЛИЛИТРАХ и выведены в проценты шкалы, потому
    // что весь движок живёт в процентах: держать порог в миллилитрах, а условие
    // писать в процентах значило бы иметь два числа об одном и том же.
    //
    //   • 1500 мл (50% шкалы) — начало «Жажды»: копится истощение жидкости
    //     (снижается верхний предел шкалы), растёт стресс, падает метаболизм;
    //   • 500 мл (16,67% шкалы) — «Обезвоживание»: метаболизм теряется вдвое
    //     быстрее, стресс растёт на 25% быстрее, падает устойчивость, запрещены
    //     действия с физической нагрузкой;
    //   • 1% шкалы — критическая нехватка: истощение копится вдвое быстрее,
    //     отнимается здоровье (100% за игровой час), копится истощение стресса.

    /// <summary>Порог «Жажды» в миллилитрах (задано автором: 1500).</summary>
    public const double ThirstThresholdMilliliters =
        CharacterVitalsTuning.ThirstThresholdMilliliters;

    /// <summary>Порог «Обезвоживания» в миллилитрах (задано автором: 500).</summary>
    public const double DehydrationThresholdMilliliters =
        CharacterVitalsTuning.DehydrationThresholdMilliliters;

    /// <summary>Порог «Жажды» в процентах шкалы (1500 из 3000 → 50%).</summary>
    public const double ThirstThresholdPercent =
        CharacterVitalsTuning.ThirstThresholdPercent;

    /// <summary>Порог «Обезвоживания» в процентах шкалы (500 из 3000 → 16,67%).</summary>
    public const double DehydrationThresholdPercent =
        CharacterVitalsTuning.DehydrationThresholdPercent;

    /// <summary>
    /// Порог критической нехватки жидкости в процентах шкалы (задано автором:
    /// «при показателе шкалы меньше 1» — выбрано 1% шкалы, ≈30 мл при 3000 мл).
    /// </summary>
    public const double CriticalHydrationPercent =
        CharacterVitalsTuning.CriticalHydrationPercent;

    /// <summary>Идентификатор дебаффа «Жажда».</summary>
    public const string ThirstEffectId = "thirst";

    /// <summary>Идентификатор дебаффа «Обезвоживание».</summary>
    public const string DehydrationEffectId = "dehydration";

    /// <summary>
    /// «Жажда»: прибавка к СКОРОСТИ накопления стресса (+0,5 %/мин) — задано
    /// автором в его едином языке «процент от шкалы в минуту». Штраф аддитивен:
    /// он прибавляется к уже полученной скорости, а не умножает её.
    /// </summary>
    public const double ThirstStressPercentPerMinute =
        CharacterVitalsTuning.ThirstStressPercentPerMinute;

    /// <summary>«Жажда»: потеря метаболизма 1% за 10 игровых минут (задано автором).</summary>
    public const double ThirstMetabolismPercentPerTenMinutes =
        CharacterVitalsTuning.ThirstMetabolismPercentPerTenMinutes;

    /// <summary>
    /// «Обезвоживание»: множитель скорости накопления стресса (задано автором:
    /// «ускорить текущую скорость накопления на 25%» → ×1,25).
    /// </summary>
    public const double DehydrationStressMultiplier =
        CharacterVitalsTuning.DehydrationStressMultiplier;

    /// <summary>
    /// «Обезвоживание»: потеря метаболизма 2% за 10 игровых минут — вдвое
    /// быстрее жажды («удваивается потеря метаболизма»).
    /// </summary>
    public const double DehydrationMetabolismPercentPerTenMinutes =
        CharacterVitalsTuning.DehydrationMetabolismPercentPerTenMinutes;

    /// <summary>
    /// Критическая нехватка жидкости: потеря здоровья 100% шкалы за игровой час
    /// (задано автором).
    /// </summary>
    public const double CriticalHydrationHealthLossPercentPerHour =
        CharacterVitalsTuning.CriticalHydrationHealthLossPercentPerHour;

    /// <summary>
    /// Критическая нехватка жидкости: истощение жидкости копится ВДВОЕ быстрее
    /// («истощение удваивает скорость накопления»). Базовый множитель жидкости
    /// равен 2 (жидкость изнашивается вдвое быстрее энергии), поэтому здесь — ×2.
    /// </summary>
    public const double CriticalHydrationExhaustionDoubleFactor =
        CharacterVitalsTuning.CriticalHydrationExhaustionDoubleFactor;

    /// <summary>
    /// Критическая нехватка жидкости: истощение СТРЕССА копится со скоростью
    /// 1% за игровой час — та же скорость, с которой стресс превращается в
    /// истощение при критическом стрессе («на каждые 2 единицы — 1»).
    /// </summary>
    public const double CriticalHydrationStressExhaustionPercentPerHour =
        CharacterVitalsTuning.CriticalHydrationStressExhaustionPercentPerHour;

    /// <summary>
    /// За сколько часов ПОЛНОЦЕННОГО сна уходит вся шкала обычного стресса.
    ///
    /// Задано автором: «100% стресса уходит за 6 часов полноценного сна».
    /// Снимается только ОБЫЧНЫЙ стресс; кумулятивный стресс по-прежнему
    /// снимается лишь отпуском (см. Character_Vitals.md).
    /// </summary>
    public const double FullSleepStressClearHours =
        CharacterVitalsTuning.FullSleepStressClearHours;

    /// <summary>
    /// Во сколько раз ОБЫЧНЫЙ сон снимает стресс медленнее полноценного
    /// (задано автором: «в 2.5 раза меньше»).
    /// </summary>
    public const double RegularSleepStressClearPenalty =
        CharacterVitalsTuning.RegularSleepStressClearPenalty;

    /// <summary>
    /// Сколько ресурсов стоит восстановление здоровья (задано автором):
    /// 1 единица здоровья = 1 единица энергии и 2 единицы жидкости.
    ///
    /// Регенерация поэтому не бесплатна: при пустой энергии или жидкости она
    /// встаёт, и «Текущая динамика» здоровья обязана это показывать, а не
    /// обещать рост, которого не будет. Заданное в процентах правило
    /// «1% здоровья = 1% энергии и 2% жидкости» переводится в единицы тем же
    /// отношением: 100 единиц здоровья стоят 100 единиц энергии и 200 жидкости.
    /// </summary>
    public const double HealthRegenEnergyPerHealthUnit =
        CharacterVitalsTuning.HealthRegenEnergyPerHealthUnit;
    public const double HealthRegenHydrationPerHealthUnit =
        CharacterVitalsTuning.HealthRegenHydrationPerHealthUnit;

    /// <summary>
    /// Базовая скорость восстановления здоровья (задано автором):
    /// 1% здоровья за 10 игровых минут при нормальном метаболизме и устойчивости.
    ///
    /// «Нормальное состояние» — это номинал метаболизма и устойчивости (60%):
    /// любое отклонение вверх даёт бонус (см.
    /// <see cref="HealthRegenElevatedBonusPercent"/>), вниз — ничего не даёт.
    /// </summary>
    public const double HealthRegenGameMinutesPerPercent =
        CharacterVitalsTuning.HealthRegenGameMinutesPerPercent;

    /// <summary>
    /// Надбавка к восстановлению здоровья за КАЖДОЕ повышенное свойство
    /// (задано автором): повышенный метаболизм и повышенная устойчивость дают по
    /// +1% здоровья за те же 10 игровых минут, то есть ускоряют восстановление,
    /// не удорожая его (цена остаётся 1:1 и 1:2).
    /// </summary>
    public const double HealthRegenElevatedBonusPercent =
        CharacterVitalsTuning.HealthRegenElevatedBonusPercent;

    /// <summary>
    /// Надбавка к восстановлению здоровья от баффа «Бык» (задано автором).
    ///
    /// Задана В ПРОЦЕНТАХ, как и надбавки за свойства: +2% здоровья за те же
    /// десять игровых минут. Прежний множитель ×1,5 был относительным — он
    /// усиливал и базовую скорость, и надбавки, поэтому «бык» давал тем больше,
    /// чем лучше прочие условия, и обещанные «+2%» не читались в интерфейсе
    /// никак: игрок видел только итоговую скорость.
    /// </summary>
    public const double HealthRegenBullBonusPercent =
        CharacterVitalsTuning.HealthRegenBullBonusPercent;

    /// <summary>
    /// Во сколько раз усталость набирается БЫСТРЕЕ, пока здоровье
    /// восстанавливается (задано автором: «получаемая усталость удваивается»).
    /// </summary>
    public const double HealthRegenFatigueMultiplier =
        CharacterVitalsTuning.HealthRegenFatigueMultiplier;

    public const double HygieneDecayHours =
        CharacterVitalsTuning.HygieneDecayHours;

    public const double DefaultResiliencePercent =
        CharacterVitalsTuning.DefaultResiliencePercent;

    public const double DefaultMetabolismPercent =
        CharacterVitalsTuning.DefaultMetabolismPercent;

    /// <summary>Границы нормального метаболизма (45–75%): вне них он меняет расход.</summary>
    public const double ReducedMetabolismPercent =
        CharacterVitalsTuning.ReducedMetabolismPercent;

    public const double ElevatedMetabolismPercent =
        CharacterVitalsTuning.ElevatedMetabolismPercent;

    public const double MetabolismRecoveryPercentPerHour =
        CharacterVitalsTuning.MetabolismRecoveryPercentPerHour;

    public const double MetabolismExhaustionPenaltyPerQuarterHour =
        CharacterVitalsTuning.MetabolismExhaustionPenaltyPerQuarterHour;

    public const double HotelPrice =
        CharacterVitalsTuning.HotelPrice;

    public const int HotelSleepHours =
        CharacterVitalsTuning.HotelSleepHours;

    public const double RelaxationExperienceMultiplier =
        CharacterVitalsTuning.RelaxationExperienceMultiplier;

    public const double RelaxationStressSlowdownPercent =
        CharacterVitalsTuning.RelaxationStressSlowdownPercent;

    public const double RelaxationDurationRealSeconds =
        CharacterVitalsTuning.RelaxationDurationRealSeconds;

    public const double RestedExperienceMultiplier =
        CharacterVitalsTuning.RestedExperienceMultiplier;

    public const double RestedStressSlowdownPercent =
        CharacterVitalsTuning.RestedStressSlowdownPercent;

    public const double RestedDurationRealSeconds =
        CharacterVitalsTuning.RestedDurationRealSeconds;

    public const double BurnoutFatigueBuildMultiplier =
        CharacterVitalsTuning.BurnoutFatigueBuildMultiplier;

    public const double BurnoutDurationRealSeconds =
        CharacterVitalsTuning.BurnoutDurationRealSeconds;

    public const double PowerSurgeDurationRealSeconds =
        CharacterVitalsTuning.PowerSurgeDurationRealSeconds;

    public static CharacterVitalsRates RatesFrom(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        bool moving,
        bool sleeping,
        double gameHourOfDay)
    {
        vitals = (vitals ?? PlayerVitalsState.Default).Normalize();
        conditions = (conditions ?? PlayerConditionState.Empty).Normalize();

        if (HasEffect(conditions, "power_surge"))
            return CharacterVitalsRatesDefaults.Zero;

        var metabolismFactor = MetabolismConsumptionFactor(vitals, conditions);
        var sleepFactor = sleeping ? SleepConsumptionMultiplier : 1d;
        var stressPercent = TotalStressPercent(conditions);
        var resiliencePercent = EffectiveResiliencePercent(vitals, conditions);
        var fatigueFactor = moving && !sleeping
            ? TimeOfDayFatigueMultiplier(gameHourOfDay)
            : 1d;

        // Нагрузка — источник расхода энергии и жидкости: нормы заданы автором
        // отдельно для движения и покоя (см. EnergyConsumptionPercentPerHour и
        // HydrationConsumptionPercentPerHour). Сон поверх нагрузки замедляет
        // расход ещё втрое.
        var energyBasePerHour = EnergyConsumptionPercentPerHour(moving);
        var hydrationBasePerHour = HydrationConsumptionPercentPerHour(moving);

        // «Жажда» и «Обезвоживание» ускоряют накопление стресса. По единому
        // языку автора («процент от шкалы в минуту») жажда — АДДИТИВНАЯ
        // прибавка к скорости, обезвоживание — МНОЖИТЕЛЬ ×1,25 к уже полученной
        // скорости. Порядок важен: сначала множитель, потом прибавка, иначе
        // «+0,5 %/мин» жажды тоже умножилось бы на 1,25.
        var dehydrationActive =
            Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(vitals, conditions)) <
            DehydrationThresholdPercent;
        var thirstActive =
            Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(vitals, conditions)) <
            ThirstThresholdPercent;
        var dehydrationStressMultiplier = dehydrationActive
            ? DehydrationStressMultiplier
            : 1d;
        var thirstStressPerMinute = thirstActive
            ? ThirstStressPercentPerMinute
            : 0d;

        // Здоровье восстанавливается за счёт энергии и жидкости (1:1 и 1:2),
        // поэтому тот же расход обязан попасть в скорость этих шкал. Иначе
        // подсказка показывала бы только трату жизнедеятельности, и игрок не
        // понял бы, почему энергия падает при лежании без движения.
        var healthRegenPerHour = HealthRegenPerHour(vitals, conditions);

        var energyPerHour =
            -energyBasePerHour *
            metabolismFactor *
            sleepFactor -
            healthRegenPerHour * HealthRegenEnergyPerHealthUnit;
        var hydrationPerHour =
            -hydrationBasePerHour *
            metabolismFactor *
            sleepFactor -
            healthRegenPerHour * HealthRegenHydrationPerHealthUnit;

        // Пищеварение даёт ПОЛОЖИТЕЛЬНУЮ скорость, пока в желудке есть
        // неусвоенное: пока идёт насыщение, энергия и жидкость растут, и
        // подсказка обязана показывать это как «динамику вверх» — иначе игрок
        // не видит, что еда уже действует.
        var digestionEnergyPerMinute =
            conditions.Stomach.EnergyPerGameSecond * 60d;
        var digestionHydrationPerMinute =
            conditions.Stomach.HydrationPerGameSecond * 60d;

        var fatiguePerHour =
            moving && !sleeping
                ? CharacterVitalsTuning.FatigueBuildPercentPerHour *
                  fatigueFactor *
                  (HasEffect(conditions, "burnout")
                      ? BurnoutFatigueBuildMultiplier
                      : 1d) *
                  (healthRegenPerHour > 0d
                      ? HealthRegenFatigueMultiplier
                      : 1d)
                : vitals.Fatigue > 0d
                    ? -CharacterVitalsTuning.FatigueRestPercentPerHour *
                      Math.Clamp(
                          1d - stressPercent / 100d,
                          0d,
                          1d)
                    : 0d;

        var stressBasePerHour =
            conditions.CumulativeFatigue > 0d
                ? PlayerConditionScale.ToPercent(
                      conditions.CumulativeFatigue) *
                  CharacterVitalsTuning
                      .StressPerCumulativeFatiguePercentPerHour *
                  Math.Clamp(
                      1d -
                      resiliencePercent /
                      CharacterVitalsTuning
                          .StressResilienceDivisor,
                      0d,
                      1d) *
                  StressMultiplier(conditions)
                : 0d;
        var stressPerHour =
            stressBasePerHour * dehydrationStressMultiplier;
        var stressThirstPerMinute = thirstStressPerMinute;

        return new CharacterVitalsRates(
            PlayerConditionScale.RateFromPercent(healthRegenPerHour / 60d),
            PlayerConditionScale.RateFromPercent(energyPerHour / 60d) +
                digestionEnergyPerMinute,
            PlayerConditionScale.RateFromPercent(hydrationPerHour / 60d) +
                digestionHydrationPerMinute,
            PlayerConditionScale.RateFromPercent(fatiguePerHour / 60d),
            PlayerConditionScale.RateFromPercent(
                stressPerHour / 60d + stressThirstPerMinute),
            PlayerConditionScale.RateFromPercent(ResilienceDirection(vitals, conditions, moving) / 60d),
            PlayerConditionScale.RateFromPercent(MetabolismDirection(vitals, conditions) / 60d));
    }

    public static CharacterVitalsUpdate Advance(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        double gameSeconds,
        double realSeconds,
        bool playerMoving,
        bool sleeping,
        double traveledMeters,
        double gameHourOfDay,
        bool allowSleepInterruption = false)
    {
        vitals = (vitals ?? PlayerVitalsState.Default).Normalize();
        conditions = (conditions ?? PlayerConditionState.Empty).Normalize();

        var game = Math.Max(
            0d,
            double.IsFinite(gameSeconds) ? gameSeconds : 0d);
        var real = Math.Max(
            0d,
            double.IsFinite(realSeconds) ? realSeconds : 0d);
        var state = TickEffects(conditions, real);

        if (game <= 0d)
            return new CharacterVitalsUpdate(
                vitals,
                state,
                Array.Empty<PlayerConditionEvent>());

        var events = new List<PlayerConditionEvent>();
        var remaining = game;
        var totalSeconds = game;
        var remainingMeters = Math.Max(
            0d,
            double.IsFinite(traveledMeters)
                ? traveledMeters
                : 0d);
        var hour = NormalizeHour(gameHourOfDay);

        while (remaining > 0.000001d)
        {
            var stepSeconds = Math.Min(
                SecondsPerGameQuarterHour,
                remaining);
            remaining -= stepSeconds;

            var fractionOfHour =
                stepSeconds / SecondsPerGameHour;
            var stepMeters =
                totalSeconds > 0d
                    ? remainingMeters *
                      stepSeconds /
                      totalSeconds
                    : 0d;
            remainingMeters -= stepMeters;

            UpdateCaffeine(
                ref vitals,
                ref state,
                stepSeconds,
                hour,
                sleeping,
                events);

            if (!HasEffect(state, "power_surge"))
            {
                // Пищеварение: желудок отдаёт шкалам ровно то, что успело
                // усвоиться за шаг. Именно здесь восстановление становится
                // ПОСТЕПЕННЫМ — еда и питьё больше не начисляют энергию и
                // жидкость мгновенно (см. CharacterDigestion).
                var digestion = CharacterDigestion.Advance(
                    state.Stomach,
                    stepSeconds);

                state = state with
                {
                    Stomach = digestion.Contents
                };

                vitals = vitals with
                {
                    Energy = vitals.Energy + digestion.EnergyGain,
                    Hydration = vitals.Hydration + digestion.HydrationGain
                };

                if (sleeping && !allowSleepInterruption && !CanSleep(vitals))
                {
                    sleeping = false;
                    events.Add(
                        new PlayerConditionEvent(
                            "SleepInterrupted"));
                }

                var resiliencePercent =
                    EffectiveResiliencePercent(
                        vitals,
                        state);
                var metabolismFactor =
                    MetabolismConsumptionFactor(
                        vitals,
                        state);
                var sleepFactor =
                    sleeping
                        ? SleepConsumptionMultiplier
                        : 1d;
                var fatigueMultiplier =
                    playerMoving && !sleeping
                        ? TimeOfDayFatigueMultiplier(hour)
                        : 1d;
                var burnoutMultiplier =
                    HasEffect(state, "burnout")
                        ? BurnoutFatigueBuildMultiplier
                        : 1d;

                if (playerMoving && !sleeping)
                {
                    var gainPercent =
                        CharacterVitalsTuning.FatigueBuildPercentPerHour *
                        fractionOfHour *
                        fatigueMultiplier *
                        burnoutMultiplier +
                        CharacterVitalsTuning
                            .FatigueBuildPercentPerKilometer *
                        stepMeters / 1000d *
                        fatigueMultiplier *
                        burnoutMultiplier;

                    // Пока здоровье восстанавливается, усталость копится
                    // ВДВОЕ быстрее (задано автором): организм тратит силы на
                    // заживление. Проверка идёт по той же скорости, которой
                    // живёт восстановление, поэтому при полном здоровье (или
                    // без ресурсов) штрафа нет — иначе «удвоение» висело бы на
                    // игроке, у которого регенерации фактически не происходит.
                    if (HealthRegenPerHour(vitals, state) > 0d)
                        gainPercent *= HealthRegenFatigueMultiplier;

                    var gain =
                        UnitsFromPercentExact(
                            gainPercent);

                    var before =
                        vitals.Fatigue;

                    ApplyNegativeChange(
                        ref vitals,
                        ref state,
                        "fatigue",
                        gain,
                        fillDirection: true,
                        resiliencePercent);

                    AddFatigueExhaustion(
                        ref state,
                        gain,
                        before,
                        resiliencePercent);
                }
                else if (!sleeping)
                {
                    var recoveryFactor =
                        Math.Clamp(
                            1d -
                            TotalStressPercent(state) /
                            100d,
                            0d,
                            1d);
                    var recovery =
                        UnitsFromPercentExact(
                            CharacterVitalsTuning.FatigueRestPercentPerHour *
                            fractionOfHour *
                            recoveryFactor);

                    vitals = vitals with
                    {
                        Fatigue = Math.Max(
                            0d,
                            vitals.Fatigue -
                            Math.Min(
                                recovery,
                                vitals.Fatigue))
                    };
                }

                var energyDemand =
                    UnitsFromPercentExact(
                        EnergyConsumptionPercentPerHour(playerMoving) *
                        fractionOfHour *
                        metabolismFactor *
                        sleepFactor);
                var hydrationDemand =
                    UnitsFromPercentExact(
                        HydrationConsumptionPercentPerHour(playerMoving) *
                        fractionOfHour *
                        metabolismFactor *
                        sleepFactor);

                var energyBefore =
                    vitals.Energy;
                var hydrationBefore =
                    vitals.Hydration;

                ApplyNegativeChange(
                    ref vitals,
                    ref state,
                    "energy",
                    energyDemand,
                    fillDirection: false,
                    resiliencePercent);

                ApplyNegativeChange(
                    ref vitals,
                    ref state,
                    "hydration",
                    hydrationDemand,
                    fillDirection: false,
                    resiliencePercent);

                if (!sleeping)
                {
                    AddResourceExhaustion(
                        ref state,
                        "energy",
                        energyBefore,
                        vitals.Energy,
                        energyDemand,
                        CharacterVitalsTuning.EnergyExhaustionMultiplier,
                        resiliencePercent);

                    // Критическая нехватка жидкости (ниже 1% шкалы) удваивает
                    // скорость накопления истощения — задано автором.
                    var criticalHydration =
                        Percent(
                            hydrationBefore,
                            EffectiveHydrationMaximum(vitals, state)) <
                        CriticalHydrationPercent;

                    AddResourceExhaustion(
                        ref state,
                        "hydration",
                        hydrationBefore,
                        vitals.Hydration,
                        hydrationDemand,
                        CharacterVitalsTuning.HydrationExhaustionMultiplier *
                        (criticalHydration
                            ? CriticalHydrationExhaustionDoubleFactor
                            : 1d),
                        resiliencePercent);

                    // Критическая нехватка жидкости отнимает здоровье со
                    // скоростью 100% шкалы за игровой час и копит истощение
                    // СТРЕССА (задано автором). Здоровье снимается в ЕДИНИЦАХ
                    // шкалы, поэтому множитель — UnitsFromPercentExact, а не
                    // проценты: иначе «100%/час» обнуляло бы шкалу за час
                    // реального времени вместо игрового.
                    if (criticalHydration)
                    {
                        vitals = vitals with
                        {
                            Health = Math.Max(
                                0d,
                                vitals.Health -
                                UnitsFromPercentExact(
                                    CriticalHydrationHealthLossPercentPerHour *
                                    fractionOfHour))
                        };

                        if (!HasEffect(state, "bull") &&
                            !SkipNegativeRoll(
                                ref state,
                                resiliencePercent / 2d))
                        {
                            state = state with
                            {
                                CumulativeStress =
                                    state.CumulativeStress +
                                    UnitsFromPercentExact(
                                        CriticalHydrationStressExhaustionPercentPerHour *
                                        fractionOfHour)
                            };
                        }
                    }
                }

                // Признак ВХОДА в критический стресс: счётчик критических часов
                // обнуляется, как только стресс опустился ниже порога. Дебафф
                // «Выгорание» выдаётся при входе в критическую зону — в том
                // числе когда игрок уже стоит в ней без дебаффа (например,
                // после загрузки сохранения), — но не продлевается, пока
                // счётчик растёт, поэтому «строго разово» соблюдено.
                var enteringCriticalStress =
                    state.CriticalStressGameSeconds <= 0d;
                AddStress(
                    ref state,
                    stepSeconds,
                    resiliencePercent,
                    Percent(
                        vitals.Hydration,
                        EffectiveHydrationMaximum(vitals, state)) <
                    DehydrationThresholdPercent
                        ? DehydrationStressMultiplier
                        : 1d);

                // «Жажда» (жидкость ниже 1500 мл) добавляет стресс аддитивно —
                // +0,5 %/мин, задано автором. Прибавка идёт ОТДЕЛЬНО от
                // множителя обезвоживания внутри AddStress: там скорость
                // умножается на 1,25, а жажда прибавляет ровно 0,5 %/мин.
                var thirstStressActive =
                    Percent(
                        vitals.Hydration,
                        EffectiveHydrationMaximum(vitals, state)) <
                    ThirstThresholdPercent;
                if (thirstStressActive)
                {
                    AddStress(
                        ref state,
                        ThirstStressPercentPerMinute *
                        (stepSeconds / 60d));
                }

                var stressAfter =
                    TotalStressPercent(state);

                if (stressAfter > StressCriticalPercent)
                {
                    var stressCounter =
                        state.CriticalStressGameSeconds +
                        stepSeconds;
                    var wholeStressHours =
                        Math.Floor(
                            stressCounter /
                            SecondsPerGameHour);

                    if (wholeStressHours > 0d)
                    {
                        // ВО СНЕ ИСТОЩЕНИЕ НЕ НАКАПЛИВАЕТСЯ (Character_Vitals.md:
                        // «во время сна не может накапливаться истощение каких-либо
                        // показателей»). Без этой проверки сон, начатый с высоким
                        // стрессом, копил кумулятивный стресс — а тот снимается
                        // ТОЛЬКО отпуском, поэтому «после ночлега стресс остался»
                        // было не багом отображения, а следствием этой дыры.
                        if (!sleeping &&
                            !HasEffect(state, "bull") &&
                            !SkipNegativeRoll(
                                ref state,
                                resiliencePercent / 2d))
                        {
                            state = state with
                            {
                                CumulativeStress =
                                    state.CumulativeStress +
                                    UnitsFromPercentExact(
                                        wholeStressHours)
                            };
                        }

                        state = state with
                        {
                            CriticalStressGameSeconds =
                                stressCounter -
                                wholeStressHours *
                                SecondsPerGameHour
                        };
                    }
                    else
                    {
                        state = state with
                        {
                            CriticalStressGameSeconds =
                                stressCounter
                        };
                    }
                }
                else
                {
                    state = state with
                    {
                        CriticalStressGameSeconds = 0d
                    };
                }

                if (stressAfter > StressCriticalPercent &&
                    enteringCriticalStress &&
                    !HasEffect(state, "burnout"))
                {
                    state = AddOrReplaceEffect(
                        state,
                        new ActivePlayerEffectState(
                            "burnout",
                            "Выгорание",
                            BurnoutDurationRealSeconds,
                            true));

                    vitals = vitals with
                    {
                        Health = Math.Max(
                            0d,
                            vitals.Health -
                            UnitsFromPercentExact(
                                CharacterVitalsTuning
                                    .BurnoutInstantPenaltyPercent)),
                        Energy = Math.Max(
                            0d,
                            vitals.Energy -
                            UnitsFromPercentExact(
                                CharacterVitalsTuning
                                    .BurnoutInstantPenaltyPercent)),
                        Hydration = Math.Max(
                            0d,
                            vitals.Hydration -
                            UnitsFromPercentExact(
                                CharacterVitalsTuning
                                    .BurnoutInstantPenaltyPercent))
                    };

                    events.Add(
                        new PlayerConditionEvent(
                            "BurnoutApplied"));
                }

                var fatiguePercent =
                    PlayerConditionScale.ToPercent(
                        vitals.Fatigue);

                if (fatiguePercent > FatigueCriticalPercent)
                {
                    var counter =
                        state.CriticalFatigueGameSeconds +
                        stepSeconds;
                    var wholeHours =
                        Math.Floor(
                            counter /
                            SecondsPerGameHour);

                    if (wholeHours > 0d)
                    {
                        // Накопление усталости ускоряется по мере роста её
                        // кумулятивной шкалы (см. CumulativeFatigueAccrualMultiplier).
                        var cumulativeGain =
                            UnitsFromPercentExact(
                                wholeHours) *
                            CumulativeFatigueAccrualMultiplier(state);

                        // «Бык» прекращает ЛЮБОЕ негативное накопление, поэтому
                        // почасовое истощение усталости не должно его обходить:
                        // без этой проверки бафф гасил только пошаговое
                        // истощение, а критическая усталость продолжала копить.
                        //
                        // Сон — тоже полный запрет на накопление истощения
                        // (Character_Vitals.md): ночуя усталым, игрок не должен
                        // просыпаться с новой порцией кумулятивной усталости,
                        // которую сон как раз и обязан снимать.
                        if (!sleeping &&
                            !HasEffect(state, "bull") &&
                            !SkipNegativeRoll(
                                ref state,
                                resiliencePercent / 2d))
                        {
                            state = state with
                            {
                                CumulativeFatigue =
                                    state.CumulativeFatigue +
                                    cumulativeGain
                            };
                        }

                        state = state with
                        {
                            CriticalFatigueGameSeconds =
                                counter -
                                wholeHours *
                                SecondsPerGameHour
                        };
                    }
                    else
                    {
                        state = state with
                        {
                            CriticalFatigueGameSeconds =
                                counter
                        };
                    }
                }
                else
                {
                    state = state with
                    {
                        CriticalFatigueGameSeconds = 0d
                    };
                }

                HealthRegenAndExhaustionDrain(
                    ref vitals,
                    ref state,
                    stepSeconds,
                    sleeping);

                ApplyHygiene(
                    ref vitals,
                    ref state,
                    stepSeconds);

                UpdateResilienceAndMetabolism(
                    ref vitals,
                    state,
                    stepSeconds,
                    sleeping,
                    playerMoving);

                // «В желудке усваивается что-то с жидкостью» — условие снятия
                // «Жажды» (задано автором). Признак берётся по ОСТАТКУ в желудке,
                // а не по последнему предмету: очередь порций уже усреднена
                // пищеварением, и остаток — единственный источник правды о том,
                // что вода ещё поступает.
                var hydrationDigesting =
                    state.Stomach.HydrationRemaining > 0.001d;

                // «Положительная (зелёная) динамика жидкости» — условие снятия
                // «Обезвоживания» (задано автором). Считается ТЕМ ЖЕ правилом,
                // что и скорость шкалы: вода обязана приходить БЫСТРЕЕ, чем
                // тратится, иначе динамика красная и дебафф снимать не за что.
                var hydrationGainPerSecond =
                    state.Stomach.HydrationPerGameSecond;
                var hydrationConsumptionPerSecond =
                    stepSeconds > 0d
                        ? hydrationDemand / stepSeconds
                        : 0d;
                var hydrationPositive =
                    hydrationGainPerSecond >
                    hydrationConsumptionPerSecond;

                SyncThresholdEffects(
                    ref vitals,
                    ref state,
                    events,
                    hydrationDigesting,
                    hydrationPositive);
            }

            vitals = ClampSoft(
                vitals,
                state);

            hour = NormalizeHour(
                hour +
                stepSeconds /
                SecondsPerGameHour);
        }

        TryActivatePowerSurge(
            ref state,
            vitals,
            events);

        return new CharacterVitalsUpdate(
            vitals.Normalize(),
            state.Normalize(),
            events);
    }

    public static bool CanSleep(
        PlayerVitalsState vitals)
    {
        vitals =
            (vitals ?? PlayerVitalsState.Default)
            .Normalize();

        return Percent(
                   vitals.Health,
                   vitals.MaxHealth) >
               CharacterVitalsTuning.SleepBlockedBelowPercent &&
               Percent(
                   vitals.Energy,
                   vitals.MaxEnergy) >
               CharacterVitalsTuning.SleepBlockedBelowPercent &&
               Percent(
                   vitals.Hydration,
                   vitals.MaxHydration) >
               CharacterVitalsTuning.SleepBlockedBelowPercent;
    }

    public static CharacterVitalsUpdate Sleep(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        int hours,
        bool fullSleep) =>
        SleepInternal(
            vitals,
            conditions,
            hours,
            fullSleep,
            allowSleepInterruption: true);

    private static CharacterVitalsUpdate SleepInternal(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        int hours,
        bool fullSleep,
        bool allowSleepInterruption)
    {
        vitals =
            (vitals ?? PlayerVitalsState.Default)
            .Normalize();
        conditions =
            (conditions ?? PlayerConditionState.Empty)
            .Normalize();

        if (!CanSleep(vitals))
        {
            return new CharacterVitalsUpdate(
                vitals,
                conditions,
                new[]
                {
                    new PlayerConditionEvent(
                        "SleepBlocked")
                });
        }

        var safeHours =
            Math.Clamp(hours, 1, 24);
        var events =
            new List<PlayerConditionEvent>();

        for (var index = 0;
             index < safeHours;
             index++)
        {
            var update = Advance(
                vitals,
                conditions,
                SecondsPerGameHour,
                0d,
                playerMoving: false,
                sleeping: true,
                traveledMeters: 0d,
                gameHourOfDay: 0d,
                allowSleepInterruption: allowSleepInterruption);

            vitals = update.Vitals;
            conditions = update.Conditions;
            events.AddRange(update.Events);

            if (index == 0)
            {
                conditions =
                    AddOrReplaceEffect(
                        conditions,
                        new ActivePlayerEffectState(
                            "relaxation",
                            "Расслабление",
                            RelaxationDurationRealSeconds,
                            false,
                            RelaxationExperienceMultiplier,
                            RelaxationStressSlowdownPercent));
            }
        }

        conditions = conditions with
        {
            CumulativeFatigue =
                fullSleep
                    ? UnitsFromPercentExact(
                        CharacterVitalsTuning
                            .FullSleepCumulativeFatigueClearPercent)
                    : Math.Max(
                        UnitsFromPercentExact(
                            CharacterVitalsTuning
                                .FieldSleepCumulativeFatigueFloorPercent),
                        conditions.CumulativeFatigue -
                        UnitsFromPercentExact(
                            CharacterVitalsTuning
                                .FieldSleepCumulativeFatigueClearPercent)),
            CriticalFatigueGameSeconds = 0d,
            // Сон снимает ОБЫЧНЫЙ стресс. До этого стресс умели уменьшать только
            // предметы, поэтому «устал и лёг спать» не помогало вовсе, и после
            // гостиницы игрок видел прежние 44%.
            //
            // Скорость задана автором: полноценный сон убирает всю шкалу за
            // <see cref="FullSleepStressClearHours"/> часов, обычный — в
            // <see cref="RegularSleepStressClearPenalty"/> раз медленнее.
            // Снимается ровно ОБЫЧНЫЙ стресс: кумулятивный неприкосновенен.
            Stress = Math.Max(
                0d,
                conditions.Stress -
                UnitsFromPercentExact(
                    CharacterVitalsTuning.FullSleepStressClearPercent /
                    (fullSleep
                        ? FullSleepStressClearHours
                        : FullSleepStressClearHours *
                          RegularSleepStressClearPenalty) *
                    safeHours))
        };

        vitals = vitals with
        {
            Fatigue = 0d
        };

        if (fullSleep)
        {
            conditions = conditions with
            {
                Effects = conditions.Effects
                    .Where(effect =>
                        !effect.Id.Equals(
                            "unkempt",
                            StringComparison.OrdinalIgnoreCase) &&
                        !effect.Id.Equals(
                            "bum",
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };

            if (conditions.SkinIssuesGameSeconds > 0d)
            {
                conditions = conditions with
                {
                    SkinIssuesGameSeconds =
                        CharacterVitalsTuning.SkinIssuesGameHours *
                        SecondsPerGameHour
                };
            }

            conditions =
                AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "rested",
                        "Отдохнувший",
                        RestedDurationRealSeconds,
                        false,
                        RestedExperienceMultiplier,
                        RestedStressSlowdownPercent));
        }

        return new CharacterVitalsUpdate(
            ClampSoft(vitals, conditions).Normalize(),
            conditions.Normalize(),
            events);
    }

    public static CharacterVitalsUpdate CompleteHotelSleep(
        PlayerVitalsState vitals,
        PlayerConditionState conditions)
    {
        var update = SleepInternal(
            vitals,
            conditions,
            HotelSleepHours,
            fullSleep: true,
            allowSleepInterruption: false);

        if (update.Events.Any(
                item => item.Kind == "SleepBlocked"))
            return update;

        var nextVitals = update.Vitals with
        {
            Energy = update.Vitals.MaxEnergy,
            Hydration = update.Vitals.MaxHydration,
            Hygiene = PlayerConditionScale.Maximum
        };

        var nextConditions = update.Conditions with
        {
            CumulativeEnergy = 0d,
            CumulativeHydration = 0d,
            CumulativeFatigue = 0d
        };

        return new CharacterVitalsUpdate(
            ClampSoft(
                nextVitals,
                nextConditions).Normalize(),
            nextConditions.Normalize(),
            update.Events.Append(
                new PlayerConditionEvent(
                    "HotelSleepCompleted")).ToArray());
    }

    public static CharacterVitalsUpdate UseItem(
        string itemId,
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        double gameHourOfDay = 12d)
    {
        vitals =
            (vitals ?? PlayerVitalsState.Default)
            .Normalize();
        conditions =
            (conditions ?? PlayerConditionState.Empty)
            .Normalize();

        var id = (itemId ?? string.Empty).Trim();
        if (id.Length == 0)
            return new CharacterVitalsUpdate(
                vitals,
                conditions,
                Array.Empty<PlayerConditionEvent>());

        // Физический профиль предмета решает, ЧЕМ он восстанавливает шкалы.
        //
        // До этого объём восстановления был задан абстрактными процентами
        // («вода = +25% жидкости»), и вместе со шкалой жидкости это
        // означало бы 3,75 литра из одной бутылки. Теперь числа берутся из
        // профиля: 500 мл воды дают 500/30 = 16,67% шкалы (шкала 3000 мл).
        var profile = CharacterConsumableCatalog.GetProfile(id);

        var nextCount =
            conditions.ItemUseCounts.TryGetValue(
                id,
                out var oldCount)
                ? Math.Max(0, oldCount) + 1
                : 1;

        var counts =
            new Dictionary<string, int>(
                conditions.ItemUseCounts,
                StringComparer.OrdinalIgnoreCase)
            {
                [id] = nextCount
            };

        conditions = conditions with
        {
            ItemUseCounts = counts,
            // Подпись пункта в мониторе ставится ДО механики предмета: пищеварение
            // читает объём порции по этому id, чтобы вычислить время усвоения.
            // Если ставить подпись только в конце, желудок получил бы порцию
            // ПРЕДЫДУЩЕГО предмета.
            LastConsumedItemId = id
        };

        var caffeineMg = CharacterConsumableCatalog.GetCaffeineMg(id);
        if (caffeineMg > 0d)
            ApplyCaffeine(
                ref vitals,
                ref conditions,
                caffeineMg,
                NormalizeHour(gameHourOfDay));

        // Питание идёт ЧЕРЕЗ ПРОФИЛЬ, а не через проценты в switch: шкалы теперь
        // измеряются в килокалориях и миллилитрах, и «вода +25%» означала бы
        // 3,75 литра. Ниже по веткам остаются только эффекты, которые к
        // питательности не относятся (стресс, усталость, метаболизм, баффы).
        //
        // ОДИН предмет — ОДНА порция. Раньше здесь стояли ДВА вызова AddResource
        // (энергия и жидкость), и каждый дописывал свою порцию: яблоко ложилось в
        // желудок как «яблоко (энергия)» и «яблоко (жидкость)» — две плитки с
        // разными таймерами, объём вдвое больше съеденного, а домашняя колбаса
        // появлялась «два раза». Складывать две порции одного съеденного не с чего:
        // объём — свойство куска, а не шкалы.
        if (profile.Feeds)
        {
            AddConsumedPortion(ref vitals, ref conditions, profile);
        }

        switch (id.ToLowerInvariant())
        {
            case "food.milk":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.MilkStressReducePercent);
                break;

            case "food.meal":
                ReduceFatigue(
                    ref vitals,
                    CharacterItemTuning.MealFatigueReducePercent);
                break;

            case "drink.lemon_tea":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.LemonTeaStressReducePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.LemonTeaMetabolismPercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.LemonTeaResiliencePercent);
                break;

            case "tea.herbal":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.HerbalTeaStressReducePercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.HerbalTeaResiliencePercent);
                break;

            case "drink.coffee":
                ReduceFatigue(
                    ref vitals,
                    CharacterItemTuning.CoffeeFatigueReducePercent);
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.CoffeeStressReducePercent);
                break;

            case "drink.energy":
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.EnergyDrinkMetabolismPercent);
                break;

            case "drink.espresso":
                ReduceFatigue(
                    ref vitals,
                    CharacterItemTuning.EspressoFatigueReducePercent);
                break;

            case "drink.black_tea":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.BlackTeaStressReducePercent);
                break;

            case "drink.green_tea":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.GreenTeaStressReducePercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.GreenTeaResiliencePercent);
                break;

            case "drink.cola":
                break;

            case "drink.decaf_coffee":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.DecafCoffeeStressReducePercent);
                break;

            case "drink.ginger_lemon_tea":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning
                        .GingerLemonTeaStressReducePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.GingerLemonTeaMetabolismPercent);
                break;

            case "drink.kvass":
                break;

            case "drink.sports":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.SportsDrinkResiliencePercent);
                break;

            case "electrolyte.sachet":
            case "supplement.electrolyte":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.ElectrolyteResiliencePercent);
                break;

            case "food.kefir":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.KefirStressReducePercent);
                break;

            case "food.yogurt":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.YogurtStressReducePercent);
                break;

            case "food.cheese":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.CheeseResiliencePercent);
                break;

            case "food.nuts":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.NutsResiliencePercent);
                break;

            case "food.oatmeal":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.OatmealStressReducePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.OatmealMetabolismPercent);
                break;

            case "food.buckwheat":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.BuckwheatResiliencePercent);
                break;

            case "food.vegetable_stew":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.VegetableStewResiliencePercent);
                break;

            case "food.soup":
                ReduceFatigue(
                    ref vitals,
                    CharacterItemTuning.SoupFatigueReducePercent);
                break;

            case "food.egg_sandwich":
                break;

            case "food.banana":
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.BananaMetabolismPercent);
                break;

            case "food.apple":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.AppleStressReducePercent);
                break;

            case "food.orange":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.OrangeResiliencePercent);
                break;

            case "food.honey":
                ReduceFatigue(
                    ref vitals,
                    CharacterItemTuning.HoneyFatigueReducePercent);
                break;

            case "food.dark_chocolate":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.DarkChocolateStressReducePercent);
                break;

            case "supplement.multivitamin":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.MultivitaminResiliencePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.MultivitaminMetabolismPercent);
                if (nextCount >=
                    CharacterItemTuning.MultivitaminBullRepeatCount)
                {
                    conditions = AddOrReplaceEffect(
                        conditions,
                        new ActivePlayerEffectState(
                            "bull",
                            "Бык",
                            CharacterItemTuning
                                .BullFromSupplementGameSeconds,
                            false));
                }
                break;

            case "vitamin.c_effervescent":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning
                        .VitaminCEffervescentResiliencePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning
                        .VitaminCEffervescentMetabolismPercent);
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning
                        .VitaminCEffervescentStressReducePercent);
                break;

            case "supplement.omega3":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.Omega3StressReducePercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.Omega3ResiliencePercent);
                break;

            case "vitamin.d3":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.VitaminD3ResiliencePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.VitaminD3MetabolismPercent);
                break;

            case "mineral.magnesium":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.MagnesiumStressReducePercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.MagnesiumResiliencePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.MagnesiumMetabolismPercent);
                if (nextCount >=
                    CharacterItemTuning.MagnesiumOverdoseRepeatCount)
                    AddStress(
                        ref conditions,
                        CharacterItemTuning
                            .MagnesiumOverdoseStressPercent);
                break;

            case "mineral.zinc":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.ZincResiliencePercent);
                if (nextCount >=
                    CharacterItemTuning.ZincOverdoseRepeatCount)
                    AddStress(
                        ref conditions,
                        CharacterItemTuning.ZincOverdoseStressPercent);
                break;

            case "adaptogen.ashwagandha":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.AshwagandhaStressReducePercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.AshwagandhaResiliencePercent);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "drowsiness",
                        "Сонливость",
                        CharacterItemTuning.DrowsinessGameSeconds,
                        true));
                break;

            case "medicine.valerian":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.ValerianStressReducePercent);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "drowsiness",
                        "Сонливость",
                        CharacterItemTuning.DrowsinessGameSeconds,
                        true));
                break;

            case "medicine.sorbent":
                AddResource(
                    ref vitals,
                    ref conditions,
                    "health",
                    CharacterItemTuning.SorbentHealthPercent);
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.SorbentStressReducePercent);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "sorbent",
                        "Сорбент",
                        CharacterItemTuning.SorbentGameSeconds,
                        false));
                break;

            case "medicine.recovery_salts":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.RecoverySaltsResiliencePercent);
                break;

            case "smoke.cigarette":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.CigaretteStressReducePercent);
                AddOvercharge(
                    ref conditions,
                    "energy",
                    CharacterItemTuning.CigaretteEnergyOverchargePercent);
                AddStress(
                    ref conditions,
                    CharacterItemTuning.CigaretteStressAddPercent);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "nicotine_rebound",
                        "Никотиновый откат",
                        CharacterItemTuning.NicotineReboundRealSeconds,
                        true));
                break;

            case "drink.beer":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.BeerStressReducePercent);
                // Обезвоживание алкоголя остаётся ЯВНЫМ минусом поверх профиля:
                // пиво даёт воду, но спирт забирает больше, и это правило не
                // должно зависеть от таблицы питательности.
                AddResource(
                    ref vitals,
                    ref conditions,
                    "hydration",
                    CharacterItemTuning.BeerHydrationChangePercent);
                ReduceFatigue(
                    ref vitals,
                    -CharacterItemTuning.BeerFatigueIncreasePercent);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "alcohol_aftereffect",
                        "Последействие алкоголя",
                        CharacterItemTuning.BeerAftereffectGameSeconds,
                        true));
                break;

            case "vitamin.c":
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.VitaminCResiliencePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.VitaminCMetabolismPercent);
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.VitaminCStressReducePercent);

                if (nextCount >=
                    CharacterItemTuning.VitaminCBullRepeatCount)
                {
                    conditions =
                        AddOrReplaceEffect(
                            conditions,
                            new ActivePlayerEffectState(
                                "bull",
                                "Бык",
                                CharacterItemTuning
                                    .BullFromSupplementGameSeconds,
                                false));
                }
                break;

            case "adaptogen.tincture":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.TinctureStressReducePercent);
                ChangeResilience(
                    ref vitals,
                    CharacterItemTuning.TinctureResiliencePercent);
                ChangeMetabolism(
                    ref vitals,
                    CharacterItemTuning.TinctureMetabolismPercent);
                break;

            case "painkiller":
                AddResource(
                    ref vitals,
                    ref conditions,
                    "health",
                    CharacterItemTuning.PainkillerHealthPercent);
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning.PainkillerStressReducePercent);
                conditions =
                    AddOrReplaceEffect(
                        conditions,
                        new ActivePlayerEffectState(
                            "analgesia",
                            "Обезболивание",
                            CharacterItemTuning.AnalgesiaGameSeconds,
                            false));

                if (nextCount >=
                    CharacterItemTuning.PainkillerOveruseRepeatCount)
                {
                    AddStress(
                        ref conditions,
                        CharacterItemTuning
                            .PainkillerOveruseStressPercent);
                    conditions =
                        AddOrReplaceEffect(
                            conditions,
                            new ActivePlayerEffectState(
                                "analgesic_overuse",
                                "Злоупотребление обезболивающими",
                                CharacterItemTuning
                                    .AnalgesicOveruseGameSeconds,
                                true));
                }
                break;

            case "soap":
                vitals = vitals with
                {
                    Hygiene = Math.Min(
                        PlayerConditionScale.Maximum,
                        vitals.Hygiene +
                        UnitsFromPercentExact(
                            CharacterVitalsTuning.SoapHygienePercent))
                };
                break;

            case "skin.ointment":
                if (conditions.SkinIssuesGameSeconds > 0d)
                {
                    conditions = conditions with
                    {
                        SkinIssuesGameSeconds =
                            Math.Min(
                                conditions.SkinIssuesGameSeconds,
                                CharacterVitalsTuning
                                    .SkinOintmentRemainingGameHours *
                                SecondsPerGameHour)
                    };
                }
                break;

            case "meat":
                break;

            case "gosha.homemade_sausage":
                ReduceStress(
                    ref conditions,
                    CharacterItemTuning
                        .HomemadeSausageStressReducePercent);
                break;

            case "water.bottle":
                // Вода даёт ТОЛЬКО жидкость, и она уже начислена по профилю
                // выше: ветка нужна лишь затем, чтобы предмет не считался
                // неизвестным.
                break;

            default:
                // Предмета нет ни в одной ветке — но он может быть обычной
                // едой или питьём, у которого есть физический профиль и нет
                // побочных эффектов. Такие предметы обрабатываются профилем
                // выше и НЕ являются неизвестными; «UnknownItem» остаётся
                // только для того, чего нет в каталоге вовсе.
                if (!profile.Feeds &&
                    !IsCatalogItem(id))
                {
                    return new CharacterVitalsUpdate(
                        vitals,
                        conditions,
                        new[]
                        {
                            new PlayerConditionEvent(
                                "UnknownItem")
                        });
                }
                break;
        }

        if (id.Equals(
                "food.milk",
                StringComparison.OrdinalIgnoreCase) &&
            nextCount >= CharacterItemTuning.MilkStrongBonesRepeatCount)
        {
            conditions =
                AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "strong_bones",
                        "Крепкие кости",
                        CharacterItemTuning.StrongBonesGameSeconds,
                        false));
        }

        return new CharacterVitalsUpdate(
            ClampSoft(vitals, conditions).Normalize(),
            conditions.Normalize(),
            Array.Empty<PlayerConditionEvent>());
    }

    public static double GetExperienceMultiplier(
        PlayerConditionState conditions) =>
        (conditions ?? PlayerConditionState.Empty)
            .Effects
            .Where(
                effect =>
                    !effect.IsDebuff &&
                    effect.RemainingRealSeconds > 0d)
            .Select(
                effect =>
                    effect.ExperienceMultiplier)
            .DefaultIfEmpty(1d)
            .Max();

    private static void AddStress(
        ref PlayerConditionState state,
        double gameSeconds,
        double resiliencePercent,
        double speedMultiplier = 1d)
    {
        var fatiguePercent =
            PlayerConditionScale.ToPercent(
                state.CumulativeFatigue);
        var baseGain =
            fatiguePercent *
            CharacterVitalsTuning
                .StressPerCumulativeFatiguePercentPerHour *
            gameSeconds /
            SecondsPerGameHour;
        if (baseGain <= 0d)
            return;

        var slowdown =
            ActiveStressSlowdownPercent(state);
        var resilienceFactor =
            Math.Clamp(
                1d -
                resiliencePercent /
                CharacterVitalsTuning.StressResilienceDivisor,
                0d,
                1d);
        var gainPercent =
            baseGain *
            Math.Clamp(
                1d - slowdown / 100d,
                0d,
                1d) *
            resilienceFactor *
            StressMultiplier(state) *
            // «Обезвоживание» ускоряет накопление стресса на 25% (задано
            // автором): множитель применяется к УЖЕ полученной скорости, а не
            // к базовой, поэтому работает поверх устойчивости, выгорания и пр.
            speedMultiplier;

        if (gainPercent <= 0d)
            return;

        var units =
            UnitsFromPercentExact(
                gainPercent);

        var normalStressUnits =
            ConsumeOvercharge(
                ref state,
                "stress",
                units,
                resiliencePercent / 2d);

        if (normalStressUnits <= 0d)
            return;

        state = state with
        {
            Stress =
                state.Stress +
                normalStressUnits
        };

    }

    /// <summary>
    /// Коэффициент ускорения накопления кумулятивной усталости.
    ///
    /// База — <see cref="CumulativeFatigueAccrualBaseMultiplier"/> (задано
    /// автором: «увеличить в 2 раза»), плюс
    /// <see cref="CumulativeFatigueAccrualStepBonus"/> за каждые ЦЕЛЫЕ
    /// <see cref="CumulativeFatigueAccrualStepPercent"/> уже накопленной
    /// кумулятивной усталости. Чем сильнее истощение, тем быстрее оно растёт —
    /// это «лавина», а не линейный рост.
    /// </summary>
    public static double CumulativeFatigueAccrualMultiplier(
        PlayerConditionState state)
    {
        var percent =
            PlayerConditionScale.ToPercent(
                state.CumulativeFatigue);
        var steps =
            Math.Floor(
                percent /
                CumulativeFatigueAccrualStepPercent);

        return CumulativeFatigueAccrualBaseMultiplier +
            steps *
            CumulativeFatigueAccrualStepBonus;
    }

    private static void AddFatigueExhaustion(
        ref PlayerConditionState state,
        double fatigueGainUnits,
        double currentSoftFatigue,
        double resiliencePercent)
    {        if (fatigueGainUnits <= 0d)
            return;

        var before =
            PlayerConditionScale.ToPercent(
                currentSoftFatigue);
        var after =
            PlayerConditionScale.ToPercent(
                currentSoftFatigue +
                fatigueGainUnits);

        if (after <= FatigueExhaustionPercent)
            return;

        var critical =
            Math.Max(
                0d,
                after -
                Math.Max(
                    FatigueExhaustionPercent,
                    before));

        if (critical <= 0d)
            return;

        var multiplier =
            after >= 100d
                ? CharacterVitalsTuning.FatigueExhaustionShare
                : CharacterVitalsTuning.FatigueExhaustionBelowFullShare;
        var units =
            UnitsFromPercentExact(
                critical * multiplier) *
            CumulativeFatigueAccrualMultiplier(state);

        if (!HasEffect(state, "bull") &&
            !SkipNegativeRoll(
                ref state,
                resiliencePercent / 2d))
        {
            state = state with
            {
                CumulativeFatigue =
                    state.CumulativeFatigue +
                    units
            };
        }
    }

    private static void AddResourceExhaustion(
        ref PlayerConditionState state,
        string resource,
        double before,
        double after,
        double demandUnits,
        double multiplier,
        double resiliencePercent)
    {
        var beforePercent =
            PlayerConditionScale.ToPercent(
                before);
        var afterPercent =
            PlayerConditionScale.ToPercent(
                after);

        if (beforePercent >= 1d &&
            afterPercent >= 1d)
            return;

        var gain =
            demandUnits * multiplier;
        if (gain <= 0d ||
            HasEffect(state, "bull") ||
            SkipNegativeRoll(
                ref state,
                resiliencePercent / 2d))
            return;

        state =
            resource.Equals(
                "energy",
                StringComparison.OrdinalIgnoreCase)
                ? state with
                {
                    CumulativeEnergy =
                        state.CumulativeEnergy +
                        gain
                }
                : state with
                {
                    CumulativeHydration =
                        state.CumulativeHydration +
                        gain
                };
    }

    private static void HealthRegenAndExhaustionDrain(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        double gameSeconds,
        bool sleeping)
    {
        if (gameSeconds <= 0d)
            return;

        // Скорость восстановления здоровья уже учитывает и нормальное состояние,
        // и бонусы за повышенные метаболизм/устойчивость, и стресс, и свободное
        // место в шкале, и запас ресурсов (см. HealthRegenPerHour). Здесь остаётся
        // только перевести «процент в час» в единицы за шаг.
        var regenPerHour = HealthRegenPerHour(vitals, state);

        if (regenPerHour > 0d)
        {
            var regen = regenPerHour * gameSeconds / SecondsPerGameHour;

            // Восстановление здоровья НЕ бесплатно: каждая единица здоровья
            // берёт 1 единицу энергии и 2 единицы жидкости (задано автором).
            // Поэтому итог — минимум из «сколько могло бы восстановиться» и
            // «на сколько хватит запасов»: без этого здоровье росло бы из
            // воздуха, а «Текущая динамика» обещала бы рост при пустой энергии.
            var healthUnits =
                UnitsFromPercentExact(regen);
            var healthRoom =
                Math.Max(
                    0d,
                    EffectiveHealthMaximum(
                        vitals,
                        state) -
                    vitals.Health);
            var affordable = Math.Min(
                vitals.Energy / HealthRegenEnergyPerHealthUnit,
                vitals.Hydration / HealthRegenHydrationPerHealthUnit);
            var restored = Math.Max(
                0d,
                Math.Min(
                    healthUnits,
                    Math.Min(healthRoom, affordable)));

            if (restored > 0d)
            {
                vitals = vitals with
                {
                    Health = vitals.Health + restored,
                    Energy = Math.Max(
                        0d,
                        vitals.Energy -
                        restored *
                        HealthRegenEnergyPerHealthUnit),
                    Hydration = Math.Max(
                        0d,
                        vitals.Hydration -
                        restored *
                        HealthRegenHydrationPerHealthUnit)
                };
            }
        }

        if (sleeping)
            return;

        var exhaustionPercent =
            PlayerConditionScale.ToPercent(
                state.CumulativeEnergy) +
            PlayerConditionScale.ToPercent(
                state.CumulativeHydration);

        if (exhaustionPercent <= 0d)
            return;

        var healthLossPercent =
            exhaustionPercent /
            CharacterVitalsTuning.ExhaustionHealthLossDivisor *
            (gameSeconds /
             SecondsPerGameQuarterHour);

        vitals = vitals with
        {
            Health =
                Math.Max(
                    0d,
                    vitals.Health -
                    UnitsFromPercentExact(
                        healthLossPercent))
        };
    }

    /// <summary>
    /// Скорость восстановления здоровья в процентах за игровой час.
    ///
    /// Модель (задана автором): базово 1% за 10 игровых минут = 6% в час, ровно
    /// при НОРМАЛЬНОМ метаболизме и устойчивости (60%). Каждое из этих свойств,
    /// поднятое выше нормы, добавляет ещё +1% «за ту же стоимость» — то есть
    /// ускоряет восстановление, не удорожая его. «Бык» даёт ПЛОСКИЕ +2% на тех же
    /// десяти минутах. Стресс замедляет (×0,75), а кумулятивный стресс — ещё
    /// сильнее (×0,5).
    ///
    /// Стоимость восстановления (1 энергия и 2 жидкости на единицу здоровья)
    /// кладётся на скорость не здесь, а в <see cref="RatesFrom"/>: подсказке
    /// нужно видеть и «сколько восстанавливается», и «сколько это стоит».
    ///
    /// Возвращаемое значение ОГРАНИЧЕНО запасами и свободным местом в шкале:
    /// обещать рост при 0,2% энергии было бы прямой ложью о поведении мира, а при
    /// полном здоровье восстановления нет вовсе, поэтому расход на него НЕ должен
    /// попадать в скорость энергии и жидкости.
    /// </summary>
    private static double HealthRegenPerHour(
        PlayerVitalsState vitals,
        PlayerConditionState state)
    {
        // Порог 1% в 10 минут = 6% в час при нормальном метаболизме/устойчивости.
        var value =
            60d / HealthRegenGameMinutesPerPercent;

        var metabolism =
            PlayerConditionScale.ToPercent(
                vitals.Metabolism);
        var resilience =
            EffectiveResiliencePercent(
                vitals,
                state);

        // «Норма» обоих свойств — 60%: выше нормы бонус, ниже — без бонуса.
        // Бонус задан в «процентах за десять минут», поэтому переводится в час
        // тем же множителем, что и база (иначе +1% превратился бы в +1% в ЧАС и
        // бонус был бы в шесть раз слабее задуманного).
        var elevatedCount = 0;
        if (metabolism > DefaultMetabolismPercent)
            elevatedCount++;
        if (resilience > DefaultResiliencePercent)
            elevatedCount++;
        value += elevatedCount *
            HealthRegenElevatedBonusPercent *
            60d / HealthRegenGameMinutesPerPercent;

        // «Бык» — ПЛОСКАЯ надбавка в тех же «процентах за десять минут», а не
        // множитель: только так обещанные автору «+2%» остаются +2% при любом
        // стрессе и метаболизме, и подсказка интерфейса показывает то же число,
        // что прибавляет движок.
        if (HasEffect(state, "bull"))
        {
            value += HealthRegenBullBonusPercent *
                60d / HealthRegenGameMinutesPerPercent;
        }

        if (TotalStressPercent(state) > 0d)
            value *= CharacterVitalsTuning.HealthRegenAnyStressMultiplier;
        if (state.CumulativeStress > 0d)
            value *=
                CharacterVitalsTuning
                    .HealthRegenCumulativeStressMultiplier;

        // Восстановление здоровья тратит энергию 1:1 и жидкость 1:2, поэтому
        // скорость, которую видит игрок, ОГРАНИЧЕНА запасами: при остатке 0,2%
        // обещать 6% в час было бы прямой ложью о поведении мира.
        //
        // Тем же ограничением служит свободное место в шкале: при полном
        // здоровье восстановления нет, и расход на него НЕ должен попадать в
        // скорость энергии и жидкости — иначе подсказка показывала бы трату,
        // которой в мире не происходит.
        //
        // Цена берётся в ЕДИНИЦАХ, а не в процентах: правило автора задано
        // именно так («1 единица здоровья стоит 1 энергии и 2 жидкости»), и
        // пересчёт в проценты шкалы здесь ничего не даёт — обе величины живут
        // на одной сетке 0..10000.
        var healthMaximum =
            EffectiveHealthMaximum(vitals, state);

        var roomPercent =
            Math.Max(
                0d,
                healthMaximum -
                vitals.Health) /
            PlayerConditionScale.UnitsPerPercent;
        var affordablePercent =
            Math.Min(
                vitals.Energy / HealthRegenEnergyPerHealthUnit,
                vitals.Hydration / HealthRegenHydrationPerHealthUnit) /
            PlayerConditionScale.UnitsPerPercent;

        return Math.Min(
            value,
            Math.Min(roomPercent, affordablePercent));
    }

    private static void ApplyHygiene(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        double gameSeconds)
    {
        var decay =
            UnitsFromPercentExact(
                100d *
                gameSeconds /
                (HygieneDecayHours *
                 SecondsPerGameHour));

        vitals = vitals with
        {
            Hygiene =
                Math.Max(
                    0d,
                    vitals.Hygiene - decay)
        };

        if (state.SkinIssuesGameSeconds > 0d)
        {
            var metabolism = PlayerConditionScale.ToPercent(vitals.Metabolism);
            var multiplier =
                metabolism < ReducedMetabolismPercent
                    ? CharacterVitalsTuning
                        .SkinIssuesReducedMetabolismSpeedFactor
                    : metabolism >= ElevatedMetabolismPercent
                        ? CharacterVitalsTuning
                            .SkinIssuesElevatedMetabolismSpeedFactor
                        : 1d;

            state = state with
            {
                SkinIssuesGameSeconds =
                    Math.Max(
                        0d,
                        state.SkinIssuesGameSeconds -
                        gameSeconds * multiplier)
            };
        }
    }

    private static void UpdateResilienceAndMetabolism(
        ref PlayerVitalsState vitals,
        PlayerConditionState state,
        double gameSeconds,
        bool sleeping,
        bool playerMoving)
    {
        var quarters =
            gameSeconds /
            SecondsPerGameQuarterHour;

        var resilience =
            PlayerConditionScale.ToPercent(
                vitals.Resilience);
        var targetResilience =
            HasEffect(state, "bum")
                ? CharacterVitalsTuning.BumResilienceCapPercent
                : DefaultResiliencePercent;
        var resilienceRatePerQuarter =
            HasEffect(state, "bum")
                ? CharacterVitalsTuning
                    .ResilienceBumReturnPercentPerQuarterHour
                : CharacterVitalsTuning
                    .ResilienceReturnPercentPerQuarterHour;

        var resilienceDelta =
            Math.Clamp(
                targetResilience - resilience,
                -resilienceRatePerQuarter * quarters,
                resilienceRatePerQuarter * quarters);

        var metabolism =
            PlayerConditionScale.ToPercent(
                vitals.Metabolism);
        var metabolismDelta =
            Math.Clamp(
                DefaultMetabolismPercent - metabolism,
                -MetabolismRecoveryPercentPerHour *
                 gameSeconds /
                 SecondsPerGameHour,
                MetabolismRecoveryPercentPerHour *
                 gameSeconds /
                 SecondsPerGameHour);

        // Штраф за истощение НЕ действует во сне — ровно как и начисление самого
        // истощения (см. Character_Vitals.md: «во время сна не может накапливаться
        // истощение каких-либо показателей»).
        //
        // Без этого правила ночлег в гостинице обнулял метаболизм: семь часов
        // сна шли ДО того, как снималось истощение, и все четыре шкалы ещё были
        // на месте — минус 8% каждые 15 минут против 1% в час восстановления.
        // Игрок видел метаболизм 0 сразу после ночлега.
        if (!sleeping)
        {
            metabolismDelta -=
                ExhaustionScales(state) *
                MetabolismExhaustionPenaltyPerQuarterHour *
                quarters;

            // «Жажда» и «Обезвоживание» дополнительно отнимают метаболизм:
            // 1% и 2% за каждые 10 игровых минут соответственно (задано
            // автором: «снижение метаболизма на 1% за 10 игровых минут», а под
            // обезвоживанием «удваивается потеря метаболизма»). Наказание
            // висит, ПОКА шкала ниже порога, а не выдаётся разово: иначе игрок
            // терял бы метаболизм один раз и дальше не замечал жажды вовсе.
            //
            // Во сне не применяется — по тому же правилу «во сне истощение не
            // накапливается»: иначе ночлег с жаждой обнулял метаболизм, а утром
            // игрок не понимал, почему.
            var hydrationPercent = Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(vitals, state));
            var perTenMinutesDelta = hydrationPercent <
                DehydrationThresholdPercent
                ? DehydrationMetabolismPercentPerTenMinutes
                : hydrationPercent < ThirstThresholdPercent
                    ? ThirstMetabolismPercentPerTenMinutes
                    : 0d;
            if (perTenMinutesDelta > 0d)
            {
                metabolismDelta -=
                    perTenMinutesDelta *
                    // «За 10 минут» — это 6 раз по 10 минут в игровом часу.
                    (gameSeconds / 600d);
            }
        }

        // «Обезвоживание: начинает падать устойчивость 1:1» — задано автором.
        // Устойчивость падает с ТОЙ ЖЕ скоростью, с которой расходуется
        // жидкость, и падение продолжается даже при нулевой шкале (автор:
        // «даже если жидкости уже не осталось, скорость расхода аналогичная»):
        // именно поэтому скорость берётся из НОРМЫ расхода, а не из фактической
        // убыли — при нуле фактическая убыль равна нулю, и дебафф исчез бы
        // ровно в самый опасный момент.
        if (!sleeping &&
            Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(vitals, state)) <
            DehydrationThresholdPercent)
        {
            var hydrationDrainPercentPerHour =
                HydrationConsumptionPercentPerHour(playerMoving) *
                MetabolismConsumptionFactor(vitals, state);

            resilienceDelta -=
                hydrationDrainPercentPerHour *
                gameSeconds /
                SecondsPerGameHour;
        }

        vitals = vitals with
        {
            Resilience =
                UnitsFromPercentExact(
                    Math.Clamp(
                        resilience + resilienceDelta,
                        0d,
                        HasEffect(state, "bum")
                            ? CharacterVitalsTuning
                                .BumResilienceCapPercent
                            : 100d)),
            Metabolism =
                UnitsFromPercentExact(
                    Math.Clamp(
                        metabolism + metabolismDelta,
                        0d,
                        100d))
        };
    }

    /// <summary>
    /// Сколько шкал истощения накоплено (0..4): стресс, усталость, жидкость,
    /// энергия. Ровно это число движок называет в правилах метаболизма —
    /// «минус 2% за каждую имеющуюся шкалу истощения за 15 игровых минут».
    ///
    /// Метод выделен, потому что одно и то же число нужно в ТРЁХ местах:
    /// начисление метаболизма, его подсказка (<see cref="MetabolismDirection"/>)
    /// и регенерация здоровья. Разъехавшись, они показали бы игроку одно, а
    /// посчитали другое.
    /// </summary>
    private static int ExhaustionScales(
        PlayerConditionState state) =>
        (state.CumulativeStress > 0d ? 1 : 0) +
        (state.CumulativeFatigue > 0d ? 1 : 0) +
        (state.CumulativeHydration > 0d ? 1 : 0) +
        (state.CumulativeEnergy > 0d ? 1 : 0);

    private static void SyncThresholdEffects(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        List<PlayerConditionEvent> events,
        bool hydrationDigesting,
        bool hydrationNetPositive)
    {
        var hygiene =
            Percent(
                vitals.Hygiene,
                PlayerConditionScale.Maximum);

        if (hygiene < CharacterVitalsTuning.UnkemptHygienePercent)
        {
            state = EnsureEffect(
                state,
                new ActivePlayerEffectState(
                    "unkempt",
                    "Неопрятный",
                    1e12,
                    true));
        }

        if (hygiene < CharacterVitalsTuning.BumHygienePercent)
        {
            var hadBum =
                HasEffect(
                    state,
                    "bum");

            state = EnsureEffect(
                state,
                new ActivePlayerEffectState(
                    "bum",
                    "Бомж",
                    1e12,
                    true));

            if (!hadBum &&
                !SkipNegativeRoll(
                    ref state,
                    EffectiveResiliencePercent(
                        vitals,
                        state) /
                    2d))
            {
                state = state with
                {
                    SkinIssuesGameSeconds =
                        CharacterVitalsTuning.SkinIssuesGameHours *
                        SecondsPerGameHour,
                    Stress =
                        UnitsFromPercentExact(
                            CharacterVitalsTuning
                                .SkinIssuesStressPercent)
                };

                vitals = vitals with
                {
                    Health =
                        Math.Max(
                            0d,
                            vitals.Health -
                            UnitsFromPercentExact(
                                CharacterVitalsTuning
                                    .SkinIssuesHealthLossPercent))
                };

                events.Add(
                    new PlayerConditionEvent(
                        "SkinProblemsApplied"));
            }
        }

        // ── «Жажда» и «Обезвоживание» ────────────────────────────────────────
        //
        // Пороги считаются от ДОСТУПНОГО максимума (истощение жидкости
        // уменьшает потолок), а не от номинала: «ниже 1500 мл» на шкале, у
        // которой истощение съело верх, наступает раньше — и это правильно,
        // потому что сравнивать надо с тем, сколько игрок реально может иметь.
        var hydrationPercent =
            Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(
                    vitals,
                    state));

        var thirstActive =
            hydrationPercent < ThirstThresholdPercent;
        var dehydrationActive =
            hydrationPercent < DehydrationThresholdPercent &&
            !hydrationNetPositive;

        // «Жажда» снимается, пока в желудке усваивается жидкость (задано
        // автором: «если в желудке усваивается что-то с жидкостью, эффект жажды
        // сразу удаляется») — то есть игрок уже пьёт, и дебафф не должен
        // висеть до тех пор, пока вода доберётся до шкалы.
        if (thirstActive &&
            !hydrationDigesting)
        {
            var hadThirst =
                HasEffect(state, ThirstEffectId);

            state = EnsureEffect(
                state,
                new ActivePlayerEffectState(
                    ThirstEffectId,
                    "Жажда",
                    1e12,
                    true));

            if (!hadThirst)
                events.Add(new PlayerConditionEvent("ThirstApplied"));
        }
        else
        {
            state = RemoveEffect(state, ThirstEffectId);
        }

        if (dehydrationActive)
        {
            var hadDehydration =
                HasEffect(state, DehydrationEffectId);

            state = EnsureEffect(
                state,
                new ActivePlayerEffectState(
                    DehydrationEffectId,
                    "Обезвоживание",
                    1e12,
                    true));

            if (!hadDehydration)
                events.Add(new PlayerConditionEvent("DehydrationApplied"));
        }
        else
        {
            // Условие «активен» уже содержит требование НЕ положительной
            // динамики, поэтому ветвь «иначе» снимает дебафф ровно в тот
            // момент, когда игрок добился ЗЕЛЁНОЙ динамики жидкости (задано
            // автором) — либо когда шкала поднялась выше порога.
            state = RemoveEffect(state, DehydrationEffectId);
        }
    }

    private static void TryActivatePowerSurge(
        ref PlayerConditionState state,
        PlayerVitalsState vitals,
        List<PlayerConditionEvent> events)
    {
        if (HasEffect(state, "power_surge"))
            return;

        var healthy =
            Percent(
                vitals.Health,
                EffectiveHealthMaximum(
                    vitals,
                    state)) >=
            CharacterVitalsTuning.PowerSurgeFullScalePercent;
        var energy =
            Percent(
                vitals.Energy,
                EffectiveEnergyMaximum(
                    vitals,
                    state)) >=
            CharacterVitalsTuning.PowerSurgeFullScalePercent;
        var hydration =
            Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(
                    vitals,
                    state)) >=
            CharacterVitalsTuning.PowerSurgeFullScalePercent;
        var rested =
            Percent(
                vitals.Fatigue,
                vitals.MaxFatigue) <=
            CharacterVitalsTuning.PowerSurgeMaxFatiguePercent;
        var calm =
            TotalStressPercent(state) <=
            CharacterVitalsTuning.PowerSurgeMaxStressPercent;
        var hygiene =
            Percent(
                vitals.Hygiene,
                PlayerConditionScale.Maximum) >=
            CharacterVitalsTuning.PowerSurgeMinHygienePercent;
        var resilience =
            EffectiveResiliencePercent(
                vitals,
                state) >
            CharacterVitalsTuning.PowerSurgeMinResiliencePercent;
        var metabolism =
            PlayerConditionScale.ToPercent(
                vitals.Metabolism) >
            CharacterVitalsTuning.PowerSurgeMinMetabolismPercent;

        if (healthy && energy && hydration &&
            rested && calm && hygiene &&
            resilience && metabolism)
        {
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "power_surge",
                    "Прилив сил",
                    PowerSurgeDurationRealSeconds,
                    false));

            events.Add(
                new PlayerConditionEvent(
                    "PowerSurgeApplied"));
        }
    }

    private static double StressMultiplier(
        PlayerConditionState state) =>
        HasEffect(state, "bum")
            ? CharacterVitalsTuning.BumStressMultiplier
            : HasEffect(state, "unkempt")
                ? CharacterVitalsTuning.UnkemptStressMultiplier
                : 1d;

    /// <summary>
    /// Стресс в процентах (мгновенный + кумулятивный). Правило живёт в
    /// <see cref="PlayerConditionEngine"/>, чтобы шкалы не разошлись между
    /// двумя движками; здесь — только короткое имя для расчётов.
    /// </summary>
    private static double TotalStressPercent(
        PlayerConditionState state) =>
        PlayerConditionEngine.TotalStressPercent(state);

    private static double MetabolismConsumptionFactor(
        PlayerVitalsState vitals,
        PlayerConditionState state)
    {
        var metabolism =
            PlayerConditionScale.ToPercent(
                vitals.Metabolism);

        var factor =
            metabolism < ReducedMetabolismPercent
                ? CharacterVitalsTuning
                    .ReducedMetabolismConsumptionFactor
                : metabolism >= ElevatedMetabolismPercent
                    ? CharacterVitalsTuning
                        .ElevatedMetabolismConsumptionFactor
                    : 1d;

        return HasEffect(state, "bull")
            ? factor * CharacterVitalsTuning.BullMetabolismConsumptionFactor
            : factor;
    }

    private static double TimeOfDayFatigueMultiplier(
        double hour)
    {
        var normalized =
            NormalizeHour(hour);

        return normalized >=
                   CharacterVitalsTuning.DayFatigueStartHour &&
               normalized <
                   CharacterVitalsTuning.NightFatigueStartHour
            ? 1d
            : normalized >=
                  CharacterVitalsTuning.NightFatigueStartHour &&
              normalized <
                  CharacterVitalsTuning.DeepNightFatigueStartHour
                ? CharacterVitalsTuning.NightFatigueMultiplier
                : CharacterVitalsTuning.DeepNightFatigueMultiplier;
    }

    private static double ResilienceDirection(
        PlayerVitalsState vitals,
        PlayerConditionState state,
        bool moving)
    {
        var current =
            PlayerConditionScale.ToPercent(
                vitals.Resilience);
        var target =
            HasEffect(state, "bum")
                ? CharacterVitalsTuning.BumResilienceCapPercent
                : DefaultResiliencePercent;
        var rate =
            HasEffect(state, "bum")
                ? CharacterVitalsTuning
                    .ResilienceBumReturnPercentPerQuarterHour
                : CharacterVitalsTuning
                    .ResilienceReturnPercentPerQuarterHour;

        var direction =
            Math.Clamp(
                target - current,
                -rate,
                rate);

        // «Обезвоживание»: устойчивость падает 1:1 с расходом жидкости (задано
        // автором) в ДОПОЛНЕНИЕ к возврату к номиналу. Подсказка обязана
        // показать это тем же числом, что и движок: скорость берётся из нормы
        // расхода жидкости и переводится из «% в час» в «% в минуту».
        if (Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(vitals, state)) <
            DehydrationThresholdPercent)
        {
            direction -=
                HydrationConsumptionPercentPerHour(moving) *
                MetabolismConsumptionFactor(vitals, state) /
                60d;
        }

        return direction;
    }

    private static double MetabolismDirection(
        PlayerVitalsState vitals,
        PlayerConditionState state)
    {
        var current =
            PlayerConditionScale.ToPercent(
                vitals.Metabolism);

        var value =
            Math.Clamp(
                DefaultMetabolismPercent - current,
                -MetabolismRecoveryPercentPerHour /
                 4d,
                MetabolismRecoveryPercentPerHour /
                 4d);

        var direction =
            value -
            ExhaustionScales(state) *
            MetabolismExhaustionPenaltyPerQuarterHour;

        // «Жажда» и «Обезвоживание» в подсказке обязаны читаться теми же
        // числами, что и начисляются: 1% и 2% за 10 игровых минут. Делим на 15
        // (четверть часа в минутах — 15), потому что подсказка показывает
        // процент за ЧЕТВЕРТЬ часа, как и штраф истощения выше.
        var hydrationPercent = Percent(
            vitals.Hydration,
            EffectiveHydrationMaximum(vitals, state));
        var debuffPerQuarterHour = hydrationPercent <
            DehydrationThresholdPercent
            ? DehydrationMetabolismPercentPerTenMinutes * 1.5d
            : hydrationPercent < ThirstThresholdPercent
                ? ThirstMetabolismPercentPerTenMinutes * 1.5d
                : 0d;

        return direction - debuffPerQuarterHour;
    }

    private static double EffectiveResiliencePercent(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        HasEffect(state, "bum")
            ? Math.Min(
                CharacterVitalsTuning.BumResilienceCapPercent,
                PlayerConditionScale.ToPercent(
                    vitals.Resilience))
            : PlayerConditionScale.ToPercent(
                vitals.Resilience);

    private static void ApplyNegativeChange(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        string key,
        double units,
        bool fillDirection,
        double resiliencePercent)
    {
        if (units <= 0d)
            return;

        var remaining =
            ConsumeOvercharge(
                ref state,
                key,
                units,
                resiliencePercent / 2d);

        if (remaining <= 0d)
            return;

        if (fillDirection &&
            key.Equals(
                "fatigue",
                StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Fatigue =
                    vitals.Fatigue + remaining
            };
        }
        else if (key.Equals(
                     "energy",
                     StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Energy =
                    Math.Max(
                        0d,
                        vitals.Energy - remaining)
            };
        }
        else if (key.Equals(
                     "hydration",
                     StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Hydration =
                    Math.Max(
                        0d,
                        vitals.Hydration - remaining)
            };
        }
        else if (key.Equals(
                     "health",
                     StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Health =
                    Math.Max(
                        0d,
                        vitals.Health - remaining)
            };
        }
    }

    private static double ConsumeOvercharge(
        ref PlayerConditionState state,
        string key,
        double units)
    {
        if (units <= 0d)
            return 0d;

        var available =
            GetOvercharge(
                state,
                key);
        var consumed =
            Math.Min(
                available,
                units);

        if (consumed > 0d)
        {
            state =
                SetOvercharge(
                    state,
                    key,
                    available - consumed);
        }

        return units - consumed;
    }

    /// <summary>
    /// То же поглощение перегрузкой, но с шансом ПРОПУСТИТЬ порцию негатива:
    /// устойчивость, делённая на два, даёт шанс «не получить очередную порцию».
    /// При срабатывании перегрузка НЕ расходуется и прирост не применяется —
    /// возвращается ноль, то есть негативное изменение целиком пропущено.
    /// Порядок важен: бросок делается ДО чтения перегрузки, иначе шанс
    /// зависел бы от её величины.
    /// </summary>
    private static double ConsumeOvercharge(
        ref PlayerConditionState state,
        string key,
        double units,
        double skipChancePercent)
    {
        if (units <= 0d)
            return 0d;

        var available = GetOvercharge(state, key);
        if (available <= 0d)
            return ConsumeOvercharge(
                ref state,
                key,
                units);

        if (SkipNegativeRoll(
                ref state,
                skipChancePercent))
            return 0d;

        return ConsumeOvercharge(
            ref state,
            key,
            units);
    }

    private static void ApplyCaffeine(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        double caffeineMg,
        double gameHour)
    {
        var dailyBefore = state.CaffeineDailyMg;
        var dailyAfter = dailyBefore + caffeineMg;
        var adaptation = Math.Clamp(
            state.CaffeineDependence / 100d,
            0d,
            CharacterVitalsTuning.CaffeineMaxAdaptation);
        var effectiveDose =
            caffeineMg *
            (1d -
             adaptation *
             CharacterVitalsTuning.CaffeineAdaptationDosePenalty);

        AddOvercharge(
            ref state,
            "energy",
            Math.Max(
                CharacterVitalsTuning.CaffeineMinEnergyOvercharge,
                effectiveDose /
                CharacterVitalsTuning.CaffeineEnergyOverchargeDivisor));
        ReduceFatigue(
            ref vitals,
            Math.Min(
                CharacterVitalsTuning.CaffeineMaxFatigueReliefPercent,
                CharacterVitalsTuning.CaffeineBaseFatigueReliefPercent +
                effectiveDose /
                CharacterVitalsTuning.CaffeineFatigueReliefDoseDivisor));

        state = state with
        {
            CaffeineLoadMg =
                state.CaffeineLoadMg + caffeineMg,
            CaffeineDailyMg = dailyAfter,
            CaffeineDependence =
                Math.Clamp(
                    state.CaffeineDependence +
                    caffeineMg /
                    CharacterVitalsTuning.CaffeineDependenceDivisor *
                    CharacterVitalsTuning.CaffeineDependenceGainFactor,
                    0d,
                    100d)
        };

        if (dailyBefore <= CharacterVitalsTuning.CaffeineOveruseDailyMg &&
            dailyAfter > CharacterVitalsTuning.CaffeineOveruseDailyMg)
            AddStress(
                ref state,
                CharacterVitalsTuning.CaffeineOveruseStressPercent);

        if (dailyBefore <= CharacterVitalsTuning.CaffeineStressDailyMg &&
            dailyAfter > CharacterVitalsTuning.CaffeineStressDailyMg)
        {
            AddStress(
                ref state,
                CharacterVitalsTuning.CaffeineStressPercent);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_overuse",
                    "Кофеин — перенапряжение",
                    CharacterVitalsTuning
                        .CaffeineOveruseDurationGameSeconds,
                    true));
        }

        if (dailyBefore <= CharacterVitalsTuning.CaffeineExcessDailyMg &&
            dailyAfter > CharacterVitalsTuning.CaffeineExcessDailyMg)
        {
            AddStress(
                ref state,
                CharacterVitalsTuning.CaffeineExcessStressPercent);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_excess",
                    "Кофеин — избыток",
                    CharacterVitalsTuning
                        .CaffeineOveruseDurationGameSeconds,
                    true));
        }

        if (caffeineMg >= CharacterVitalsTuning.CaffeineJitterDoseMg)
        {
            AddStress(
                ref state,
                CharacterVitalsTuning.CaffeineJitterStressPercent);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_jitter",
                    "Кофеиновая возбудимость",
                    CharacterVitalsTuning.CaffeineJitterDurationRealSeconds,
                    true));
        }

        if (gameHour >= CharacterVitalsTuning.LateCaffeineStartHour ||
            gameHour < CharacterVitalsTuning.LateCaffeineEndHour)
        {
            AddStress(
                ref state,
                CharacterVitalsTuning.LateCaffeineStressPercent);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "late_caffeine",
                    "Поздний кофеин",
                    CharacterVitalsTuning.LateCaffeineDurationGameSeconds,
                    true));
        }
    }

    private static void UpdateCaffeine(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        double gameSeconds,
        double gameHour,
        bool sleeping,
        List<PlayerConditionEvent> events)
    {
        if (gameSeconds <= 0d)
            return;

        var hours = gameSeconds / SecondsPerGameHour;
        var loadBefore = state.CaffeineLoadMg;
        var loadAfter =
            loadBefore * Math.Pow(
                0.5d,
                hours / CharacterVitalsTuning.CaffeineHalfLifeGameHours);

        var daySeconds =
            state.CaffeineDaySeconds + gameSeconds;
        var dailyMg = state.CaffeineDailyMg;

        var caffeineDaySeconds =
            CharacterVitalsTuning.CaffeineDayGameHours *
            3600d;

        while (daySeconds >= caffeineDaySeconds)
        {
            daySeconds -= caffeineDaySeconds;
            dailyMg = 0d;
        }

        var dependence =
            Math.Max(
                0d,
                state.CaffeineDependence -
                hours * CharacterVitalsTuning.CaffeineDependenceDecayPerHour);

        state = state with
        {
            CaffeineLoadMg = Math.Max(
                0d,
                loadAfter),
            CaffeineDailyMg = dailyMg,
            CaffeineDaySeconds = Math.Max(
                0d,
                daySeconds),
            CaffeineDependence =
                Math.Clamp(
                    dependence,
                    0d,
                    100d)
        };

        if (HasEffect(state, "power_surge"))
            return;

        if (dependence >= CharacterVitalsTuning.CaffeineWithdrawalDependencePercent &&
            loadBefore >= CharacterVitalsTuning.CaffeineWithdrawalLoadBeforeMg &&
            loadAfter < CharacterVitalsTuning.CaffeineWithdrawalLoadAfterMg &&
            !HasEffect(
                state,
                "caffeine_withdrawal"))
        {
            vitals = vitals with
            {
                Fatigue =
                    vitals.Fatigue +
                    UnitsFromPercentExact(
                        sleeping
                            ? CharacterVitalsTuning
                                .CaffeineWithdrawalSleepFatiguePercent
                            : CharacterVitalsTuning
                                .CaffeineWithdrawalAwakeFatiguePercent)
            };

            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_withdrawal",
                    "Кофеиновый откат",
                    CharacterVitalsTuning
                        .CaffeineWithdrawalDurationGameSeconds,
                    true));

            AddStress(
                ref state,
                sleeping
                    ? CharacterVitalsTuning
                        .CaffeineWithdrawalSleepStressPercent
                    : CharacterVitalsTuning
                        .CaffeineWithdrawalAwakeStressPercent);

            events.Add(
                new PlayerConditionEvent(
                    "CaffeineWithdrawal"));
        }
    }

    private static double GetOvercharge(
        PlayerConditionState state,
        string key) =>
        key.ToLowerInvariant() switch
        {
            "health" => state.OverchargeHealth,
            "energy" => state.OverchargeEnergy,
            "hydration" => state.OverchargeHydration,
            "fatigue" => state.OverchargeFatigue,
            "stress" => state.OverchargeStress,
            _ => 0d
        };

    private static PlayerConditionState SetOvercharge(
        PlayerConditionState state,
        string key,
        double value) =>
        key.ToLowerInvariant() switch
        {
            "health" => state with
            {
                OverchargeHealth =
                    Math.Max(0d, value)
            },
            "energy" => state with
            {
                OverchargeEnergy =
                    Math.Max(0d, value)
            },
            "hydration" => state with
            {
                OverchargeHydration =
                    Math.Max(0d, value)
            },
            "fatigue" => state with
            {
                OverchargeFatigue =
                    Math.Max(0d, value)
            },
            "stress" => state with
            {
                OverchargeStress =
                    Math.Max(0d, value)
            },
            _ => state
        };

    private static void AddOvercharge(
        ref PlayerConditionState state,
        string key,
        double percent)
    {
        var multiplier =
            HasEffect(state, "bull")
                ? CharacterItemTuning.BullOverchargeMultiplier
                : 1d;
        var units =
            UnitsFromPercentExact(
                percent * multiplier);

        state =
            SetOvercharge(
                state,
                key,
                GetOvercharge(
                    state,
                    key) + units);
    }

    /// <summary>
    /// Кладёт ОДИН съеденный предмет в желудок как ОДНУ порцию — с обеими
    /// шкалами сразу.
    ///
    /// Зачем отдельно от <see cref="AddResource"/>. Раньше питание шло двумя
    /// вызовами (энергия и жидкость), и каждый дописывал СВОЮ порцию. Яблоко
    /// (90 ккал, 155 мл) ложилось в желудок как две записи — «яблоко (энергия)»
    /// и «яблоко (жидкость)» — с разными таймерами (2 мин и 7 мин), общим
    /// объёмом вдвое больше съеденного и двумя плитками в мониторе. Домашняя
    /// колбаса появлялась «два раза», апельсин — тоже; шоколад давал две
    /// плитки, одна из которых исчезала через секунды.
    ///
    /// ПОЧЕМУ СКЛАДЫВАТЬ БЫЛО НЕЛЬЗЯ. Объём порции — свойство КУСКА, а не шкалы:
    /// доля желудка берётся из массы предмета. Две порции по 0,18 л описывали бы
    /// 360 г съеденного вместо 180 г, и желудок переполнялся бы вдвое быстрее
    /// реального. Поэтому одна порция несёт оба остатка и обе ставки, а объём
    /// берётся один раз — по всему предмету.
    ///
    /// ПОЧЕМУ НЕ «ОБЩАЯ СТАВКА». Соблазн свести порцию к одной ставке выглядит
    /// физиологичнее, но еда и питьё усваиваются с РАЗНЫХ скоростях: полный
    /// желудок еды уходит за <see cref="CharacterDigestion.FoodStomachEmptyHours"/>
    /// часов, жидкость — по <see cref="CharacterDigestion.HydrationAbsorptionLitersPerHour"/>
    /// литра в час. Одна ставка на обе шкалы заставила бы воду усваиваться со
    /// скоростью еды (или наоборот), и таймер порции перестал бы отвечать
    /// содержимому. Поэтому у порции по-прежнему ДВЕ ставки — но у ОДНОЙ порции.
    ///
    /// Отрицательные проценты (алкоголь обезвоживает) действуют МГНОВЕННО и в
    /// желудок не попадают: желудок замедляет только ВОССТАНОВЛЕНИЕ, а не
    /// «выпивание в минус». Порция без питательности (таблетка, сигарета)
    /// желудок не занимает — <see cref="CharacterDigestion.Begin"/> её не создаёт.
    /// </summary>
    private static void AddConsumedPortion(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        ConsumableProfile profile)
    {
        // Отрицательное — отнимаем сразу, порцию не заводим.
        if (profile.EnergyPercent < 0d || profile.HydrationPercent < 0d)
        {
            vitals = vitals with
            {
                Energy = Math.Max(
                    0d,
                    vitals.Energy +
                    UnitsFromPercentExact(Math.Min(0d, profile.EnergyPercent))),
                Hydration = Math.Max(
                    0d,
                    vitals.Hydration +
                    UnitsFromPercentExact(Math.Min(0d, profile.HydrationPercent)))
            };
        }

        var energyPercent = Math.Max(0d, profile.EnergyPercent);
        var hydrationPercent = Math.Max(0d, profile.HydrationPercent);

        if (energyPercent <= 0d && hydrationPercent <= 0d)
            return;

        // «Прилив сил» замораживает шкалы: восстановление от еды не должно
        // становиться единственным способом обойти заморозку.
        if (HasEffect(state, "power_surge"))
            return;

        var metabolism = PlayerConditionScale.ToPercent(vitals.Metabolism);

        state = state with
        {
            Stomach = CharacterDigestion.Begin(
                state.Stomach,
                // ЧТО съедено запоминает САМА порция, а не одно общее поле
                // «последний предмет»: иначе Апельсин стирал бы Воду, хотя вода
                // всё ещё переваривается.
                state.LastConsumedItemId,
                energyPercent,
                hydrationPercent,
                vitals.MaxEnergy,
                vitals.MaxHydration,
                elevatedMetabolism: metabolism >= ElevatedMetabolismPercent,
                reducedMetabolism: metabolism < ReducedMetabolismPercent,
                // Объём порции: он задаёт ВРЕМЯ переваривания, а не объём
                // восстановления, и берётся ПО ВСЕМУ предмету — один раз.
                // Минимум 0,01 отсекает вырождение у предметов с нулевой массой,
                // у которых всё же есть питательность.
                portionFraction: Math.Max(
                    0.01d,
                    profile.PortionFraction > 0d
                        ? profile.PortionFraction
                        : CharacterConsumableCatalog.GetPortionFractionOrDefault(
                            state.LastConsumedItemId)))
        };
    }

    /// <summary>
    /// Прибавляет ОДНУ шкалу мгновенно. Осталось только для «здоровья» и
    /// отрицательных значений — питательность еды идёт через
    /// <see cref="AddConsumedPortion"/>, чтобы не заводить по порции на каждую
    /// шкалу одного и того же съеденного предмета.
    /// </summary>
    private static void AddResource(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        string key,
        double percent)
    {
        if (key.Equals(
                "health",
                StringComparison.OrdinalIgnoreCase))
        {            vitals = vitals with
            {
                Health =
                    vitals.Health +
                    UnitsFromPercentExact(percent)
            };
            return;
        }

        var isEnergy =
            key.Equals(
                "energy",
                StringComparison.OrdinalIgnoreCase);
        var isHydration =
            key.Equals(
                "hydration",
                StringComparison.OrdinalIgnoreCase);

        if (!isEnergy && !isHydration)
            return;

        // Отрицательное значение — это ОТНИМАНИЕ (например, алкоголь
        // обезвоживает), и оно обязано действовать мгновенно: желудок лишь
        // замедляет ВОССТАНОВЛЕНИЕ, но не «выпивание в минус».
        if (percent > 0d)
            return;

        if (isEnergy)
        {
            vitals = vitals with
            {
                Energy = Math.Max(
                    0d,
                    vitals.Energy + UnitsFromPercentExact(percent))
            };
        }
        else
        {
            vitals = vitals with
            {
                Hydration = Math.Max(
                    0d,
                    vitals.Hydration + UnitsFromPercentExact(percent))
            };
        }
    }

    /// <summary>
    /// Есть ли предмет в каталоге приложения.
    ///
    /// Нужно, чтобы отличить «предмет без побочных эффектов» (он уже начислен
    /// профилем и не является неизвестным) от действительно неизвестного Id.
    /// Каталог — контент приложения, и его нельзя продублировать в движке.
    /// </summary>
    private static bool IsCatalogItem(string itemId) =>
        ItemCatalogFactory.Items.Any(
            item => item.Id.Equals(
                itemId,
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Можно ли УПОТРЕБИТЬ предмет: попадёт ли он в желудок и подействует ли.
    ///
    /// Зачем отдельный ответ. Монитор открывает по ПКМ список употребимого
    /// («каталог существующих объектов, которые съедобны», по словам автора), и
    /// состав списка — знание О ПРЕДМЕТАХ, а не о разметке. Решение принимает
    /// движок: съедобное — это то, что либо несёт питательность в желудок
    /// (пищевой профиль), либо имеет собственную ветку эффектов в
    /// <see cref="UseItem"/>. Без этой проверки список пришлось бы собирать
    /// перечислением веток на странице, и он разошёлся бы с механикой при первой
    /// же правке.
    ///
    /// Мыло и мазь в списке ЕСТЬ: у них нет калорий, но есть эффект, и автор
    /// назвал их в требованиях к меню («помылся за 35% гигиены»).
    /// </summary>
    public static bool CanConsume(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return false;

        // Питательность: вода и еда попадают в желудок и кормят шкалы.
        if (CharacterConsumableCatalog.GetProfile(itemId).Grams > 0d)
            return true;

        // Эффекты: у таблеток, добавок, сигареты и бытовых предметов есть своя
        // ветка в UseItem, даже когда калорий у них нет.
        return ConsumableEffectItems.Contains(itemId);
    }

    /// <summary>
    /// Предметы БЕЗ пищевого профиля, у которых есть ветка эффектов в
    /// <see cref="UseItem"/>.
    ///
    /// Список нужен только для ответа «можно употребить» — сама механика живёт
    /// в switch UseItem, и здесь перечислены те же Id. Разойтись они могут
    /// только вместе с правкой switch, и тогда это заметно сразу: предмет
    /// пропадёт из меню желудка.
    /// </summary>
    private static readonly HashSet<string> ConsumableEffectItems =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "food.milk",
            "food.meal",
            "meat",
            "gosha.homemade_sausage",
            "supplement.multivitamin",
            "vitamin.c",
            "vitamin.c_effervescent",
            "supplement.omega3",
            "vitamin.d3",
            "mineral.magnesium",
            "mineral.zinc",
            "adaptogen.ashwagandha",
            "adaptogen.tincture",
            "medicine.valerian",
            "medicine.sorbent",
            "medicine.recovery_salts",
            "painkiller",
            "smoke.cigarette",
            "soap",
            "skin.ointment",
            "water.bottle"
        };

    private static void ReduceFatigue(        ref PlayerVitalsState vitals,
        double percent) =>
        vitals = vitals with
        {
            Fatigue =
                Math.Max(
                    0d,
                    vitals.Fatigue -
                    UnitsFromPercentExact(
                        percent))
        };

    private static void ReduceStress(
        ref PlayerConditionState state,
        double percent) =>
        state = state with
        {
            Stress =
                Math.Max(
                    0d,
                    state.Stress -
                    UnitsFromPercentExact(
                        percent))
        };

    private static void AddStress(
        ref PlayerConditionState state,
        double percent) =>
        state = state with
        {
            Stress =
                state.Stress +
                UnitsFromPercentExact(
                    percent)
        };

    private static void ChangeResilience(
        ref PlayerVitalsState vitals,
        double percent) =>
        vitals = vitals with
        {
            Resilience =
                vitals.Resilience +
                UnitsFromPercentExact(
                    percent)
        };

    private static void ChangeMetabolism(
        ref PlayerVitalsState vitals,
        double percent) =>
        vitals = vitals with
        {
            Metabolism =
                vitals.Metabolism +
                UnitsFromPercentExact(
                    percent)
        };

    private static PlayerVitalsState ClampSoft(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        vitals with
        {
            Health =
                Math.Clamp(
                    vitals.Health,
                    0d,
                    EffectiveHealthMaximum(
                        vitals,
                        state)),
            Energy =
                Math.Clamp(
                    vitals.Energy,
                    0d,
                    EffectiveEnergyMaximum(
                        vitals,
                        state)),
            Hydration =
                Math.Clamp(
                    vitals.Hydration,
                    0d,
                    EffectiveHydrationMaximum(
                        vitals,
                        state)),
            Fatigue =
                Math.Clamp(
                    vitals.Fatigue,
                    0d,
                    Math.Max(
                        0d,
                        vitals.MaxFatigue -
                        state.CumulativeFatigue))
        };

    private static double EffectiveHealthMaximum(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        EffectiveConsumableMaximum(
            vitals.MaxHealth,
            state.CumulativeHealth);

    private static double EffectiveEnergyMaximum(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        EffectiveConsumableMaximum(
            vitals.MaxEnergy,
            state.CumulativeEnergy);

    private static double EffectiveHydrationMaximum(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        EffectiveConsumableMaximum(
            vitals.MaxHydration,
            state.CumulativeHydration);

    private static double EffectiveConsumableMaximum(
        double maximum,
        double cumulative) =>
        Math.Max(
            0d,
            maximum *
            (1d -
             Math.Clamp(
                 PlayerConditionScale.ToPercent(
                     cumulative),
                 0d,
                 100d) /
             100d));

    private static double UnitsFromPercentExact(double percent) =>
        double.IsFinite(percent)
            ? percent * PlayerConditionScale.UnitsPerPercent
            : 0d;

    private static double Percent(
        double value,
        double maximum) =>
        maximum > 0d
            ? Math.Clamp(
                value / maximum * 100d,
                0d,
                100d)
            : 0d;

    private static double NormalizeHour(
        double hour)
    {
        if (!double.IsFinite(hour))
            return 12d;

        var value = hour % 24d;
        return value < 0d
            ? value + 24d
            : value;
    }

    private static bool SkipNegativeRoll(
        ref PlayerConditionState state,
        double chancePercent)
    {
        var chance =
            Math.Clamp(
                chancePercent,
                0d,
                100d);
        if (chance <= 0d)
            return false;

        var sequence =
            unchecked(
                state.RandomSequence + 1);

        state = state with
        {
            RandomSequence =
                sequence
        };

        var raw =
            unchecked(
                (ulong)(
                    sequence *
                    1103515245L +
                    12345L));

        var sample =
            raw % 10000UL;

        return sample <
               chance * 100d;
    }

    private static PlayerConditionState TickEffects(
        PlayerConditionState state,
        double realSeconds)
    {
        if (realSeconds <= 0d ||
            state.Effects.Count == 0)
            return state.Normalize();

        return state with
        {
            Effects = state.Effects
                .Select(
                    effect => effect with
                    {
                        RemainingRealSeconds =
                            Math.Max(
                                0d,
                                effect.RemainingRealSeconds -
                                realSeconds)
                    })
                .Where(
                    effect =>
                        effect.RemainingRealSeconds >
                        0.000001d)
                .ToArray()
        };
    }

    private static bool HasEffect(
        PlayerConditionState state,
        string id) =>
        state.Effects.Any(
            effect =>
                effect.Id.Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase) &&
                effect.RemainingRealSeconds > 0d);

    private static PlayerConditionState AddOrReplaceEffect(
        PlayerConditionState state,
        ActivePlayerEffectState effect) =>
        state with
        {
            Effects = state.Effects
                .Where(
                    existing =>
                        !existing.Id.Equals(
                            effect.Id,
                            StringComparison.OrdinalIgnoreCase) &&
                        !(effect.Id.Equals(
                            "rested",
                            StringComparison.OrdinalIgnoreCase) &&
                          existing.Id.Equals(
                              "relaxation",
                              StringComparison.OrdinalIgnoreCase)) &&
                        !(effect.Id.Equals(
                            "relaxation",
                            StringComparison.OrdinalIgnoreCase) &&
                          existing.Id.Equals(
                              "rested",
                              StringComparison.OrdinalIgnoreCase)))
                .Append(effect)
                .ToArray()
        };

    private static PlayerConditionState EnsureEffect(
        PlayerConditionState state,
        ActivePlayerEffectState effect) =>
        HasEffect(
            state,
            effect.Id)
            ? state
            : state with
            {
                Effects = state.Effects
                    .Append(effect)
                    .ToArray()
            };

    /// <summary>
    /// Убирает эффект по id, если он есть. Симметрично <see cref="EnsureEffect"/>:
    /// «Жажда» и «Обезвоживание» живут ровно пока держится условие, а не по
    /// таймеру, поэтому их снятие — такая же часть правила, как и выдача.
    /// </summary>
    private static PlayerConditionState RemoveEffect(
        PlayerConditionState state,
        string effectId) =>
        state.Effects.Any(
            effect =>
                effect.Id.Equals(
                    effectId,
                    StringComparison.OrdinalIgnoreCase))
            ? state with
            {
                Effects = state.Effects
                    .Where(
                        effect =>
                            !effect.Id.Equals(
                                effectId,
                                StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            }
            : state;

    private static double ActiveStressSlowdownPercent(
        PlayerConditionState state) =>
        state.Effects
            .Where(
                effect =>
                    !effect.IsDebuff &&
                    effect.RemainingRealSeconds > 0d)
            .Select(
                effect =>
                    effect.StressAccumulationSlowdownPercent)
            .DefaultIfEmpty(0d)
            .Max();
}
