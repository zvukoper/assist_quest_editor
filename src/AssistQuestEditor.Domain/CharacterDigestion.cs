namespace AssistQuestEditor.Domain;

/// <summary>
/// Содержимое «виртуального желудка»: сколько восстановления ещё не усвоено,
/// сколько места оно занимает и с какой скоростью поступает.
///
/// Зачем отдельная сущность. Восстановление энергии и жидкости по еде и питью
/// НЕ должно быть мгновенным: съеденное переваривается, и шкалы наполняются
/// постепенно. Желудок — скрытый (интерфейс его не показывает), но
/// СОХРАНЯЕМЫЙ: иначе загрузка сохранения во время переваривания мгновенно
/// отдала бы остаток, то есть механика вела бы себя по-разному до и после
/// загрузки.
///
/// Хранится в ЕДИНИЦАХ шкалы 0..10000 (как и всё состояние персонажа), ставки —
/// в единицах за ИГРОВУЮ секунду: игровое время идёт 1:1 с реальным, но при
/// ускорении FF желудок обязан переваривать быстрее вместе с миром.
/// </summary>
public sealed record StomachContents(
    double EnergyRemaining,
    double HydrationRemaining,
    double EnergyPerGameSecond,
    double HydrationPerGameSecond,
    double VolumeFraction = 0d)
{
    public static StomachContents Empty => new(0d, 0d, 0d, 0d, 0d);

    /// <summary>
    /// Желудок пуст. Порог в тысячную долю единицы нужен из-за дробных ставок:
    /// после ровного времени переваривания остаток равен 1e-13, а не нулю, и
    /// строгое сравнение с нулём заставляло бы желудок «жевать» лишний шаг.
    /// </summary>
    public bool IsEmpty =>
        EnergyRemaining <= 0.001d &&
        HydrationRemaining <= 0.001d;

    /// <summary>
    /// Занятый объём желудка как доля литра (0..1).
    ///
    /// Нужен, потому что вместимость желудка ограничена, а ВРЕМЯ переваривания
    /// от неё не зависит: скорость усвоения постоянна (см.
    /// <see cref="CharacterDigestion"/>). Без этого поля игрок мог бы съесть
    /// пять обедов подряд и получить пять обедов восстановления, что физически
    /// невозможно.
    /// </summary>
    public double OccupiedLiters => VolumeFraction * CharacterDigestion.StomachVolumeLiters;

    public double FreeLiters =>
        Math.Max(
            0d,
            CharacterDigestion.StomachVolumeLiters - OccupiedLiters);
}

/// <summary>
/// Пищеварение: превращение «выпил/съел» в постепенное восстановление.
///
/// Модель взята из физических величин, заданных автором, а не назначена
/// «на глаз»:
///   • шкала энергии — 5000 ккал, шкала жидкости — 15000 мл (в единицах
///     хранения это по-прежнему 0..10000);
///   • объём желудка — 1 литр;
///   • полный желудок ЕДЫ опустошается за 2 игровых часа;
///   • жидкость усваивается со скоростью 1,35 литра в час.
///
/// Отсюда следуют ОБЕ ставки, и они постоянны:
///   • энергия — весь объём шкалы за 2 часа (5000 единиц в час);
///   • жидкость — 1350 мл в час = 9% шкалы в час (900 единиц в час, то есть
///     15 единиц в минуту).
///
/// Скорость жидкости задана НЕ «на глаз», а по замеру автора: он наблюдал
/// «+7,4 единицы в минуту» и попросил «+10 единиц в минуту». Эти 7,4 — значение
/// при ПОНИЖЕННОМ метаболизме: прежний литр в час давал 11,1 единицы в минуту,
/// а пониженный метаболизм замедляет пищеварение в 1,5 раза (11,1 / 1,5 = 7,4).
/// Отсюда 1 л/ч × 10 / 7,4 ≈ 1,35 л/ч: в том же состоянии игрок видит ровно
/// заказанные 10 единиц в минуту, а при обычном метаболизме — 15.
///
/// Почему прежнего литра в час не хватало. Организм в покое тратит 100% шкалы за
/// 90 часов (≈9,3 единицы в минуту), то есть литр в час перекрывал расход всего
/// в 1,2 раза, а при пониженном метаболизме — НЕ перекрывал вовсе (7,4 < 9,3, а
/// под нагрузкой расход ещё выше). Динамика после питья оставалась падающей —
/// именно на это и жаловался автор.
///
/// Масса порции при этом задаёт НЕ длительность, а занятое место: порция больше
/// литра не влезает, и лишнее не съедается. Прежняя модель работала наоборот
/// (одинаковое время для любой порции), из-за чего мелкая еда не успевала
/// перекрыть расход и динамика энергии оставалась красной — на это и жаловался
/// автор в прошлый раз.
///
/// Метаболизм меняет И скорость, И объём:
/// повышенный — в 2,5 раза быстрее и +0,5% объёма, пониженный — в 1,5 раза
/// медленнее и −0,5%. Проценты объёма считаются от максимума шкалы (как все
/// пороги движка), а не от порции: иначе бонус был бы неразличим, потому что
/// одна порция — это единицы процентов шкалы.
/// </summary>
public static class CharacterDigestion
{
    /// <summary>
    /// Сколько КИЛОКАЛОРИЙ вмещает полная шкала энергии (задано автором).
    ///
    /// Единицы хранения (0..10000) остались прежними, но теперь у них есть
    /// физический смысл: 10000 единиц — это 5000 ккал.
    /// </summary>
    public const double EnergyScaleKilocalories = 5000d;

    /// <summary>Сколько МИЛЛИЛИТРОВ вмещает полная шкала жидкости.</summary>
    public const double HydrationScaleMilliliters = 15000d;

    /// <summary>Объём желудка: 1 литр (задано автором).</summary>
    public const double StomachVolumeLiters = 1d;

    /// <summary>
    /// Жидкость усваивается со скоростью 1,35 литра в час.
    ///
    /// Число выведено из замера автора: он видел «+7,4 единицы в минуту» и
    /// попросил «+10 единиц в минуту». 7,4 — это прежний литр в час при
    /// ПОНИЖЕННОМ метаболизме (11,1 / 1,5), поэтому новый объём равен
    /// 1 × 10 / 7,4 ≈ 1,35 л/ч: в том же состоянии игрок видит заказанные 10, а
    /// при обычном метаболизме — 15 единиц в минуту.
    ///
    /// Час ИГРОВОЙ: игровое время идёт 1:1 с реальным, но при ускорении FF
    /// желудок обязан переваривать быстрее вместе с миром.
    ///
    /// Величина задана в ЛИТРАХ, потому что так её называет автор, а шкала
    /// измеряется в миллилитрах: пересчёт живёт в одном месте —
    /// <see cref="HoursPerLitreOfWater"/> и <see cref="Begin"/>.
    /// </summary>
    public const double HydrationAbsorptionLitersPerHour = 1.35d;

    /// <summary>
    /// За сколько игровых часов усваивается ОДИН литр жидкости.
    ///
    /// Выведено из <see cref="HydrationAbsorptionLitersPerHour"/>, а не задано
    /// вторым числом: две константы об одном и том же разошлись бы при первой
    /// же правке одной из них, и «литр за час» из подсказки перестал бы
    /// совпадать с начислением.
    /// </summary>
    public const double HoursPerLitreOfWater =
        1d / HydrationAbsorptionLitersPerHour;

    /// <summary>
    /// Полный желудок еды опустошается за 2 игровых часа (задано автором).
    /// Жидкость уходит по скорости усвоения — литр за
    /// <see cref="HoursPerLitreOfWater"/> игрового часа.
    /// </summary>
    public const double FoodStomachEmptyHours = 2d;

    /// <summary>Эталонные объёмы порций, к которым привязаны каталоги предметов.</summary>
    public const double ReferenceFoodGrams = 500d;
    public const double ReferenceDrinkMilliliters = 500d;

    /// <summary>Ускорение при повышенном метаболизме (в 2,5 раза).</summary>
    public const double ElevatedMetabolismSpeedFactor = 1d / 2.5d;
    public const double ReducedMetabolismSpeedFactor = 1.5d;

    /// <summary>Бонус/штраф объёма восстановления при отклонении метаболизма.</summary>
    public const double ElevatedMetabolismVolumeBonus = 0.005d;
    public const double ReducedMetabolismVolumePenalty = 0.005d;

    private const double SecondsPerGameHour = 3600d;

    /// <summary>
    /// Объём порции как доля желудка (0..1).
    ///
    /// Это и есть место, где живёт правило «объём желудка 1 литр»: 500 г еды
    /// занимают половину желудка, 500 мл напитка — столько же (миллилитр и грамм
    /// здесь приравнены: и то и другое — объём порции).
    /// </summary>
    public static double PortionFraction(double gramsOrMilliliters)
    {
        var grams = gramsOrMilliliters;
        if (!double.IsFinite(grams) || grams <= 0d)
            return 0d;

        return Math.Clamp(
            grams / (StomachVolumeLiters * 1000d),
            0d,
            1d);
    }

    /// <summary>
    /// Начинает (или дополняет) переваривание порции.
    ///
    /// <paramref name="energyPercent"/> и <paramref name="hydrationPercent"/> —
    /// НОМИНАЛ порции в процентах шкалы (то, что раньше начислялось мгновенно),
    /// а <paramref name="portionFraction"/> — её объём внутри желудка.
    /// Ноль означает «порция этого ресурса не даёт» — тогда и ставка, и объём по
    /// этой шкале нулевые.
    ///
    /// Модель очереди с ограничением вместимости: если игрок ест, пока предыдущая
    /// порция ещё переваривается, остаток складывается, но ЖЕЛУДОК БОЛЬШЕ ЛИТРА
    /// НЕ ВМЕЩАЕТ. Порция, которая не влезает, принимается лишь в свободной части
    /// — лишнее не съедается вовсе. Ставка усвоения при этом НЕ меняется: она
    /// определяется физиологией, а не количеством съеденного.
    /// </summary>
    public static StomachContents Begin(
        StomachContents current,
        double energyPercent,
        double hydrationPercent,
        double energyMaximum,
        double hydrationMaximum,
        bool elevatedMetabolism,
        bool reducedMetabolism,
        double portionFraction = 1d)
    {
        var contents = (current ?? StomachContents.Empty).Normalize();

        if (!double.IsFinite(portionFraction))
            portionFraction = 1d;
        portionFraction = Math.Clamp(portionFraction, 0d, 1d);

        // Желудок не резиновый: принимаем столько, сколько влезает в остаток
        // литрового объёма. Ноль свободного места — порция не съедена.
        var freeFraction = Math.Max(
            0d,
            1d - contents.VolumeFraction);
        var accepted = Math.Min(portionFraction, freeFraction);

        if (accepted <= 0d)
            return contents;

        // Питательность масштабируется ПРИНЯТОЙ долей: порция, которая влезла
        // наполовину, и накормит наполовину. Без этого не влезшая часть всё
        // равно давала бы полный объём восстановления.
        var share = portionFraction > 0d
            ? accepted / portionFraction
            : 0d;

        var energyAdd = Volume(
            energyPercent,
            energyMaximum,
            elevatedMetabolism,
            reducedMetabolism) * share;
        var hydrationAdd = Volume(
            hydrationPercent,
            hydrationMaximum,
            elevatedMetabolism,
            reducedMetabolism) * share;

        if (energyAdd <= 0d && hydrationAdd <= 0d)
            return contents;

        return new StomachContents(
            contents.EnergyRemaining + energyAdd,
            contents.HydrationRemaining + hydrationAdd,
            RatePerSecond(
                energyMaximum,
                1d / FoodStomachEmptyHours,
                elevatedMetabolism,
                reducedMetabolism),
            RatePerSecond(
                hydrationMaximum,
                // Скорость усвоения жидкости задана ФИЗИЧЕСКИ — в литрах в час, — и
                // не зависит от того, сколько литров вмещает шкала. В долях шкалы
                // это HydrationAbsorptionLitersPerHour * 1000 / 15000.
                HydrationAbsorptionLitersPerHour * 1000d /
                HydrationScaleMilliliters,
                elevatedMetabolism,
                reducedMetabolism),
            Math.Clamp(
                contents.VolumeFraction + accepted,
                0d,
                1d));
    }

    /// <summary>
    /// Продвигает пищеварение на <paramref name="gameSeconds"/> игровых секунд.
    ///
    /// Возвращает новое содержимое желудка и прибавку к шкалам в ЕДИНИЦАХ.
    /// Остаток никогда не «переполняется»: отдаётся ровно то, что успело
    /// усвоиться, и желудок опустошается ровно тогда, когда время вышло.
    /// </summary>
    public static DigestionStep Advance(
        StomachContents current,
        double gameSeconds)
    {
        var contents = (current ?? StomachContents.Empty).Normalize();
        var seconds = Math.Max(
            0d,
            double.IsFinite(gameSeconds) ? gameSeconds : 0d);

        if (contents.IsEmpty || seconds <= 0d)
            return new DigestionStep(contents, 0d, 0d);

        // Ставка ПОСТОЯННА на всё время переваривания: её задаёт физиология
        // (весь объём шкалы за 2 часа для еды, литр в час для жидкости), а не
        // размер порции. Поэтому шаг — это просто «сколько успело усвоиться»,
        // без второй бухгалтерии оставшегося времени, которая разошлась бы с
        // остатком после округления.
        var energyGain = Math.Min(
            contents.EnergyRemaining,
            contents.EnergyPerGameSecond * seconds);
        var hydrationGain = Math.Min(
            contents.HydrationRemaining,
            contents.HydrationPerGameSecond * seconds);

        var energyRemaining = Math.Max(0d, contents.EnergyRemaining - energyGain);
        var hydrationRemaining = Math.Max(0d, contents.HydrationRemaining - hydrationGain);

        var isEmpty =
            energyRemaining <= 0d &&
            hydrationRemaining <= 0d;

        var next = new StomachContents(
            energyRemaining,
            hydrationRemaining,
            energyRemaining > 0d ? contents.EnergyPerGameSecond : 0d,
            hydrationRemaining > 0d ? contents.HydrationPerGameSecond : 0d,
            // Объём освобождается вместе с содержимым: пустой желудок снова
            // вмещает литр.
            isEmpty ? 0d : contents.VolumeFraction);

        return new DigestionStep(next, energyGain, hydrationGain);
    }

    public static StomachContents Normalize(this StomachContents contents)
    {
        contents ??= StomachContents.Empty;

        var energyRemaining = NormalizeNonNegative(contents.EnergyRemaining);
        var hydrationRemaining = NormalizeNonNegative(contents.HydrationRemaining);

        if (energyRemaining <= 0d) energyRemaining = 0d;
        if (hydrationRemaining <= 0d) hydrationRemaining = 0d;

        var energyRate = NormalizeNonNegative(contents.EnergyPerGameSecond);
        var hydrationRate = NormalizeNonNegative(contents.HydrationPerGameSecond);

        if (energyRemaining <= 0d) energyRate = 0d;
        if (hydrationRemaining <= 0d) hydrationRate = 0d;

        var volume = double.IsFinite(contents.VolumeFraction)
            ? Math.Clamp(contents.VolumeFraction, 0d, 1d)
            : 0d;

        if (energyRemaining <= 0d && hydrationRemaining <= 0d)
            volume = 0d;

        return new StomachContents(
            energyRemaining,
            hydrationRemaining,
            energyRate,
            hydrationRate,
            volume);
    }

    /// <summary>
    /// Сколько ЕДИНИЦ В ИГРОВУЮ СЕКУНДУ поступает по шкале.
    ///
    /// <paramref name="shareOfScalePerHour"/> — доля ШКАЛЫ в час (для еды это
    /// 1/2, потому что полный желудок уходит за 2 часа; для жидкости —
    /// 1350/15000, потому что усваивается 1,35 литра в час из
    /// пятнадцатилитровой шкалы). Метаболизм ускоряет или замедляет эту долю.
    ///
    /// Именно постоянство этой величины делает динамику предсказуемой: пока
    /// желудок не пуст, шкала растёт с известной скоростью, и «Текущая динамика»
    /// в мониторе не врёт.
    /// </summary>
    private static double RatePerSecond(
        double maximum,
        double shareOfScalePerHour,
        bool elevatedMetabolism,
        bool reducedMetabolism)
    {
        var scale = double.IsFinite(maximum) && maximum > 0d
            ? maximum
            : PlayerConditionScale.Maximum;

        if (!double.IsFinite(shareOfScalePerHour) || shareOfScalePerHour <= 0d)
            return 0d;

        var speedMultiplier = elevatedMetabolism
            ? 1d / ElevatedMetabolismSpeedFactor
            : reducedMetabolism
                ? 1d / ReducedMetabolismSpeedFactor
                : 1d;

        return scale *
            shareOfScalePerHour *
            speedMultiplier /
            SecondsPerGameHour;
    }

    /// <summary>
    /// Сколько ЕДИНИЦ восстановления даёт порция.
    ///
    /// Номинал порции в процентах плюс бонус/штраф метаболизма, считаемый от
    /// максимума шкалы.
    /// </summary>
    private static double Volume(
        double percent,
        double maximum,
        bool elevatedMetabolism,
        bool reducedMetabolism)
    {
        if (percent <= 0d)
            return 0d;

        var scale = double.IsFinite(maximum) && maximum > 0d
            ? maximum
            : PlayerConditionScale.Maximum;

        var nominal = PlayerConditionScale.UnitsPerPercent * percent;

        var bonusFraction = elevatedMetabolism
            ? ElevatedMetabolismVolumeBonus
            : reducedMetabolism
                ? -ReducedMetabolismVolumePenalty
                : 0d;

        return Math.Max(0d, nominal + scale * bonusFraction);
    }

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) && value > 0.001d ? value : 0d;
}

/// <summary>Результат шага пищеварения.</summary>
public sealed record DigestionStep(
    StomachContents Contents,
    double EnergyGain,
    double HydrationGain)
{
    public bool IsEmpty => Contents.IsEmpty;
}
