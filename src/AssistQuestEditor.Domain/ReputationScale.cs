namespace AssistQuestEditor.Domain;

/// <summary>
/// Именованный диапазон репутации: название, границы и цвет подписи.
///
/// Границы задаются как <c>[Min, Max)</c> для положительных и <c>(Max, Min]</c>
/// для отрицательных значений: ровно 300 ещё нейтрально, а 300+ уже «Неопасный».
/// Так на каждой границе значение принадлежит ровно одному диапазону.
/// </summary>
public sealed record ReputationRange(
    string Name,
    int Min,
    int Max,
    string TextColor)
{
    public bool Contains(int value) =>
        value >= Min && value < Max;

    /// <summary>Крайняя нижняя граница (для последнего отрицательного диапазона).</summary>
    public bool IsLowest => Min <= ReputationScale.MinReputation;

    /// <summary>Крайняя верхняя граница (для последнего положительного диапазона).</summary>
    public bool IsHighest => Max >= ReputationScale.MaxReputation;
}

/// <summary>
/// Шкала репутации НПЦ.
///
/// Фиксирует три независимых представления одного и того же числа:
///   - <see cref="Resolve"/> — имя диапазона и цвет подписи для UI;
///   - <see cref="FillColor"/> — цвет заливки прогрессбара (серая в нейтральной
///     зоне, lime в положительной, красная в отрицательной);
///   - <see cref="ProgressPercent"/> — заполнение 0..100% от модуля значения.
///
/// Отрицательная репутация НЕ разворачивает прогрессбар: значение -10000 даёт
/// те же 100%, что и +10000, меняется только цвет и знак в подписи.
///
/// Таблица границ (из ТЗ):
///   Нейтральный    -300..300      Подозрительный -500..-300
///   Неопасный       300..500      Чужак          -1000..-500
///   Знакомый        500..1000     Нежелательный  -3000..-1000
///   Приятель       1000..3000     Враг           -6000..-3000 и ниже
///   Друг           3000..6000
///   Свой            6000 и выше
/// </summary>
public static class ReputationScale
{
    /// <summary>Значение, соответствующее 100% заполнения прогрессбара.</summary>
    public const int MaxReputation = 10000;

    /// <summary>Значение, соответствующее 100% заполнения в отрицательную сторону.</summary>
    public const int MinReputation = -10000;

    /// <summary>Граница нейтральной зоны: ниже — красная заливка, выше — lime.</summary>
    public const int NeutralThreshold = 300;

    /// <summary>Цвет заливки в нейтральной зоне.</summary>
    public const string NeutralFillColor = "#8f9baa";

    /// <summary>Цвет заливки при положительной репутации.</summary>
    public const string PositiveFillColor = "#44ff00";

    /// <summary>Цвет заливки при отрицательной репутации.</summary>
    public const string NegativeFillColor = "#d21f1f";

    /// <summary>Цвет подписи нейтрального диапазона: приглушённый белый.</summary>
    public const string NeutralTextColor = "#c8ccd2";

    /// <summary>
    /// Диапазоны от худшего к лучшему. Порядок важен для <see cref="Resolve"/>:
    /// значение ищется первым подходящим диапазоном.
    /// </summary>
    public static readonly IReadOnlyList<ReputationRange> Ranges =
    [
        // «Враг» — единственный отрицательный диапазон, который по ТЗ
        // продолжается и за -6000 («-6000 и более — Враг»), поэтому он один
        // тянется до конца шкалы, а не разбит на дополнительный «Враждебный».
        new("Враг", MinReputation, -3000, "#940000"),
        new("Нежелательный", -3000, -1000, "#a05008"),
        new("Чужак", -1000, -500, "#ada010"),
        new("Подозрительный", -500, -NeutralThreshold, "#5e234d"),
        new("Нейтральный", -NeutralThreshold, NeutralThreshold, NeutralTextColor),
        new("Неопасный", NeutralThreshold, 500, "#00a7bd"),
        new("Знакомый", 500, 1000, "#00614a"),
        new("Приятель", 1000, 3000, "#224f33"),
        new("Друг", 3000, 6000, "#175e0d"),
        new("Свой", 6000, MaxReputation + 1, "#44ff00")
    ];

    public static ReputationRange Neutral =>
        Ranges.First(range => range.Contains(0));

    /// <summary>
    /// Определяет диапазон значения.
    /// Значения за пределами шкалы попадают в крайние диапазоны: репутация может
    /// уйти ниже -10000 или выше 10000, но подпись должна остаться осмысленной.
    /// </summary>
    public static ReputationRange Resolve(int value)
    {
        var clamped = Math.Clamp(value, MinReputation, MaxReputation);

        foreach (var range in Ranges)
        {
            if (range.Contains(clamped))
                return range;
        }

        // Значение ровно MaxReputation принадлежит последнему диапазону,
        // у которого Max = MaxReputation + 1, поэтому сюда попасть нельзя.
        return value > 0 ? Ranges[^1] : Ranges[0];
    }

    /// <summary>
    /// Цвет заливки прогрессбара. Серая в нейтральной зоне, lime при
    /// положительной репутации, красная при отрицательной.
    /// </summary>
    public static string FillColor(int value) =>
        value switch
        {
            > NeutralThreshold => PositiveFillColor,
            < -NeutralThreshold => NegativeFillColor,
            _ => NeutralFillColor
        };

    /// <summary>
    /// Заполнение прогрессбара в процентах от модуля значения: 0..100.
    /// Отрицательное значение не разворачивает шкалу, а даёт тот же процент,
    /// что и положительное: -10000 — это 100%.
    /// </summary>
    public static double ProgressPercent(int value)
    {
        var magnitude = Math.Abs((double)value);
        var percent = magnitude / MaxReputation * 100d;
        return Math.Clamp(percent, 0d, 100d);
    }

    /// <summary>
    /// Подпись значения: проценты не показываются, только само число репутации.
    /// Знак «+» для положительных значений делает рост заметным рядом с минусом.
    /// </summary>
    public static string FormatValue(int value) =>
        value > 0 ? "+" + value : value.ToString();

    /// <summary>
    /// Итоговое состояние для UI одним вызовом: подпись диапазона, её цвет,
    /// цвет заливки и процент заполнения.
    /// </summary>
    public static ReputationView Describe(int value)
    {
        var range = ReputationScale.Resolve(value);
        return new ReputationView(
            value,
            FormatValue(value),
            range.Name,
            range.TextColor,
            FillColor(value),
            ProgressPercent(value),
            $"Репутация: {FormatValue(value)} из {MaxReputation} ({range.Name})");
    }
}

/// <summary>
/// Готовое представление репутации для интерфейса.
/// Считается в домене, чтобы UI не дублировал пороги и цвета.
/// </summary>
public sealed record ReputationView(
    int Value,
    string ValueLabel,
    string RangeName,
    string RangeColor,
    string FillColor,
    double ProgressPercent,
    string Tooltip);
