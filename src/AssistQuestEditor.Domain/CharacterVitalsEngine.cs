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

    public const double FatigueCriticalPercent = 80d;
    public const double FatigueExhaustionPercent = 90d;
    public const double FatigueBuildHours = 18d;
    public const double FatigueRestHours = 9d;
    public const double FatigueBuildKilometers = 900d;
    public const double StressCriticalPercent = 50d;

    /// <summary>
    /// Базовый множитель накопления КУМУЛЯТИВНОЙ усталости.
    /// Задано автором: накопление увеличено вдвое относительно «1 единица
    /// истощения на 1% критического перерасхода».
    /// </summary>
    public const double CumulativeFatigueAccrualBaseMultiplier = 2d;

    /// <summary>За каждые накопленные 5% кумулятивной усталости коэффициент растёт.</summary>
    public const double CumulativeFatigueAccrualStepPercent = 5d;

    /// <summary>Прирост коэффициента за каждые накопленные 5% (задано автором).</summary>
    public const double CumulativeFatigueAccrualStepBonus = 0.25d;

    public const double HydrationConsumptionHours = 2d;
    public const double EnergyConsumptionHours = 4d;
    public const double SleepConsumptionMultiplier = 1d / 3d;
    public const double HygieneDecayHours = 72d;

    public const double DefaultResiliencePercent = 60d;
    public const double DefaultMetabolismPercent = 60d;
    public const double MetabolismRecoveryPercentPerHour = 1d;
    public const double MetabolismExhaustionPenaltyPerQuarterHour = 2d;

    public const double HotelPrice = 5000d;
    public const int HotelSleepHours = 7;

    public const double RelaxationExperienceMultiplier = 1.15d;
    public const double RelaxationStressSlowdownPercent = 15d;
    public const double RelaxationDurationRealSeconds = 15d * 60d;
    public const double RestedExperienceMultiplier = 1.25d;
    public const double RestedStressSlowdownPercent = 25d;
    public const double RestedDurationRealSeconds = 25d * 60d;
    public const double BurnoutFatigueBuildMultiplier = 1.10d;
    public const double BurnoutDurationRealSeconds = 15d * 60d;
    public const double PowerSurgeDurationRealSeconds = 15d * 60d;

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

        var energyPerHour =
            -(100d / EnergyConsumptionHours) *
            metabolismFactor *
            sleepFactor;
        var hydrationPerHour =
            -(100d / HydrationConsumptionHours) *
            metabolismFactor *
            sleepFactor;

        var fatiguePerHour =
            moving && !sleeping
                ? 100d / FatigueBuildHours *
                  fatigueFactor *
                  (HasEffect(conditions, "burnout")
                      ? BurnoutFatigueBuildMultiplier
                      : 1d)
                : vitals.Fatigue > 0d
                    ? -100d / FatigueRestHours *
                      Math.Clamp(
                          1d - stressPercent / 100d,
                          0d,
                          1d)
                    : 0d;

        var stressPerHour =
            conditions.CumulativeFatigue > 0d
                ? PlayerConditionScale.ToPercent(
                      conditions.CumulativeFatigue) *
                  0.5d *
                  Math.Clamp(
                      1d - resiliencePercent / 400d,
                      0d,
                      1d) *
                  StressMultiplier(conditions)
                : 0d;

        return new CharacterVitalsRates(
            PlayerConditionScale.RateFromPercent(HealthRegenPerHour(vitals, conditions) / 60d),
            PlayerConditionScale.RateFromPercent(energyPerHour / 60d),
            PlayerConditionScale.RateFromPercent(hydrationPerHour / 60d),
            PlayerConditionScale.RateFromPercent(fatiguePerHour / 60d),
            PlayerConditionScale.RateFromPercent(stressPerHour / 60d),
            PlayerConditionScale.RateFromPercent(ResilienceDirection(vitals, conditions) / 60d),
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
                        100d / FatigueBuildHours *
                        fractionOfHour *
                        fatigueMultiplier *
                        burnoutMultiplier +
                        100d / FatigueBuildKilometers *
                        stepMeters / 1000d *
                        fatigueMultiplier *
                        burnoutMultiplier;

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
                            100d / FatigueRestHours *
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
                        100d / EnergyConsumptionHours *
                        fractionOfHour *
                        metabolismFactor *
                        sleepFactor);
                var hydrationDemand =
                    UnitsFromPercentExact(
                        100d / HydrationConsumptionHours *
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
                        1d,
                        resiliencePercent);

                    AddResourceExhaustion(
                        ref state,
                        "hydration",
                        hydrationBefore,
                        vitals.Hydration,
                        hydrationDemand,
                        2d,
                        resiliencePercent);
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
                    resiliencePercent);
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
                                5d)),
                        Energy = Math.Max(
                            0d,
                            vitals.Energy -
                            UnitsFromPercentExact(
                                5d)),
                        Hydration = Math.Max(
                            0d,
                            vitals.Hydration -
                            UnitsFromPercentExact(
                                5d))
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
                        if (!HasEffect(state, "bull") &&
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
                    state,
                    stepSeconds,
                    sleeping);

                ApplyHygiene(
                    ref vitals,
                    ref state,
                    stepSeconds);

                UpdateResilienceAndMetabolism(
                    ref vitals,
                    state,
                    stepSeconds);

                SyncThresholdEffects(
                    ref vitals,
                    ref state,
                    events);
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
                   vitals.MaxHealth) > 5d &&
               Percent(
                   vitals.Energy,
                   vitals.MaxEnergy) > 5d &&
               Percent(
                   vitals.Hydration,
                   vitals.MaxHydration) > 5d;
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
                    ? 0d
                    : Math.Max(
                        UnitsFromPercentExact(
                            10d),
                        conditions.CumulativeFatigue -
                        UnitsFromPercentExact(
                            6d)),
            CriticalFatigueGameSeconds = 0d
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
                        5d * SecondsPerGameHour
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
            ItemUseCounts = counts
        };

        var caffeineMg = CharacterConsumableCatalog.GetCaffeineMg(id);
        if (caffeineMg > 0d)
            ApplyCaffeine(
                ref vitals,
                ref conditions,
                caffeineMg,
                NormalizeHour(gameHourOfDay));

        switch (id.ToLowerInvariant())
        {
            case "water.bottle":
                AddResource(
                    ref vitals,
                    "hydration",
                    25d);
                break;

            case "food.milk":
                AddResource(
                    ref vitals,
                    "energy",
                    8d);
                AddResource(
                    ref vitals,
                    "hydration",
                    10d);
                ReduceStress(
                    ref conditions,
                    2d);
                break;

            case "food.meal":
                AddResource(
                    ref vitals,
                    "energy",
                    15d);
                AddResource(
                    ref vitals,
                    "hydration",
                    5d);
                ReduceFatigue(
                    ref vitals,
                    2d);
                break;

            case "drink.lemon_tea":
                AddResource(
                    ref vitals,
                    "hydration",
                    12d);
                ReduceStress(
                    ref conditions,
                    3d);
                ChangeMetabolism(
                    ref vitals,
                    2d);
                ChangeResilience(
                    ref vitals,
                    1d);
                break;

            case "tea.herbal":
                ReduceStress(
                    ref conditions,
                    5d);
                ChangeResilience(
                    ref vitals,
                    2d);
                break;

            case "drink.coffee":
                ReduceFatigue(ref vitals, 3d);
                ReduceStress(ref conditions, 1d);
                break;

            case "drink.energy":
                AddResource(ref vitals, "energy", 15d);
                ChangeMetabolism(ref vitals, 2d);
                break;

            case "drink.espresso":
                ReduceFatigue(ref vitals, 2d);
                break;

            case "drink.black_tea":
                ReduceStress(ref conditions, 1d);
                break;

            case "drink.green_tea":
                ReduceStress(ref conditions, 2d);
                ChangeResilience(ref vitals, 1d);
                break;

            case "drink.cola":
                AddResource(ref vitals, "hydration", 5d);
                break;

            case "drink.decaf_coffee":
                ReduceStress(ref conditions, 1d);
                break;

            case "drink.ginger_lemon_tea":
                AddResource(ref vitals, "hydration", 10d);
                ReduceStress(ref conditions, 3d);
                ChangeMetabolism(ref vitals, 2d);
                break;

            case "drink.kvass":
                AddResource(ref vitals, "hydration", 10d);
                AddResource(ref vitals, "energy", 4d);
                break;

            case "drink.sports":
                AddResource(ref vitals, "hydration", 25d);
                AddResource(ref vitals, "energy", 5d);
                ChangeResilience(ref vitals, 1d);
                break;

            case "electrolyte.sachet":
            case "supplement.electrolyte":
                AddResource(ref vitals, "hydration", 20d);
                ChangeResilience(ref vitals, 2d);
                break;

            case "food.kefir":
                AddResource(ref vitals, "energy", 5d);
                AddResource(ref vitals, "hydration", 8d);
                ReduceStress(ref conditions, 2d);
                break;

            case "food.yogurt":
                AddResource(ref vitals, "energy", 6d);
                AddResource(ref vitals, "hydration", 6d);
                ReduceStress(ref conditions, 2d);
                break;

            case "food.cheese":
                AddResource(ref vitals, "energy", 10d);
                ChangeResilience(ref vitals, 1d);
                break;

            case "food.nuts":
                AddResource(ref vitals, "energy", 13d);
                ChangeResilience(ref vitals, 2d);
                break;

            case "food.oatmeal":
                AddResource(ref vitals, "energy", 12d);
                ReduceStress(ref conditions, 1d);
                ChangeMetabolism(ref vitals, 1d);
                break;

            case "food.buckwheat":
                AddResource(ref vitals, "energy", 14d);
                ChangeResilience(ref vitals, 1d);
                break;

            case "food.vegetable_stew":
                AddResource(ref vitals, "energy", 9d);
                AddResource(ref vitals, "hydration", 7d);
                ChangeResilience(ref vitals, 1d);
                break;

            case "food.soup":
                AddResource(ref vitals, "energy", 8d);
                AddResource(ref vitals, "hydration", 12d);
                ReduceFatigue(ref vitals, 1d);
                break;

            case "food.egg_sandwich":
                AddResource(ref vitals, "energy", 14d);
                AddResource(ref vitals, "hydration", 3d);
                break;

            case "food.banana":
                AddResource(ref vitals, "energy", 10d);
                AddResource(ref vitals, "hydration", 3d);
                ChangeMetabolism(ref vitals, 1d);
                break;

            case "food.apple":
                AddResource(ref vitals, "energy", 7d);
                AddResource(ref vitals, "hydration", 4d);
                ReduceStress(ref conditions, 1d);
                break;

            case "food.orange":
                AddResource(ref vitals, "energy", 6d);
                AddResource(ref vitals, "hydration", 5d);
                ChangeResilience(ref vitals, 1d);
                break;

            case "food.honey":
                AddResource(ref vitals, "energy", 9d);
                ReduceFatigue(ref vitals, 1d);
                break;

            case "food.dark_chocolate":
                AddResource(ref vitals, "energy", 5d);
                ReduceStress(ref conditions, 2d);
                break;

            case "supplement.multivitamin":
                ChangeResilience(ref vitals, 5d);
                ChangeMetabolism(ref vitals, 2d);
                if (nextCount >= 5)
                {
                    conditions = AddOrReplaceEffect(
                        conditions,
                        new ActivePlayerEffectState(
                            "bull",
                            "Бык",
                            2d * 24d * 3600d,
                            false));
                }
                break;

            case "vitamin.c_effervescent":
                ChangeResilience(ref vitals, 6d);
                ChangeMetabolism(ref vitals, 2d);
                ReduceStress(ref conditions, 1d);
                break;

            case "supplement.omega3":
                ReduceStress(ref conditions, 2d);
                ChangeResilience(ref vitals, 2d);
                break;

            case "vitamin.d3":
                ChangeResilience(ref vitals, 4d);
                ChangeMetabolism(ref vitals, 1d);
                break;

            case "mineral.magnesium":
                ReduceStress(ref conditions, 5d);
                ChangeResilience(ref vitals, 2d);
                ChangeMetabolism(ref vitals, 1d);
                if (nextCount >= 10)
                    AddStress(ref conditions, 1d);
                break;

            case "mineral.zinc":
                ChangeResilience(ref vitals, 3d);
                if (nextCount >= 8)
                    AddStress(ref conditions, 1d);
                break;

            case "adaptogen.ashwagandha":
                ReduceStress(ref conditions, 7d);
                ChangeResilience(ref vitals, 3d);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "drowsiness",
                        "Сонливость",
                        2d * 3600d,
                        true));
                break;

            case "medicine.valerian":
                ReduceStress(ref conditions, 8d);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "drowsiness",
                        "Сонливость",
                        2d * 3600d,
                        true));
                break;

            case "medicine.sorbent":
                AddResource(ref vitals, "health", 3d);
                ReduceStress(ref conditions, 2d);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "sorbent",
                        "Сорбент",
                        4d * 3600d,
                        false));
                break;

            case "medicine.recovery_salts":
                AddResource(ref vitals, "hydration", 30d);
                ChangeResilience(ref vitals, 2d);
                break;

            case "smoke.cigarette":
                ReduceStress(ref conditions, 2d);
                AddOvercharge(ref conditions, "energy", 3d);
                AddStress(ref conditions, 1d);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "nicotine_rebound",
                        "Никотиновый откат",
                        60d * 60d,
                        true));
                break;

            case "drink.beer":
                ReduceStress(ref conditions, 3d);
                AddResource(ref vitals, "hydration", -10d);
                AddResource(ref vitals, "energy", 3d);
                ReduceFatigue(ref vitals, -3d);
                conditions = AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "alcohol_aftereffect",
                        "Последействие алкоголя",
                        3d * 3600d,
                        true));
                break;

            case "vitamin.c":
                ChangeResilience(
                    ref vitals,
                    8d);
                ChangeMetabolism(
                    ref vitals,
                    2d);
                ReduceStress(
                    ref conditions,
                    1d);

                if (nextCount >= 5)
                {
                    conditions =
                        AddOrReplaceEffect(
                            conditions,
                            new ActivePlayerEffectState(
                                "bull",
                                "Бык",
                                2d * 24d * 3600d,
                                false));
                }
                break;

            case "adaptogen.tincture":
                ReduceStress(
                    ref conditions,
                    8d);
                ChangeResilience(
                    ref vitals,
                    4d);
                ChangeMetabolism(
                    ref vitals,
                    3d);
                break;

            case "painkiller":
                AddResource(
                    ref vitals,
                    "health",
                    5d);
                ReduceStress(
                    ref conditions,
                    3d);
                conditions =
                    AddOrReplaceEffect(
                        conditions,
                        new ActivePlayerEffectState(
                            "analgesia",
                            "Обезболивание",
                            2d * 3600d,
                            false));

                if (nextCount >= 4)
                {
                    AddStress(
                        ref conditions,
                        1d);
                    conditions =
                        AddOrReplaceEffect(
                            conditions,
                            new ActivePlayerEffectState(
                                "analgesic_overuse",
                                "Злоупотребление обезболивающими",
                                6d * 3600d,
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
                            35d))
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
                                2d * SecondsPerGameHour)
                    };
                }
                break;

            case "meat":
                AddResource(
                    ref vitals,
                    "energy",
                    12d);
                break;

            case "gosha.homemade_sausage":
                AddResource(
                    ref vitals,
                    "energy",
                    10d);
                ReduceStress(
                    ref conditions,
                    1d);
                break;

            default:
                return new CharacterVitalsUpdate(
                    vitals,
                    conditions,
                    new[]
                    {
                        new PlayerConditionEvent(
                            "UnknownItem")
                    });
        }

        if (id.Equals(
                "food.milk",
                StringComparison.OrdinalIgnoreCase) &&
            nextCount >= 3)
        {
            conditions =
                AddOrReplaceEffect(
                    conditions,
                    new ActivePlayerEffectState(
                        "strong_bones",
                        "Крепкие кости",
                        3d * 24d * 3600d,
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
        double resiliencePercent)
    {
        var fatiguePercent =
            PlayerConditionScale.ToPercent(
                state.CumulativeFatigue);
        var baseGain =
            fatiguePercent *
            0.5d *
            gameSeconds /
            SecondsPerGameHour;
        if (baseGain <= 0d)
            return;

        var slowdown =
            ActiveStressSlowdownPercent(state);
        var resilienceFactor =
            Math.Clamp(
                1d - resiliencePercent / 400d,
                0d,
                1d);
        var gainPercent =
            baseGain *
            Math.Clamp(
                1d - slowdown / 100d,
                0d,
                1d) *
            resilienceFactor *
            StressMultiplier(state);

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
                ? 1d
                : 0.25d;
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
        PlayerConditionState state,
        double gameSeconds,
        bool sleeping)
    {
        if (gameSeconds <= 0d)
            return;

        var energyPercent =
            Percent(
                vitals.Energy,
                vitals.MaxEnergy);
        var hydrationPercent =
            Percent(
                vitals.Hydration,
                vitals.MaxHydration);

        var regenQuarter =
            energyPercent > 70d &&
            hydrationPercent > 70d
                ? 1.5d
                : energyPercent > 35d &&
                  hydrationPercent > 35d
                    ? 0.5d
                    : 0d;

        if (regenQuarter > 0d)
        {
            var quarters =
                gameSeconds /
                SecondsPerGameQuarterHour;
            var regen =
                regenQuarter *
                quarters;

            if (TotalStressPercent(state) > 0d)
                regen *= 0.75d;
            if (state.CumulativeStress > 0d)
                regen *= 0.5d;

            var metabolism = PlayerConditionScale.ToPercent(vitals.Metabolism);
            if (metabolism < 45d)
                regen *= 0.75d;
            else if (metabolism >= 75d)
                regen *= 1.5d;

            if (HasEffect(state, "bull"))
                regen *= 1.5d;

            vitals = vitals with
            {
                Health =
                    Math.Min(
                        EffectiveHealthMaximum(
                            vitals,
                            state),
                        vitals.Health +
                        UnitsFromPercentExact(
                            regen))
            };
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
            6d *
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

    private static double HealthRegenPerHour(
        PlayerVitalsState vitals,
        PlayerConditionState state)
    {
        var energyPercent =
            Percent(
                vitals.Energy,
                vitals.MaxEnergy);
        var hydrationPercent =
            Percent(
                vitals.Hydration,
                vitals.MaxHydration);

        var quarter =
            energyPercent > 70d &&
            hydrationPercent > 70d
                ? 1.5d
                : energyPercent > 35d &&
                  hydrationPercent > 35d
                    ? 0.5d
                    : 0d;

        var value = quarter * 4d;
        if (TotalStressPercent(state) > 0d)
            value *= 0.75d;
        if (state.CumulativeStress > 0d)
            value *= 0.5d;

        var metabolism = PlayerConditionScale.ToPercent(vitals.Metabolism);
        if (metabolism < 45d)
            value *= 0.75d;
        else if (metabolism >= 75d)
            value *= 1.5d;

        if (HasEffect(state, "bull"))
            value *= 1.5d;

        return value;
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
                metabolism < 45d ? 0.75d :
                metabolism >= 75d ? 1.5d : 1d;

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
        double gameSeconds)
    {
        var quarters =
            gameSeconds /
            SecondsPerGameQuarterHour;

        var resilience =
            PlayerConditionScale.ToPercent(
                vitals.Resilience);
        var targetResilience =
            HasEffect(state, "bum")
                ? 50d
                : DefaultResiliencePercent;
        var resilienceRatePerQuarter =
            HasEffect(state, "bum")
                ? 1d
                : 0.5d;

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

        var exhaustionScales =
            (state.CumulativeStress > 0d ? 1 : 0) +
            (state.CumulativeFatigue > 0d ? 1 : 0) +
            (state.CumulativeHydration > 0d ? 1 : 0) +
            (state.CumulativeEnergy > 0d ? 1 : 0);

        metabolismDelta -=
            exhaustionScales *
            MetabolismExhaustionPenaltyPerQuarterHour *
            quarters;

        vitals = vitals with
        {
            Resilience =
                UnitsFromPercentExact(
                    Math.Clamp(
                        resilience + resilienceDelta,
                        0d,
                        HasEffect(state, "bum")
                            ? 50d
                            : 100d)),
            Metabolism =
                UnitsFromPercentExact(
                    Math.Clamp(
                        metabolism + metabolismDelta,
                        0d,
                        100d))
        };
    }

    private static void SyncThresholdEffects(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        List<PlayerConditionEvent> events)
    {
        var hygiene =
            Percent(
                vitals.Hygiene,
                PlayerConditionScale.Maximum);

        if (hygiene < 50d)
        {
            state = EnsureEffect(
                state,
                new ActivePlayerEffectState(
                    "unkempt",
                    "Неопрятный",
                    1e12,
                    true));
        }

        if (hygiene < 20d)
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
                        5d *
                        SecondsPerGameHour,
                    Stress =
                        UnitsFromPercentExact(
                            90d)
                };

                vitals = vitals with
                {
                    Health =
                        Math.Max(
                            0d,
                            vitals.Health -
                            UnitsFromPercentExact(
                                25d))
                };

                events.Add(
                    new PlayerConditionEvent(
                        "SkinProblemsApplied"));
            }
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
                    state)) >= 99.9d;
        var energy =
            Percent(
                vitals.Energy,
                EffectiveEnergyMaximum(
                    vitals,
                    state)) >= 99.9d;
        var hydration =
            Percent(
                vitals.Hydration,
                EffectiveHydrationMaximum(
                    vitals,
                    state)) >= 99.9d;
        var rested =
            Percent(
                vitals.Fatigue,
                vitals.MaxFatigue) <= 10d;
        var calm =
            TotalStressPercent(state) <= 20d;
        var hygiene =
            Percent(
                vitals.Hygiene,
                PlayerConditionScale.Maximum) >= 50d;
        var resilience =
            EffectiveResiliencePercent(
                vitals,
                state) > 75d;
        var metabolism =
            PlayerConditionScale.ToPercent(
                vitals.Metabolism) > 75d;

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
            ? 2.5d
            : HasEffect(state, "unkempt")
                ? 1.5d
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
            metabolism < 45d
                ? 0.90d
                : metabolism >= 75d
                    ? 1.25d
                    : 1d;

        return HasEffect(state, "bull")
            ? factor * 0.50d
            : factor;
    }

    private static double TimeOfDayFatigueMultiplier(
        double hour)
    {
        var normalized =
            NormalizeHour(hour);

        return normalized >= 6d &&
               normalized < 18d
            ? 1d
            : normalized >= 18d &&
              normalized < 22d
                ? 1.5d
                : 2.5d;
    }

    private static double ResilienceDirection(
        PlayerVitalsState vitals,
        PlayerConditionState state)
    {
        var current =
            PlayerConditionScale.ToPercent(
                vitals.Resilience);
        var target =
            HasEffect(state, "bum")
                ? 50d
                : DefaultResiliencePercent;
        var rate =
            HasEffect(state, "bum")
                ? 1d
                : 0.5d;

        return Math.Clamp(
            target - current,
            -rate,
            rate);
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

        var exhaustionScales =
            (state.CumulativeStress > 0d ? 1 : 0) +
            (state.CumulativeFatigue > 0d ? 1 : 0) +
            (state.CumulativeHydration > 0d ? 1 : 0) +
            (state.CumulativeEnergy > 0d ? 1 : 0);

        return value -
               exhaustionScales *
               MetabolismExhaustionPenaltyPerQuarterHour;
    }

    private static double EffectiveResiliencePercent(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        HasEffect(state, "bum")
            ? Math.Min(
                50d,
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
            0.75d);
        var effectiveDose =
            caffeineMg * (1d - adaptation * 0.35d);

        AddOvercharge(
            ref state,
            "energy",
            Math.Max(1d, effectiveDose / 12d));
        ReduceFatigue(
            ref vitals,
            Math.Min(
                5d,
                1.5d + effectiveDose / 90d));

        state = state with
        {
            CaffeineLoadMg =
                state.CaffeineLoadMg + caffeineMg,
            CaffeineDailyMg = dailyAfter,
            CaffeineDependence =
                Math.Clamp(
                    state.CaffeineDependence +
                    caffeineMg / 100d * 0.8d,
                    0d,
                    100d)
        };

        if (dailyBefore <= 200d &&
            dailyAfter > 200d)
            AddStress(ref state, 1d);

        if (dailyBefore <= 400d &&
            dailyAfter > 400d)
        {
            AddStress(ref state, 2d);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_overuse",
                    "Кофеин — перенапряжение",
                    2d * 3600d,
                    true));
        }

        if (dailyBefore <= 600d &&
            dailyAfter > 600d)
        {
            AddStress(ref state, 4d);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_excess",
                    "Кофеин — избыток",
                    2d * 3600d,
                    true));
        }

        if (caffeineMg >= 200d)
        {
            AddStress(ref state, 1d);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_jitter",
                    "Кофеиновая возбудимость",
                    90d * 60d,
                    true));
        }

        if (gameHour >= 20d || gameHour < 6d)
        {
            AddStress(ref state, 0.5d);
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "late_caffeine",
                    "Поздний кофеин",
                    3d * 3600d,
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
                hours / 5d);

        var daySeconds =
            state.CaffeineDaySeconds + gameSeconds;
        var dailyMg = state.CaffeineDailyMg;

        while (daySeconds >= 24d * 3600d)
        {
            daySeconds -= 24d * 3600d;
            dailyMg = 0d;
        }

        var dependence =
            Math.Max(
                0d,
                state.CaffeineDependence -
                hours * 0.15d);

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

        if (dependence >= 20d &&
            loadBefore >= 20d &&
            loadAfter < 8d &&
            !HasEffect(
                state,
                "caffeine_withdrawal"))
        {
            vitals = vitals with
            {
                Fatigue =
                    vitals.Fatigue +
                    UnitsFromPercentExact(
                        sleeping ? 1d : 3d)
            };

            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "caffeine_withdrawal",
                    "Кофеиновый откат",
                    2d * 3600d,
                    true));

            AddStress(
                ref state,
                sleeping ? 1d : 2d);

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
                ? 1.5d
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

    private static void AddResource(
        ref PlayerVitalsState vitals,
        string key,
        double percent)
    {
        var units =
            UnitsFromPercentExact(
                percent);

        if (key.Equals(
                "health",
                StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Health =
                    vitals.Health + units
            };
        }
        else if (key.Equals(
                     "energy",
                     StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Energy =
                    vitals.Energy + units
            };
        }
        else if (key.Equals(
                     "hydration",
                     StringComparison.OrdinalIgnoreCase))
        {
            vitals = vitals with
            {
                Hydration =
                    vitals.Hydration + units
            };
        }
    }

    private static void ReduceFatigue(
        ref PlayerVitalsState vitals,
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
