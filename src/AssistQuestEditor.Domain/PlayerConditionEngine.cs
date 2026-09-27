namespace AssistQuestEditor.Domain;

/// <summary>Событие долгосрочного состояния персонажа.</summary>
public sealed record PlayerConditionEvent(string Kind);

public sealed record PlayerConditionUpdate(
    PlayerVitalsState Vitals,
    PlayerConditionState Conditions,
    IReadOnlyList<PlayerConditionEvent> Events);

/// <summary>
/// Чистые правила мягкой/кумулятивной усталости и стресса и временных эффектов.
/// Проценты состояний — процентные пункты 0..100.
/// </summary>
public static class PlayerConditionEngine
{
    public const double FatigueCriticalPercent = 80d;
    public const double StressCriticalPercent = 50d;
    public const double FatigueBuildHours = 18d;
    public const double FatigueRestHours = 9d;
    /// <summary>
    /// Дистанция, за которую усталость набирается на 100%.
    ///
    /// Второе слагаемое к <see cref="FatigueBuildHours"/>, а не замена: работа за
    /// рулём утомляет и временем, и дорогой, причём по-разному при разной
    /// скорости. Дальний рейс «600 км за 6 часов» выматывает сильнее, чем те же
    /// 6 часов на 30 км/ч, и наоборот. Прежняя версия учитывала ТОЛЬКО время,
    /// поэтому усталость практически не двигалась: игровое время идёт с реальной
    /// скоростью (1:1), а 100% за 18 часов — это 5.6% за час, чего в пределах
    /// сессии не видно (значение округляется до целого).
    /// </summary>
    public const double FatigueBuildKilometers = 900d;
    public const double StressPerCumulativeFatiguePerHour = 0.5d;
    public const double CumulativeFatiguePerCriticalHour = 1d;
    public const double CumulativeStressPerCriticalHour = 1d;

    public const double BurnoutFatigueBuildMultiplier = 1.10d;
    public const double BurnoutPenaltyPercent = 5d;
    public const double BurnoutDurationRealSeconds = 15d * 60d;

    public const double RelaxationExperienceMultiplier = 1.15d;
    public const double RelaxationStressSlowdownPercent = 15d;
    public const double RelaxationDurationRealSeconds = 15d * 60d;
    public const double RestedExperienceMultiplier = 1.25d;
    public const double RestedStressSlowdownPercent = 25d;
    public const double RestedDurationRealSeconds = 25d * 60d;

    private const double SecondsPerGameHour = 3600d;

    public static PlayerConditionUpdate Advance(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        double gameSeconds,
        double realSeconds,
        bool playerMoving,
        bool sleeping)
        => Advance(
            vitals,
            conditions,
            gameSeconds,
            realSeconds,
            playerMoving,
            sleeping,
            traveledMeters: 0d);

    /// <summary>
    /// То же начисление, но с учётом ПРОЙДЕННОЙ ЗА ЭТОТ ЖЕ ИНТЕРВАЛ дистанции.
    ///
    /// Дорога утомляет наравне со временем: усталость растёт и по часам
    /// (<see cref="FatigueBuildHours"/>), и по километрам
    /// (<see cref="FatigueBuildKilometers"/>). Дистанция распределяется по часовым
    /// шагам пропорционально времени шага, поэтому результат не зависит от того,
    /// пришёл интервал одним вызовом или был разбит на несколько.
    /// </summary>
    public static PlayerConditionUpdate Advance(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        double gameSeconds,
        double realSeconds,
        bool playerMoving,
        bool sleeping,
        double traveledMeters)
    {
        vitals = NormalizeVitals(vitals);
        conditions = (conditions ?? PlayerConditionState.Empty).Normalize();

        var game = Math.Max(0d, double.IsFinite(gameSeconds) ? gameSeconds : 0d);
        var real = Math.Max(0d, double.IsFinite(realSeconds) ? realSeconds : 0d);
        var state = TickEffects(conditions, real);

        if (game <= 0d)
            return new PlayerConditionUpdate(vitals, state, Array.Empty<PlayerConditionEvent>());

        var events = new List<PlayerConditionEvent>();
        var remainingHours = game / SecondsPerGameHour;
        var totalHours = remainingHours;

        // Дистанция раскладывается по шагам пропорционально их длительности:
        // сумма слагаемых не зависит от дробления интервала на вызовы.
        var remainingMeters = Math.Max(
            0d,
            double.IsFinite(traveledMeters) ? traveledMeters : 0d);

        while (remainingHours > 0.0000001d)
        {
            var hours = Math.Min(1d, remainingHours);
            remainingHours -= hours;
            var totalStressBefore = TotalStress(state);

            var stepMeters = totalHours > 0d
                ? remainingMeters * hours / totalHours
                : 0d;

            if (playerMoving && !sleeping)
            {
                var multiplier = HasEffect(state, "burnout")
                    ? BurnoutFatigueBuildMultiplier
                    : 1d;

                var fatigueGain =
                    100d / FatigueBuildHours * hours +
                    100d / FatigueBuildKilometers * stepMeters / 1000d;

                vitals = vitals with
                {
                    Fatigue = vitals.Fatigue + fatigueGain * multiplier
                };
            }
            else if (!playerMoving && !sleeping)
            {
                var recoveryFactor = Math.Clamp(
                    1d - TotalStress(state) / 100d,
                    0d,
                    1d);

                vitals = vitals with
                {
                    Fatigue = vitals.Fatigue -
                              100d / FatigueRestHours * hours * recoveryFactor
                };
            }

            vitals = ClampSoft(vitals, state);

            var totalFatigue = TotalFatigue(vitals, state);
            if (totalFatigue > FatigueCriticalPercent)
            {
                var counter = state.CriticalFatigueGameSeconds + hours * SecondsPerGameHour;
                var wholeHours = Math.Floor(counter / SecondsPerGameHour);

                state = wholeHours > 0d
                    ? state with
                    {
                        CumulativeFatigue = Math.Clamp(
                            state.CumulativeFatigue + wholeHours * CumulativeFatiguePerCriticalHour,
                            0d,
                            100d),
                        CriticalFatigueGameSeconds = counter - wholeHours * SecondsPerGameHour
                    }
                    : state with { CriticalFatigueGameSeconds = counter };

                vitals = ClampSoft(vitals, state);
            }
            else
            {
                state = state with { CriticalFatigueGameSeconds = 0d };
            }

            var stressSlowdown = ActiveStressSlowdownPercent(state);
            var stressGain = state.CumulativeFatigue *
                             StressPerCumulativeFatiguePerHour *
                             hours *
                             Math.Clamp(1d - stressSlowdown / 100d, 0d, 1d);

            state = state with { Stress = state.Stress + stressGain };

            if (TotalStress(state) > StressCriticalPercent)
            {
                var counter = state.CriticalStressGameSeconds + hours * SecondsPerGameHour;
                var wholeHours = Math.Floor(counter / SecondsPerGameHour);
                state = wholeHours > 0d
                    ? state with
                    {
                        CumulativeStress = Math.Clamp(
                            state.CumulativeStress + wholeHours * CumulativeStressPerCriticalHour,
                            0d,
                            100d),
                        CriticalStressGameSeconds = counter - wholeHours * SecondsPerGameHour
                    }
                    : state with { CriticalStressGameSeconds = counter };
            }
            else
            {
                state = state with { CriticalStressGameSeconds = 0d };
            }

            state = state with
            {
                Stress = Math.Clamp(
                    state.Stress,
                    0d,
                    Math.Max(0d, 100d - state.CumulativeStress))
            };

            var totalStressAfter = TotalStress(state);
            if (totalStressBefore <= StressCriticalPercent &&
                totalStressAfter > StressCriticalPercent &&
                !HasEffect(state, "burnout"))
            {
                state = AddOrReplaceEffect(
                    state,
                    new ActivePlayerEffectState(
                        "burnout",
                        "Выгорание",
                        BurnoutDurationRealSeconds,
                        true,
                        1d,
                        0d));

                vitals = ApplyBurnoutPenalty(vitals, state);
                events.Add(new PlayerConditionEvent("BurnoutApplied"));
            }
        }

        return new PlayerConditionUpdate(
            NormalizeVitals(vitals),
            state.Normalize(),
            events);
    }

    public static PlayerConditionUpdate Sleep(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        int hours,
        bool fullSleep)
    {
        var safeHours = Math.Clamp(hours, 1, 24);
        var currentVitals = NormalizeVitals(vitals);
        var state = (conditions ?? PlayerConditionState.Empty).Normalize();
        var events = new List<PlayerConditionEvent>();

        for (var hour = 0; hour < safeHours; hour++)
        {
            var update = Advance(
                currentVitals,
                state,
                SecondsPerGameHour,
                0d,
                playerMoving: false,
                sleeping: true);

            currentVitals = update.Vitals;
            state = update.Conditions;
            events.AddRange(update.Events);

            if (hour == 0)
            {
                state = AddOrReplaceEffect(
                    state,
                    new ActivePlayerEffectState(
                        "relaxation",
                        "Расслабление",
                        RelaxationDurationRealSeconds,
                        false,
                        RelaxationExperienceMultiplier,
                        RelaxationStressSlowdownPercent));
            }
        }

        state = state with
        {
            CumulativeFatigue = fullSleep
                ? 0d
                : state.CumulativeFatigue > 10d
                    ? Math.Max(10d, state.CumulativeFatigue - 6d)
                    : state.CumulativeFatigue,
            CriticalFatigueGameSeconds = 0d
        };

        currentVitals = ClampSoft(currentVitals with { Fatigue = 0d }, state);

        if (fullSleep)
        {
            state = AddOrReplaceEffect(
                state,
                new ActivePlayerEffectState(
                    "rested",
                    "Отдохнувший",
                    RestedDurationRealSeconds,
                    false,
                    RestedExperienceMultiplier,
                    RestedStressSlowdownPercent));
        }

        return new PlayerConditionUpdate(
            NormalizeVitals(currentVitals),
            state.Normalize(),
            events);
    }

    public static double GetExperienceMultiplier(PlayerConditionState conditions) =>
        (conditions ?? PlayerConditionState.Empty).Effects
            .Where(effect => !effect.IsDebuff && effect.RemainingRealSeconds > 0d)
            .Select(effect => effect.ExperienceMultiplier)
            .DefaultIfEmpty(1d)
            .Max();

    private static PlayerVitalsState NormalizeVitals(PlayerVitalsState value) =>
        value with
        {
            MaxHealth = Math.Max(0d, value.MaxHealth),
            MaxEnergy = Math.Max(0d, value.MaxEnergy),
            MaxHydration = Math.Max(0d, value.MaxHydration),
            MaxFatigue = Math.Max(0d, value.MaxFatigue),
            Health = Math.Clamp(value.Health, 0d, Math.Max(0d, value.MaxHealth)),
            Energy = Math.Clamp(value.Energy, 0d, Math.Max(0d, value.MaxEnergy)),
            Hydration = Math.Clamp(value.Hydration, 0d, Math.Max(0d, value.MaxHydration)),
            Fatigue = Math.Clamp(value.Fatigue, 0d, Math.Max(0d, value.MaxFatigue))
        };

    private static PlayerConditionState TickEffects(PlayerConditionState state, double realSeconds)
    {
        if (realSeconds <= 0d || state.Effects.Count == 0)
            return state.Normalize();

        return state with
        {
            Effects = state.Effects
                .Select(effect => effect with
                {
                    RemainingRealSeconds = Math.Max(0d, effect.RemainingRealSeconds - realSeconds)
                })
                .Where(effect => effect.RemainingRealSeconds > 0.000001d)
                .ToArray()
        };
    }

    private static bool HasEffect(PlayerConditionState state, string id) =>
        state.Effects.Any(effect =>
            effect.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            effect.RemainingRealSeconds > 0d);

    private static PlayerConditionState AddOrReplaceEffect(
        PlayerConditionState state,
        ActivePlayerEffectState effect) =>
        state with
        {
            Effects = state.Effects
                .Where(existing =>
                    !existing.Id.Equals(effect.Id, StringComparison.OrdinalIgnoreCase) &&
                    !(effect.Id.Equals("rested", StringComparison.OrdinalIgnoreCase) &&
                      existing.Id.Equals("relaxation", StringComparison.OrdinalIgnoreCase)) &&
                    !(effect.Id.Equals("relaxation", StringComparison.OrdinalIgnoreCase) &&
                      existing.Id.Equals("rested", StringComparison.OrdinalIgnoreCase)))
                .Append(effect)
                .ToArray()
        };

    private static PlayerVitalsState ClampSoft(
        PlayerVitalsState vitals,
        PlayerConditionState state) =>
        vitals with
        {
            Health = Math.Clamp(vitals.Health, 0d, EffectiveConsumableMax(vitals.MaxHealth, state.CumulativeHealth)),
            Energy = Math.Clamp(vitals.Energy, 0d, EffectiveConsumableMax(vitals.MaxEnergy, state.CumulativeEnergy)),
            Hydration = Math.Clamp(vitals.Hydration, 0d, EffectiveConsumableMax(vitals.MaxHydration, state.CumulativeHydration)),
            Fatigue = Math.Clamp(vitals.Fatigue, 0d, Math.Max(0d, vitals.MaxFatigue - state.CumulativeFatigue))
        };

    private static double EffectiveConsumableMax(double maximum, double cumulative) =>
        Math.Max(0d, maximum * (1d - Math.Clamp(cumulative, 0d, 100d) / 100d));

    private static PlayerVitalsState ApplyBurnoutPenalty(
        PlayerVitalsState vitals,
        PlayerConditionState state)
    {
        static double Reduce(double value, double maximum, double cumulative)
        {
            var effectiveMaximum = EffectiveConsumableMax(maximum, cumulative);
            return Math.Max(0d, value - effectiveMaximum * BurnoutPenaltyPercent / 100d);
        }

        return vitals with
        {
            Health = Reduce(vitals.Health, vitals.MaxHealth, state.CumulativeHealth),
            Energy = Reduce(vitals.Energy, vitals.MaxEnergy, state.CumulativeEnergy),
            Hydration = Reduce(vitals.Hydration, vitals.MaxHydration, state.CumulativeHydration)
        };
    }

    private static double TotalStress(PlayerConditionState state) =>
        Math.Clamp(state.Stress + state.CumulativeStress, 0d, 100d);

    private static double TotalFatigue(PlayerVitalsState vitals, PlayerConditionState state) =>
        Math.Clamp(vitals.Fatigue + state.CumulativeFatigue, 0d, 100d);

    private static double ActiveStressSlowdownPercent(PlayerConditionState state) =>
        state.Effects
            .Where(effect => !effect.IsDebuff && effect.RemainingRealSeconds > 0d)
            .Select(effect => effect.StressAccumulationSlowdownPercent)
            .DefaultIfEmpty(0d)
            .Max();
}
