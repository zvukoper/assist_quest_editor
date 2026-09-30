namespace AssistQuestEditor.Domain;

/// <summary>Событие долгосрочного состояния персонажа.</summary>
public sealed record PlayerConditionEvent(string Kind);

public sealed record PlayerConditionUpdate(
    PlayerVitalsState Vitals,
    PlayerConditionState Conditions,
    IReadOnlyList<PlayerConditionEvent> Events);

/// <summary>
/// Скорость изменения шкал: сколько ЕДИНИЦ шкалы (0..10000) прибывает или
/// убывает за одну игровую минуту. UI показывает это в подсказке шкалы.
///
/// Величины считаются из ТЕХ ЖЕ констант, что и начисление, поэтому вторая
/// таблица скоростей не заводится: подсказка не может разойтись с правилами.
/// </summary>
public sealed record PlayerConditionRates(
    double HealthPerGameMinute,
    double EnergyPerGameMinute,
    double HydrationPerGameMinute,
    double FatiguePerGameMinute,
    double StressPerGameMinute)
{
    public static PlayerConditionRates Zero => new(0d, 0d, 0d, 0d, 0d);

    /// <summary>
    /// Скорость изменения шкал из текущего состояния и режима игрока.
    ///
    /// Движение утомляет, отдых восстанавливает, а кумулятивная усталость
    /// повышает стресс — те же три правила, что в <see cref="PlayerConditionEngine"/>.
    /// Внутренние единицы (0..10000) получаются домножением процентов на 100,
    /// поэтому значение за минуту остаётся заметным даже когда в процентах оно
    /// было бы нулём.
    /// </summary>
    public static PlayerConditionRates From(
        PlayerVitalsState vitals,
        PlayerConditionState conditions,
        bool playerMoving,
        bool sleeping)
    {
        var state = (conditions ?? PlayerConditionState.Empty).Normalize();
        var currentVitals = (vitals ?? PlayerVitalsState.Default).Normalize();

        if (sleeping)
        {
            // Сон снимает усталость в разы быстрее, чем отдых на месте: он
            // выводит усталость на ноль за считанные игровые часы.
            var sleepPerHour = -100d / PlayerConditionEngine.FatigueRestHours *
                PlayerConditionEngine.SleepFatigueRecoveryMultiplier;

            return new PlayerConditionRates(
                0d,
                0d,
                0d,
                PlayerConditionScale.RateFromPercent(sleepPerHour / 60d),
                0d);
        }

        if (!playerMoving)
        {
            var recoveryFactor = Math.Clamp(
                1d - PlayerConditionEngine.TotalStressPercent(state) / 100d,
                0d,
                1d);

            var recoveryPerHour = -100d / PlayerConditionEngine.FatigueRestHours *
                recoveryFactor *
                (currentVitals.Fatigue > 0d ? 1d : 0d);

            return new PlayerConditionRates(
                0d,
                0d,
                0d,
                PlayerConditionScale.RateFromPercent(recoveryPerHour / 60d),
                ComputeStressPerGameMinute(state));
        }

        var fatigueMultiplier = HasEffect(state, "burnout")
            ? PlayerConditionEngine.BurnoutFatigueBuildMultiplier
            : 1d;

        var buildPerHour = 100d / PlayerConditionEngine.FatigueBuildHours *
            fatigueMultiplier;

        return new PlayerConditionRates(
            0d,
            0d,
            0d,
            PlayerConditionScale.RateFromPercent(buildPerHour / 60d),
            ComputeStressPerGameMinute(state));
    }

    /// <summary>
    /// Стресс прибывает от КУМУЛИТИВНОЙ усталости, а не от мгновенной: правило
    /// то же, что в <c>Advance</c>. Ноль, пока кумулятивной усталости нет.
    /// </summary>
    private static double ComputeStressPerGameMinute(PlayerConditionState state)
    {
        if (state.CumulativeFatigue <= 0d)
            return 0d;

        var slowdown = state.Effects
            .Where(effect => !effect.IsDebuff && effect.RemainingRealSeconds > 0d)
            .Select(effect => effect.StressAccumulationSlowdownPercent)
            .DefaultIfEmpty(0d)
            .Max();

        // Кумулятивная усталость уже в единицах, поэтому и прирост стресса
        // получается в единицах: пересчёт процентов здесь не нужен.
        var perHour = state.CumulativeFatigue *
            PlayerConditionEngine.StressPerCumulativeFatiguePerHour *
            Math.Clamp(1d - slowdown / 100d, 0d, 1d);

        return perHour / 60d;
    }

    private static bool HasEffect(PlayerConditionState state, string id) =>
        state.Effects.Any(effect =>
            effect.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            effect.RemainingRealSeconds > 0d);
}

/// <summary>
/// Чистые правила мягкой/кумулятивной усталости и стресса и временных эффектов.
///
/// ВНУТРЕННИЕ ПОРОГИ — ПРОЦЕНТЫ (0..100): «усталость 80% критична», «100% за
/// 18 часов» — читаемые правила, и переписывать их в 0..10000 значило бы
/// потерять смысл. На ГРАНИЦЕ движка значения переводятся в единицы шкалы
/// (<see cref="PlayerConditionScale"/>): на входе проценты → единицы, на выходе
/// единицы храним, а проценты нужны только для сравнения с порогами.
/// </summary>
public static class PlayerConditionEngine
{
    // Все числа ниже ПРИВЯЗАНЫ к калибровочному файлу
    // <see cref="CharacterVitalsTuning"/>: раньше они были продублированы здесь
    // собственными литералами, и правка калибровки не меняла поведение этого
    // (второго) движка условий. Теперь источник один — менять нужно только
    // CharacterVitalsTuning.cs.
    public const double FatigueCriticalPercent =
        CharacterVitalsTuning.FatigueCriticalPercent;
    public const double StressCriticalPercent =
        CharacterVitalsTuning.StressCriticalPercent;
    public const double FatigueBuildHours =
        CharacterVitalsTuning.FatigueBuildHours;
    public const double FatigueRestHours =
        CharacterVitalsTuning.FatigueRestHours;
    /// <summary>
    /// Во сколько раз сон снимает усталость быстрее обычного отдыха на месте.
    /// Множитель нужен только подсказке о скорости изменения: сам сон считает
    /// восстановление по шагам, но игрок должен видеть, что во сне шкала уходит
    /// вниз быстрее.
    /// </summary>
    public const double SleepFatigueRecoveryMultiplier = 3d;
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
    public const double FatigueBuildKilometers =
        CharacterVitalsTuning.FatigueBuildKilometers;
    public const double StressPerCumulativeFatiguePerHour =
        CharacterVitalsTuning.StressPerCumulativeFatiguePercentPerHour;

    /// <summary>
    /// Доля ПРИРОСТА усталости, превращающаяся в истощение усталости:
    /// <see cref="CharacterVitalsTuning.FatigueExhaustionBelowFullShare"/> пока
    /// усталость не дошла до 100%, <see cref="CharacterVitalsTuning.FatigueExhaustionShare"/>
    /// на самой 100%.
    ///
    /// Почему ДОЛЯ, а не ставка за час. Автор задал механику пропорцией («при
    /// усталости 100% попытка добавить усталость добавляет истощение 1:1»), и
    /// почасовая ставка ей противоречила: она начисляла истощение даже когда
    /// усталость стояла на месте, и НЕ начисляла при разовом рывке вверх.
    /// </summary>
    public const double FatigueExhaustionShare =
        CharacterVitalsTuning.FatigueExhaustionShare;
    public const double FatigueExhaustionBelowFullShare =
        CharacterVitalsTuning.FatigueExhaustionBelowFullShare;

    /// <summary>Доля ПРИРОСТА стресса, становящаяся истощением стресса (1:2).</summary>
    public const double StressExhaustionShare =
        CharacterVitalsTuning.StressExhaustionShare;

    public const double BurnoutFatigueBuildMultiplier =
        CharacterVitalsTuning.BurnoutFatigueBuildMultiplier;
    public const double BurnoutPenaltyPercent =
        CharacterVitalsTuning.BurnoutInstantPenaltyPercent;
    public const double BurnoutDurationRealSeconds =
        CharacterVitalsTuning.BurnoutDurationRealSeconds;

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
            var totalStressBefore = TotalStressPercent(state);

            var stepMeters = totalHours > 0d
                ? remainingMeters * hours / totalHours
                : 0d;

            // МЯГКАЯ усталость запоминается ДО шага: истощение усталости — доля
            // её ПРИРОСТА, поэтому нужна разница, а не итог.
            var fatigueBefore = vitals.Fatigue;

            if (playerMoving && !sleeping)
            {
                var multiplier = HasEffect(state, "burnout")
                    ? BurnoutFatigueBuildMultiplier
                    : 1d;

                // Проценты переводятся в единицы сразу: 100% за 18 часов — это
                // 55.6 единицы за игровую минуту, а не 0.09, которое при показе
                // процентов округлялось бы до нуля.
                var fatigueGain =
                    PlayerConditionScale.FromPercent(
                        100d / FatigueBuildHours * hours +
                        100d / FatigueBuildKilometers * stepMeters / 1000d);

                vitals = vitals with
                {
                    Fatigue = vitals.Fatigue + fatigueGain * multiplier
                };
            }
            else if (!playerMoving && !sleeping)
            {
                var recoveryFactor = Math.Clamp(
                    1d - TotalStress(state) / PlayerConditionScale.Maximum,
                    0d,
                    1d);

                vitals = vitals with
                {
                    Fatigue = vitals.Fatigue -
                              PlayerConditionScale.FromPercent(
                                  100d / FatigueRestHours * hours) * recoveryFactor
                };
            }

            vitals = ClampSoft(vitals, state);

            var fatigueIncrease = Math.Max(0d, vitals.Fatigue - fatigueBefore);
            var totalFatiguePercent = TotalFatiguePercent(vitals, state);
            if (totalFatiguePercent > FatigueCriticalPercent)
            {
                // Истощение усталости — ДОЛЯ ПРИРОСТА: четверть до 100% и всё
                // как есть на самой 100% (задано автором: «0,25 за каждую новую
                // единицу; при 100% — 1:1»). Лавина
                // (<see cref="CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier"/>)
                // живёт в ОДНОМ месте на два движка: своя копия здесь уже
                // расходилась с начислением.
                if (fatigueIncrease > 0d)
                {
                    var share = PlayerConditionScale.ToPercent(vitals.Fatigue) >= 100d
                        ? FatigueExhaustionShare
                        : FatigueExhaustionBelowFullShare;
                    state = state with
                    {
                        CumulativeFatigue = Math.Clamp(
                            state.CumulativeFatigue +
                            fatigueIncrease * share *
                            CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(state),
                            0d,
                            PlayerConditionScale.Maximum)
                    };
                }

                // Счётчик часов выше порога остаётся: по нему определяется ВХОД
                // в критическую зону (выгорание выдаётся разово, не продлевается).
                state = state with
                {
                    CriticalFatigueGameSeconds =
                        state.CriticalFatigueGameSeconds + hours * SecondsPerGameHour
                };

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

            if (TotalStressPercent(state) > StressCriticalPercent)
            {
                // Истощение стресса — ДОЛЯ ПРИРОСТА: «на каждые 2 единицы
                // стресса одна единица истощения». Ноль, когда стресс стоит,
                // и ноль, пока суммарный стресс ниже порога — ниже 50% стресс
                // переносится без последствий.
                var exhaustionGain = stressGain * StressExhaustionShare;
                if (exhaustionGain > 0d)
                {
                    state = state with
                    {
                        CumulativeStress = Math.Clamp(
                            state.CumulativeStress + exhaustionGain,
                            0d,
                            PlayerConditionScale.Maximum)
                    };
                }

                state = state with
                {
                    CriticalStressGameSeconds =
                        state.CriticalStressGameSeconds + hours * SecondsPerGameHour
                };
            }
            else
            {
                state = state with { CriticalStressGameSeconds = 0d };
            }

            // Истощение задаёт НЕСНИЖАЕМЫЙ ОСТАТОК стресса: накопленный вред
            // нельзя «стряхнуть» — шкала остаётся не ниже него, пока истощение
            // не снято (отпуск). Верхний предел при этом НЕ снижается: раньше
            // истощение стресса занимало место в шкале, и при 100% стресс было
            // некуда копить — сам механизм себя блокировал.
            state = state with
            {
                Stress = Math.Clamp(
                    state.Stress,
                    Math.Min(state.CumulativeStress, PlayerConditionScale.Maximum),
                    PlayerConditionScale.Maximum)
            };

            var totalStressAfter = TotalStressPercent(state);
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
                ? PlayerConditionScale.FromPercent(
                    CharacterVitalsTuning
                        .FullSleepCumulativeFatigueClearPercent)
                : state.CumulativeFatigue >
                  PlayerConditionScale.FromPercent(
                      CharacterVitalsTuning
                          .FieldSleepCumulativeFatigueFloorPercent)
                    ? Math.Max(
                        PlayerConditionScale.FromPercent(
                            CharacterVitalsTuning
                                .FieldSleepCumulativeFatigueFloorPercent),
                        state.CumulativeFatigue -
                        PlayerConditionScale.FromPercent(
                            CharacterVitalsTuning
                                .FieldSleepCumulativeFatigueClearPercent))
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
        (value ?? PlayerVitalsState.Default).Normalize();

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
            // Усталость — НАКОПИТЕЛЬНАЯ шкала: истощение задаёт её НЕСНИЖАЕМЫЙ
            // ОСТАТОК (нижняя граница), а не уменьшает потолок. Потолок, наоборот,
            // остаётся полным: иначе при 100% истощения усталость некуда было бы
            // набирать, и «больше устать» стало бы невозможно.
            Fatigue = Math.Clamp(
                vitals.Fatigue,
                Math.Min(Math.Max(0d, state.CumulativeFatigue), vitals.MaxFatigue),
                vitals.MaxFatigue)
        };

    private static double EffectiveConsumableMax(double maximum, double cumulativeUnits) =>
        Math.Max(
            0d,
            maximum *
            (1d - Math.Clamp(
                PlayerConditionScale.ToPercent(cumulativeUnits),
                0d,
                100d) / 100d));

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

    /// <summary>Стресс в ЕДИНИЦАХ шкалы (мгновенный + кумулятивный).</summary>
    private static double TotalStress(PlayerConditionState state) =>
        Math.Clamp(
            state.Stress + state.CumulativeStress,
            0d,
            PlayerConditionScale.Maximum);

    /// <summary>Стресс в ПРОЦЕНТАХ — для сравнения с <see cref="StressCriticalPercent"/>.</summary>
    public static double TotalStressPercent(PlayerConditionState state) =>
        PlayerConditionScale.ToPercent(TotalStress(state));

    /// <summary>Усталость в ЕДИНИЦАХ шкалы (мгновенная + кумулятивная).</summary>
    private static double TotalFatigue(PlayerVitalsState vitals, PlayerConditionState state) =>
        Math.Clamp(
            vitals.Fatigue + state.CumulativeFatigue,
            0d,
            PlayerConditionScale.Maximum);

    /// <summary>Усталость в ПРОЦЕНТАХ — для сравнения с <see cref="FatigueCriticalPercent"/>.</summary>
    private static double TotalFatiguePercent(PlayerVitalsState vitals, PlayerConditionState state) =>
        PlayerConditionScale.ToPercent(TotalFatigue(vitals, state));

    private static double ActiveStressSlowdownPercent(PlayerConditionState state) =>
        state.Effects
            .Where(effect => !effect.IsDebuff && effect.RemainingRealSeconds > 0d)
            .Select(effect => effect.StressAccumulationSlowdownPercent)
            .DefaultIfEmpty(0d)
            .Max();
}
