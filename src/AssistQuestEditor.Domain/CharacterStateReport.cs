using System.Globalization;
using System.Text;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Отчёт об изменении состояния персонажа после события (сон, ночлег, отдых).
///
/// Зачем отдельный тип. Автор просил: «после событий типа ночлега, сна и отдыха
/// в журнал событий выводить отчёт о том, как и какие показатели изменились,
/// какие получены перки». Сравнение двух состояний — это ПРАВИЛО, а не текст
/// интерфейса: считать «сколько ушло стресса» на стороне Host значило бы
/// дублировать знание о том, какие шкалы существуют и в каких единицах они
/// живут. Здесь оно живёт рядом с самим движком, а Host только печатает готовые
/// строки.
///
/// Отчёт печатается РУССКИМИ названиями шкал, потому что он идёт прямо в журнал.
/// Разметка ссылок (<c>[[metric:energy]]</c>) добавляется здесь же: кликабельность
/// названий — требование автора, и делать это в Host значило бы разбирать текст
/// дважды.
/// </summary>
public static class CharacterStateReport
{
    /// <summary>
    /// Разница меньше этого значения не показывается: движок считает в единицах
    /// 0..10000 с дробной частью, и «+0 ед.» от накопленной погрешности выглядел
    /// бы как изменение, которого игрок не заметил.
    /// </summary>
    private const double VisibleUnits = 0.5d;

    /// <summary>
    /// Строит отчёт по разнице двух состояний.
    ///
    /// Порядок строк — от самого важного для игрока: здоровье, энергия, жидкость,
    /// усталость, стресс, гигиена, устойчивость, метаболизм, затем кумулятивные
    /// шкалы, затем полученные и снятые эффекты. Так журнал читается сверху вниз
    /// без поиска.
    ///
    /// Подпись ссылки — ЧЕЛОВЕЧЕСКОЕ название шкалы, а не ключ: журнал печатает
    /// подпись как текст, и «[[metric:energy]]» показал бы игроку «energy».
    /// </summary>
    public static IReadOnlyList<string> Describe(
        PlayerVitalsState before,
        PlayerConditionState beforeConditions,
        PlayerVitalsState after,
        PlayerConditionState afterConditions)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        before = before.Normalize();
        after = after.Normalize();
        beforeConditions = (beforeConditions ?? PlayerConditionState.Empty).Normalize();
        afterConditions = (afterConditions ?? PlayerConditionState.Empty).Normalize();

        var lines = new List<string>();

        AddScale(lines, "health", "Здоровье", before.Health, after.Health);
        AddScale(lines, "energy", "Энергия", before.Energy, after.Energy);
        AddScale(lines, "hydration", "Жидкость", before.Hydration, after.Hydration);
        AddScale(lines, "fatigue", "Усталость", before.Fatigue, after.Fatigue);
        AddScale(
            lines,
            "stress",
            "Стресс",
            beforeConditions.Stress,
            afterConditions.Stress);
        AddScale(
            lines,
            "hygiene",
            "Гигиена",
            before.Hygiene,
            after.Hygiene,
            // Гигиена — скрытая шкала: отдельного блока в мониторе у неё нет,
            // поэтому ссылку не ставим. Мёртвая ссылка хуже её отсутствия.
            linkable: false);
        AddScale(
            lines,
            "resilience",
            "Устойчивость",
            before.Resilience,
            after.Resilience);
        AddScale(
            lines,
            "metabolism",
            "Метаболизм",
            before.Metabolism,
            after.Metabolism);

        // Кумулятивные шкалы печатаются тем же правилом, что и остальные — только
        // при заметном изменении: они меняются редко, и постоянные
        // «Истощение энергии: 0% → 0%» превратили бы отчёт в шум, в котором не
        // видно настоящих изменений.
        AddScale(
            lines,
            "cumulativeEnergy",
            "Истощение энергии",
            beforeConditions.CumulativeEnergy,
            afterConditions.CumulativeEnergy);
        AddScale(
            lines,
            "cumulativeHydration",
            "Истощение жидкости",
            beforeConditions.CumulativeHydration,
            afterConditions.CumulativeHydration);
        AddScale(
            lines,
            "cumulativeFatigue",
            "Истощение усталости",
            beforeConditions.CumulativeFatigue,
            afterConditions.CumulativeFatigue);
        AddScale(
            lines,
            "cumulativeStress",
            "Кумулятивный стресс",
            beforeConditions.CumulativeStress,
            afterConditions.CumulativeStress);

        AddEffects(lines, beforeConditions, afterConditions);

        if (lines.Count == 0)
            lines.Add("Показатели не изменились.");

        return lines;
    }

    /// <summary>
    /// Одна строка отчёта: «• [[metric:energy]] Энергия: 20% → 100% (+80%)».
    ///
    /// Ссылка разметкой, а не отдельным полем: кликабельность рисует Хост, и
    /// ему нужен только текст с разметкой. Проценты выбраны для показа, потому
    /// что шкалы для игрока измеряются в процентах; единицы — там, где процент
    /// слишком груб (мелкое изменение просто округлилось бы до нуля).
    /// </summary>
    private static void AddScale(
        List<string> lines,
        string key,
        string label,
        double beforeUnits,
        double afterUnits,
        bool linkable = true)
    {
        var delta = afterUnits - beforeUnits;

        if (Math.Abs(delta) < VisibleUnits)
            return;

        var beforePercent = PlayerConditionScale.ToPercent(beforeUnits);
        var afterPercent = PlayerConditionScale.ToPercent(afterUnits);
        var deltaPercent = Math.Abs(delta / PlayerConditionScale.UnitsPerPercent);
        var sign = delta > 0d ? "+" : "−";

        var text = new StringBuilder();
        text.Append("• ");

        // Разметка ссылки нужна только там, где у монитора ЕСТЬ блок этой шкалы:
        // по клику монитор выделяет блок по ключу, и ключ без блока дал бы
        // мёртвую ссылку. Подпись — название шкалы, а не ключ.
        if (linkable)
        {
            text.Append("[[metric:");
            text.Append(key);
            text.Append(':');
            text.Append(label);
            text.Append("]] ");
        }

        text.Append(label);
        text.Append(": ");
        text.Append(FormatPercent(beforePercent));
        text.Append("% → ");
        text.Append(FormatPercent(afterPercent));
        text.Append("% (");
        text.Append(sign);
        text.Append(FormatPercent(deltaPercent));
        text.Append('%');

        // Мелкую разницу в процентах не видно: она округлялась бы до нуля,
        // и строка «50% → 50% (+0%)» читалась бы как «ничего не изменилось»,
        // хотя изменение есть. Там, где процент показывает ноль, печатаем
        // единицы шкалы.
        if (Math.Round(deltaPercent, 1) == 0d)
        {
            text.Append(", ");
            text.Append(FormatUnits(Math.Abs(delta)));
            text.Append(" ед.");
        }

        text.Append(')');
        lines.Add(text.ToString());
    }

    /// <summary>
    /// Полученные и снятые эффекты.
    ///
    /// Требование автора: «какие получены перки». Печатаются оба направления —
    /// потерянный дебафф («Бомж») для игрока так же важен, как полученный бафф,
    /// и умолчание о нём выглядело бы как «ничего не произошло».
    /// </summary>
    private static void AddEffects(
        List<string> lines,
        PlayerConditionState before,
        PlayerConditionState after)
    {
        var beforeIds = before.Effects
            .Select(effect => effect.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var afterIds = after.Effects
            .Select(effect => effect.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var effect in after.Effects)
        {
            if (beforeIds.Contains(effect.Id))
                continue;

            lines.Add(
                "• Получен " + (effect.IsDebuff ? "дебафф" : "бафф") +
                " [[perk:" + effect.Id + ":" +
                (effect.IsDebuff ? "debuff" : "buff") + ":" +
                effect.Name + "]] " +
                effect.Name + ".");
        }

        foreach (var effect in before.Effects)
        {
            if (afterIds.Contains(effect.Id))
                continue;

            lines.Add(
                "• Снят " + (effect.IsDebuff ? "дебафф" : "бафф") +
                " [[perk:" + effect.Id + ":" +
                (effect.IsDebuff ? "debuff" : "buff") + ":" +
                effect.Name + "]] " +
                effect.Name + ".");
        }
    }

    private static string FormatPercent(double percent) =>
        percent.ToString(
            percent == Math.Floor(percent) ? "0" : "0.#",
            CultureInfo.InvariantCulture);

    private static string FormatUnits(double units) =>
        units.ToString(
            units == Math.Floor(units) ? "0" : "0.#",
            CultureInfo.InvariantCulture);
}
