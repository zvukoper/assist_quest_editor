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
            HealthRegenPerHour(vitals, conditions) / 60d,
            energyPerHour / 60d,
            hydrationPerHour / 60d,
            fatiguePerHour / 60d,
            stressPerHour / 60d,
            ResilienceDirection(vitals, conditions) / 60d,
            MetabolismDirection(vitals, conditions) / 60d);
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
                        PlayerConditionScale.FromPercent(
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
                        PlayerConditionScale.FromPercent(
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
                    PlayerConditionScale.FromPercent(
                        100d / EnergyConsumptionHours *
                        fractionOfHour *
                        metabolismFactor *
                        sleepFactor);
                var hydrationDemand =
                    PlayerConditionScale.FromPercent(
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

                var stressBefore =
                    TotalStressPercent(state);
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
                                    PlayerConditionScale.FromPercent(
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

                if (stressBefore <= StressCriticalPercent &&
                    stressAfter > StressCriticalPercent &&
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
                            PlayerConditionScale.FromPercent(
                                5d)),
                        Energy = Math.Max(
                            0d,
                            vitals.Energy -
                            PlayerConditionScale.FromPercent(
                                5d)),
                        Hydration = Math.Max(
                            0d,
                            vitals.Hydration -
                            PlayerConditionScale.FromPercent(
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
                        var cumulativeGain =
                            PlayerConditionScale.FromPercent(
                                wholeHours);

                        if (!SkipNegativeRoll(
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
                        PlayerConditionScale.FromPercent(
                            10d),
                        conditions.CumulativeFatigue -
                        PlayerConditionScale.FromPercent(
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
        PlayerConditionState conditions)
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
                AddOvercharge(
                    ref conditions,
                    "energy",
                    10d);
                ReduceFatigue(
                    ref vitals,
                    4d);
                ReduceStress(
                    ref conditions,
                    1d);
                ChangeMetabolism(
                    ref vitals,
                    3d);

                if (nextCount >= 4)
                {
                    AddStress(
                        ref conditions,
                        2d);
                    conditions =
                        AddOrReplaceEffect(
                            conditions,
                            new ActivePlayerEffectState(
                                "caffeine_overuse",
                                "Кофеин — перенапряжение",
                                90d * 60d,
                                true));
                }
                break;

            case "drink.energy":
                AddResource(
                    ref vitals,
                    "energy",
                    20d);
                AddOvercharge(
                    ref conditions,
                    "energy",
                    10d);
                AddStress(
                    ref conditions,
                    4d);
                ChangeMetabolism(
                    ref vitals,
                    4d);

                if (nextCount >= 3)
                {
                    AddStress(
                        ref conditions,
                        2d);
                    conditions =
                        AddOrReplaceEffect(
                            conditions,
                            new ActivePlayerEffectState(
                                "energy_drink_overuse",
                                "Энергетик — злоупотребление",
                                120d * 60d,
                                true));
                }
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
                        PlayerConditionScale.FromPercent(
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
            PlayerConditionScale.FromPercent(
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

    private static void AddFatigueExhaustion(
        ref PlayerConditionState state,
        double fatigueGainUnits,
        double currentSoftFatigue,
        double resiliencePercent)
    {
        if (fatigueGainUnits <= 0d)
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
            PlayerConditionScale.FromPercent(
                critical * multiplier);

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
                        PlayerConditionScale.FromPercent(
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
                    PlayerConditionScale.FromPercent(
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
        return value;
    }

    private static void ApplyHygiene(
        ref PlayerVitalsState vitals,
        ref PlayerConditionState state,
        double gameSeconds)
    {
        var decay =
            PlayerConditionScale.FromPercent(
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
            state = state with
            {
                SkinIssuesGameSeconds =
                    Math.Max(
                        0d,
                        state.SkinIssuesGameSeconds -
                        gameSeconds)
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
                PlayerConditionScale.FromPercent(
                    Math.Clamp(
                        resilience + resilienceDelta,
                        0d,
                        HasEffect(state, "bum")
                            ? 50d
                            : 100d)),
            Metabolism =
                PlayerConditionScale.FromPercent(
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
                        PlayerConditionScale.FromPercent(
                            90d)
                };

                vitals = vitals with
                {
                    Health =
                        Math.Max(
                            0d,
                            vitals.Health -
                            PlayerConditionScale.FromPercent(
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

        if (SkipNegativeRoll(
                ref state,
                resiliencePercent / 2d))
            return;

        var remaining =
            ConsumeOvercharge(
                ref state,
                key,
                units);

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
            PlayerConditionScale.FromPercent(
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
            PlayerConditionScale.FromPercent(
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
                    PlayerConditionScale.FromPercent(
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
                    PlayerConditionScale.FromPercent(
                        percent))
        };

    private static void AddStress(
        ref PlayerConditionState state,
        double percent) =>
        state = state with
        {
            Stress =
                state.Stress +
                PlayerConditionScale.FromPercent(
                    percent)
        };

    private static void ChangeResilience(
        ref PlayerVitalsState vitals,
        double percent) =>
        vitals = vitals with
        {
            Resilience =
                vitals.Resilience +
                PlayerConditionScale.FromPercent(
                    percent)
        };

    private static void ChangeMetabolism(
        ref PlayerVitalsState vitals,
        double percent) =>
        vitals = vitals with
        {
            Metabolism =
                vitals.Metabolism +
                PlayerConditionScale.FromPercent(
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
