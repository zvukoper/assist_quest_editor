namespace AssistQuestEditor.Domain;

/// <summary>
/// ОДНА порция в «Пищеварении»: что съедено, сколько от неё осталось и с какой
/// скоростью она уходит.
///
/// МОДЕЛЬ ИЗМЕНЕНА НА ПРОПУСКНУЮ СПОСОБНОСТЬ (см. DigestionRefactoring.md).
/// Прежняя порция помнила ДВЕ НЕЗАВИСИМЫЕ ставки (энергия «полный желудок за
/// 2 часа», жидкость «1,35 л/ч») и таяла по своему таймеру: еда и вода не
/// мешали друг другу вовсе, а время усвоения не зависело от того, что ещё
/// лежит рядом. Теперь у пищеварения ОДНА пропускная способность, которая
/// делится между всеми порциями по их «весу» (вода усваивается легче сухой
/// пищи), поэтому смесь разных продуктов замедляет каждую из них — ровно то,
/// что задал автор.
///
/// Поэтому порция хранит ФИЗИЧЕСКИЕ величины, а не остатки шкал:
///   • <paramref name="MassTotal"/> — объём порции в миллилитрах (для воды
///     миллилитр равен грамму). Это и есть место, которое порция занимает в
///     пищеварении;
///   • <paramref name="MassRemaining"/> — сколько объёма ещё не усвоено;
///   • <paramref name="KilocaloriesTotal"/> — энергия порции;
///   • <paramref name="WaterMillilitersTotal"/> — вода в порции.
///
/// Остаток ВОДЫ и ЭНЕРГИИ — ПРОИЗВОДНЫЕ от <see cref="RemainingShare"/>: порция
/// теряет объём равномерно, а вместе с ним уходят и вода, и калории. Отдельных
/// счётчиков нет намеренно: два счётчика об одном и том же разошлись бы после
/// первой же правки, и игрок получил бы еду, из которой «вытекла» вода, но
/// остались калории (или наоборот).
/// </summary>
public sealed record StomachPortion(
    string ItemId,
    double MassTotal,
    double MassRemaining,
    double KilocaloriesTotal,
    double WaterMillilitersTotal,
    // БОНУС/ШТРАФ ОБЪЁМА ВОССТАНОВЛЕНИЯ от метаболизма, как ДОЛЯ МАКСИМУМА
    // ШКАЛЫ (+0,005 при повышенном, −0,005 при пониженном) — ровно та величина,
    // которой задан автор: «усвоение ×2,5 и +0,5% объёма».
    //
    // Почему ПРИБАВКА, а не множитель. Замер автора выражен в ДОЛЯХ ШКАЛЫ, а не
    // в процентах от порции: 0,5% шкалы — это 50 единиц независимо от того, что
    // съедено. Множитель «+0,5% от порции» дал бы у обеда на 320 ккал целых
    // +7,8%, то есть переписал бы калибровку, которую автор уже согласовал.
    //
    // Прибавка действует ТОЛЬКО на ту шкалу, которую порция и так восполняет:
    // у воды энергия остаётся НУЛЕВОЙ (иначе вода заводила бы восстановление
    // энергии, и в пищеварении появлялась бы «чужая» шкала).
    //
    // ВЫЧИСЛЯЕМОЕ поле, а не зафиксированное: его переписывает
    // <see cref="CharacterDigestion.Materialize"/> под ТЕКУЩИЙ метаболизм на
    // каждом шаге пищеварения. Раньше оно застывало на моменте приёма, и тот же
    // эксплоит, что закрыт у скорости, работал на объёме: поесть при 80% и
    // получить +0,5% навсегда, потом упасть до 20%.
    double YieldBonusFraction,
    double PerGameSecond)
{
    /// <summary>
    /// ВОДА В СОСТАВЕ порции, доля 0..1. Главная величина формулы: именно она
    /// делает воду «лёгкой», а сухой хлеб — «тяжёлым» (см.
    /// <see cref="CharacterDigestion.WaterContentSpeedFactor"/>).
    /// </summary>
    public double WaterContent =>
        MassTotal > 0.001d
            ? Math.Clamp(WaterMillilitersTotal / MassTotal, 0d, 1d)
            : 0d;

    /// <summary>Какая доля порции ещё не усвоена (1 — целая, 0 — пустая).</summary>
    public double RemainingShare =>
        MassTotal > 0.001d
            ? Math.Clamp(MassRemaining / MassTotal, 0d, 1d)
            : 0d;

    /// <summary>
    /// Остаток ЭНЕРГИИ в ЕДИНИЦАХ ШКАЛЫ — рабочая величина движка.
    ///
    /// Считается ОДНОЙ формулой, из которой выведена и ставка (см.
    /// <see cref="EnergyPerGameSecond"/>), поэтому порция физически не может
    /// «отдать» больше, чем в ней есть. Ноль там, где порция энергии не несёт
    /// (вода): именно это делает остаток по «чужой» шкале нулевым и не даёт
    /// появляться лишней порции в пищеварении.
    /// </summary>
    public double EnergyRemainingUnits =>
        CharacterDigestion.EnergyUnits(KilocaloriesTotal) > 0d
            ? EnergyTotalUnits * RemainingShare
            : 0d;

    /// <summary>Остаток ЖИДКОСТИ в единицах шкалы — рабочая величина движка.</summary>
    public double HydrationRemainingUnits =>
        CharacterDigestion.HydrationUnits(WaterMillilitersTotal) > 0d
            ? HydrationTotalUnits * RemainingShare
            : 0d;

    /// <summary>Сколько энергии ещё осталось отдать шкале, ккал.</summary>
    public double KilocaloriesRemaining =>
        CharacterDigestion.KilocaloriesFromUnits(EnergyRemainingUnits);

    /// <summary>Сколько воды ещё осталось отдать шкале, мл.</summary>
    public double WaterMillilitersRemaining =>
        CharacterDigestion.MillilitersFromUnits(HydrationRemainingUnits);

    /// <summary>
    /// ВЕСЬ объём восстановления энергии, который порция отдаст за своё время:
    /// питательность плюс бонус метаболизма.
    /// </summary>
    private double EnergyTotalUnits =>
        CharacterDigestion.EnergyUnits(KilocaloriesTotal) +
        YieldBonusFraction * PlayerConditionScale.Maximum;

    private double HydrationTotalUnits =>
        CharacterDigestion.HydrationUnits(WaterMillilitersTotal) +
        YieldBonusFraction * PlayerConditionScale.Maximum;

    /// <summary>
    /// Порция усвоена. Порог в тысячную долю миллилитра: дробные ставки
    /// оставляют 1e-13 вместо нуля, и строгое сравнение с нулём держало бы
    /// пустую порцию в пищеварении лишний шаг.
    /// </summary>
    public bool IsEmpty => MassRemaining <= 0.001d;

    /// <summary>
    /// Сколько ИГРОВЫХ СЕКУНД осталось до исчезновения порции — по ЕЁ ТЕКУЩЕЙ
    /// ставке. Ставка пересчитывается при каждом изменении состава
    /// (<see cref="CharacterDigestion.Advance"/>), поэтому смесь продуктов честно
    /// удлиняет срок, а не показывает «сколько было бы, если бы порция была одна».
    /// </summary>
    public double RemainingGameSeconds
    {
        get
        {
            if (PerGameSecond <= 0d)
                return 0d;

            var remaining = MassRemaining / PerGameSecond;
            return double.IsFinite(remaining) && remaining > 0d ? remaining : 0d;
        }
    }
    /// <summary>Сколько осталось перевариваться, в ИГРОВЫХ МИНУТАХ.</summary>
    public double RemainingGameMinutes => RemainingGameSeconds / 60d;

    /// <summary>
    /// Доля ВМЕСТИМОСТИ, которую занимает ПОРЦИЯ (0..1).
    ///
    /// Отдельно от <see cref="StomachContents.VolumeFraction"/>, которая
    /// описывает ВСЁ содержимое: интерфейс рисует место КАЖДОЙ плитки, а сводка
    /// — общий занятый объём. Считать одно через другое нельзя, поэтому обе
    /// величины выводятся из миллилитров одной формулой.
    /// </summary>
    public double VolumeFraction =>
        CharacterDigestion.PortionFraction(MassRemaining);

    /// <summary>Объём порции в ЛИТРАХ — для подписей, которые читают литры.</summary>
    public double VolumeLiters => MassRemaining / 1000d;

    /// <summary>Объём порции в МИЛЛИЛИТРАХ.</summary>
    public double VolumeMilliliters => MassRemaining;

    /// <summary>
    /// Ставка НАЧИСЛЕНИЯ энергии, единиц шкалы за игровую секунду.
    ///
    /// Выводится из ставки усвоения ОБЪЁМА той же пропорцией, что и остаток:
    /// сколько миллилитров уходит за секунду, столько же долей питательности
    /// приходит в шкалу. Поэтому остаток и скорость не могут разойтись, а
    /// порция не может отдать больше, чем в ней есть.
    /// </summary>
    public double EnergyPerGameSecond =>
        MassTotal > 0.001d
            ? EnergyTotalUnits *
              Math.Max(0d, PerGameSecond) / MassTotal
            : 0d;

    /// <summary>Ставка начисления жидкости, единиц шкалы за игровую секунду.</summary>
    public double HydrationPerGameSecond =>
        MassTotal > 0.001d
            ? HydrationTotalUnits *
              Math.Max(0d, PerGameSecond) / MassTotal
            : 0d;
}

/// <summary>
/// Содержимое «Пищеварения»: что лежит, сколько места занято и с какой
/// скоростью это поступает в шкалы.
///
/// Зачем отдельная сущность. Восстановление энергии и жидкости по еде и питью
/// НЕ должно быть мгновенным: съеденное переваривается, и шкалы наполняются
/// постепенно. Пищеварение — скрытое (интерфейс показывает его отдельным блоком
/// монитора), но СОХРАНЯЕМОЕ: иначе загрузка сохранения во время переваривания
/// мгновенно отдала бы остаток, то есть механика вела бы себя по-разному до и
/// после загрузки.
///
/// Первые четыре поля — ОСТАТКИ ШКАЛ в единицах 0..10000 (как и всё состояние
/// персонажа), а не миллилитры: их читает движок и пишет сохранение, и они
/// обязаны быть в той же размерности, что и шкалы. Физические величины живут в
/// <see cref="Portions"/>, а суммы ВЫВОДЯТСЯ из порций
/// (<see cref="CharacterDigestion.Normalize"/>) — держать их отдельными числами
/// рядом со списком значило бы позволить им разойтись, и расхождение было бы
/// невидимым: монитор рисовал бы одно, а движок начислял другое.
/// </summary>
public sealed record StomachContents(
    double EnergyRemaining,
    double HydrationRemaining,
    double EnergyPerGameSecond,
    double HydrationPerGameSecond,
    double VolumeFraction = 0d)
{
    public static StomachContents Empty => new(0d, 0d, 0d, 0d, 0d);

    /// <summary>Порции по отдельности — то, из чего складываются числа выше.</summary>
    public IReadOnlyList<StomachPortion> Portions { get; init; } =
        Array.Empty<StomachPortion>();

    /// <summary>
    /// ТЕКУЩИЙ метаболизм, влияющий на пищеварение ПРЯМО СЕЙЧАС, %.
    ///
    /// Раньше здесь лежала пара флагов (<c>ElevatedMetabolism</c> и
    /// <c>ReducedMetabolism</c>), снятых в МОМЕНТ ПРИЁМА пищи, и ставки порций
    /// оставались такими навсегда. Это давало эксплоит: игрок знал, что сейчас
    /// примет препарат, роняющий метаболизм, — и ел заранее, при высоком
    /// метаболизме. Еда получала «сохранённую» скорость ×2,5 и отрабатывала по
    /// ней при нулевом метаболизме, то есть смысл механики терялся.
    ///
    /// Теперь хранится ЧИСЛО, и движок подаёт его на КАЖДОМ шаге
    /// (<see cref="CharacterDigestion.Advance"/>): состав пересчитывается под
    /// фактическое состояние, и заранее «запасённой» скорости не существует.
    /// </summary>
    public double MetabolismPercent { get; init; } =
        CharacterVitalsTuning.DefaultMetabolismPercent;

    /// <summary>Во сколько раз метаболизм меняет пропускную способность.</summary>
    public double ThroughputFactor =>
        CharacterDigestion.MetabolismThroughputFactor(MetabolismPercent);

    /// <summary>
    /// Id предмета для ПОРЦИЙ ИЗ СТАРЫХ СОХРАНЕНИЙ (форматы до v15).
    ///
    /// В тех версиях пищеварение описывалось одними суммами, а имя предмета
    /// лежало отдельно (<c>PlayerConditionState.LastConsumedItemId</c>). Читатель
    /// сохранения кладёт его сюда, и <see cref="CharacterDigestion.Normalize"/>
    /// строит из сумм одну порцию с этим именем. Иначе загрузка старого файла
    /// обнулила бы пищеварение — игрок потерял бы уже съеденное.
    /// </summary>
    public string LegacyItemId { get; init; } = string.Empty;

    /// <summary>
    /// Пищеварение пусто. Порог в тысячную долю единицы нужен из-за дробных
    /// ставок: после ровного времени переваривания остаток равен 1e-13, а не
    /// нулю, и строгое сравнение с нулём заставляло бы пищеварение «жевать»
    /// лишний шаг.
    /// </summary>
    public bool IsEmpty =>
        EnergyRemaining <= 0.001d &&
        HydrationRemaining <= 0.001d;

    /// <summary>Занятый объём в МИЛЛИЛИТРАХ — рабочая величина интерфейса.</summary>
    public double OccupiedMilliliters =>
        VolumeFraction * CharacterDigestion.CapacityMilliliters;

    /// <summary>
    /// Занятый объём в литрах. Нужен, потому что вместимость ограничена, а
    /// СКОРОСТЬ от неё не зависит: она задана пропускной способностью. Без этого
    /// поля игрок мог бы съесть четыре обеда подряд и получить четыре обеда
    /// восстановления, что физически невозможно.
    /// </summary>
    public double OccupiedLiters =>
        VolumeFraction * CharacterDigestion.StomachVolumeLiters;

    public double FreeLiters =>
        Math.Max(
            0d,
            CharacterDigestion.StomachVolumeLiters - OccupiedLiters);
}

/// <summary>
/// Пищеварение: превращение «выпил/съел» в постепенное восстановление.
///
/// МОДЕЛЬ (по документу DigestionRefactoring.md). Пищеварение — ОДНА ёмкость с
/// ОДНОЙ пропускной способностью. Каждую игровую секунду из неё уходит
/// ограниченный объём, и он делится между порциями по их «весу»:
///
///     k_i = 1 + (R − 1) · W_i                  ← «лёгкость» порции
///     rate_i = V_dry · m_i · k_i / Σ(m_j)      ← ставка порции, мл/с
///
/// где W_i — доля воды в порции, m_i — её оставшийся объём, R — во сколько раз
/// вода легче сухой пищи, V_dry — базовая пропускная способность.
///
/// Три следствия, ради которых модель и менялась:
///   • вода уходит БЫСТРЕЕ сухой пищи: W = 1 даёт k = 1,35, у сухого хлеба
///     W ≈ 0,1 даёт k ≈ 1,04;
///   • чем БОЛЬШЕ лежит в пищеварении, тем МЕДЛЕННЕЕ уходит каждая порция
///     (знаменатель Σm);
///   • смесь разнородной пищи замедляет ВСЕХ: вода, добавленная к обеду,
///     увеличивает Σm, но не увеличивает вклад еды в числитель.
///
/// Пропускная способность ВЫВОДИТСЯ из физических норм автора, а не назначена
/// «на глаз»:
///   • V_dry = вместимость / 2 часа = 2000 мл / 2 ч = 1000 мл/ч ≈ 16,67 мл/мин;
///   • V_water = 1,35 л/ч = 1350 мл/ч (физиология усвоения воды);
///   • R = V_water / V_dry = 1,35.
///
/// ПОЧЕМУ R = 1,35, А НЕ «4–5», КАК В ДОКУМЕНТЕ. Документ для справки берёт
/// «вода усваивается в 4–5 раз быстрее» и выводит из этого K = 1 + 4W. Но
/// собственные замеры автора задают ДРУГИЕ скорости: 1,35 л/ч воды против
/// «полная еда за 2 часа». Взять 4–5 значило бы назначить воде 5 литров в час,
/// то есть переписать уже согласованную физиологию. Поэтому структура формулы
/// взята из документа (вес по содержанию воды и общий лимит), а числом стало
/// отношение РЕАЛЬНЫХ норм автора. Однотипная вода по-прежнему обгоняет
/// смешанную еду в разы — за счёт того, что при смеси она забирает большую долю
/// общей пропускной способности.
///
/// ПОЧЕМУ ОБЪЁМ ВЫРОС С 1 ЛИТРА ДО 2. Задано автором: «Доступный объём
/// увеличиваем с 1000 до 2000». Вместимость ограничивает, сколько еды можно
/// «загрузить» заранее, и теперь она же задаёт базовую пропускную способность
/// (полный объём за <see cref="FoodStomachEmptyHours"/> часов).
///
/// Метаболизм меняет ДВЕ вещи:
///   • пропускную способность — повышенный пропускает в 2,5 раза больше за то
///     же время, пониженный — в 1,5 раза меньше;
///   • объём восстановления — на +/−0,5% шкалы (задано автором), что записано
///     в <see cref="StomachPortion.YieldFactor"/> в момент приёма. Проценты
///     объёма считаются от максимума шкалы (как все пороги движка), а не от
///     порции: иначе бонус был бы неразличим, потому что одна порция — это
///     единицы процентов шкалы.
///
/// ПРО ВРЕМЯ ПЕРЕВАРИВАНИЯ. Прежняя модель обещала «полный желудок за 2 часа»
/// для КАЖДОЙ порции отдельно. Теперь это не так: порция в одиночку уходит
/// ровно за столько (мера автора сохранена как эталон), но в смеси — дольше, и
/// именно это задано документом («чем больше намешано, тем дольше каждая
/// усваивается»). Общая пропускная способность при этом не превышается никогда:
/// за час пищеварение не пропустит больше, чем V_dry · (средняя лёгкость).
/// </summary>
public static class CharacterDigestion
{
    /// <summary>
    /// НИЖНЯЯ ГРАНИЦА ОБЪЁМА ПОРЦИИ как ДОЛЯ вместимости: столько занимает даже
    /// самая мелкая позиция (пакет сахара 5 г, таблетка).
    ///
    /// Зачем граница. Без неё мелкая позиция занимала бы НОЛЬ и усваивалась
    /// мгновенно: место не тратится, таймер пуст, а признак «влезет» становится
    /// бессмысленным. Прежде это число стояло литералом (0,01) в нескольких
    /// местах, и проверки повторяли литерал за движком; теперь минимум назван
    /// один раз — по нему сверяются и приём, и признак «влезет», и тесты.
    /// </summary>
    public const double MinimumPortionFraction = 0.01d;

    /// <summary>
    /// Сколько КИЛОКАЛОРИЙ вмещает полная шкала энергии (задано автором).
    ///
    /// Единицы хранения (0..10000) остались прежними, но теперь у них есть
    /// физический смысл: 10000 единиц — это 5000 ккал.
    /// </summary>
    public const double EnergyScaleKilocalories =
        CharacterVitalsTuning.EnergyScaleKilocalories;

    /// <summary>
    /// Сколько МИЛЛИЛИТРОВ вмещает полная шкала жидкости (задано автором: 3000).
    ///
    /// Было 15 000 — число делало расход жидкости почти незаметным: при 167 мл/ч
    /// шкала опустошалась за 90 игровых часов, и «выпил воды, а динамика всё равно
    /// падающая» лечилось не балансом, а увеличением запаса. Автор задал реальные
    /// размеры: 3000 мл на шкалу и 150 мл за 30 игровых минут под нагрузкой, то
    /// есть 300 мл/ч — шкала опустошается за 10 игровых часов нормальной жизни.
    ///
    /// Число связано с расходом и пищеварением: усвоение жидкости
    /// (<see cref="HydrationAbsorptionLitersPerHour"/>) считается ОТ ЭТОЙ ШКАЛЫ, и
    /// при 3000 мл литр в час даёт 45% шкалы в час (4500 единиц), то есть вода
    /// по-прежнему обгоняет расход (300 мл/ч под нагрузкой) в разы.
    /// </summary>
    public const double HydrationScaleMilliliters =
        CharacterVitalsTuning.HydrationScaleMilliliters;

    /// <summary>
    /// Вместимость ПИЩЕВАРЕНИЯ в литрах (2 — задано автором: «увеличиваем с 1000
    /// до 2000»).
    ///
    /// Это же число задаёт базовую пропускную способность: полный объём уходит
    /// за <see cref="FoodStomachEmptyHours"/> часов.
    /// </summary>
    public const double StomachVolumeLiters =
        CharacterVitalsTuning.StomachVolumeLiters;

    /// <summary>Вместимость пищеварения в МИЛЛИЛИТРАХ (2000).</summary>
    public const double CapacityMilliliters =
        StomachVolumeLiters * 1000d;

    /// <summary>
    /// Базовая пропускная способность СУХОЙ пищи, миллилитров в час. Выведена из
    /// вместимости и нормы «полный объём за 2 часа»: 2000 / 2 = 1000 мл/ч.
    /// </summary>
    public const double DryThroughputLitersPerHour =
        CharacterVitalsTuning.DryThroughputLitersPerHour;

    /// <summary>
    /// Пропускная способность СУХОЙ пищи в миллилитрах за ИГРОВУЮ СЕКУНДУ —
    /// рабочая величина формулы ставки.
    /// </summary>
    public const double DryThroughputMillilitersPerSecond =
        DryThroughputLitersPerHour * 1000d / 3600d;

    /// <summary>
    /// Жидкость усваивается со скоростью 1,35 литра в час.
    ///
    /// Величина задана ФИЗИОЛОГИЧЕСКИ (литрами), а не долей шкалы: литр воды
    /// остаётся литром независимо от того, сколько миллилитров вмещает шкала.
    /// Отсюда и «побочный эффект» уменьшения шкалы до 3000 мл: в ПРОЦЕНТАХ шкалы
    /// то же усвоение стало в 5 раз быстрее (45% в час вместо 9%), потому что
    /// делится на меньший объём. Пересчёт единиц живёт в
    /// <see cref="HydrationUnits"/>, а «литр за час» в подсказке читается из
    /// <see cref="HoursPerLitreOfWater"/>.
    ///
    /// Час ИГРОВОЙ: игровое время идёт 1:1 с реальным, но при ускорении FF
    /// пищеварение обязано переваривать быстрее вместе с миром.
    /// </summary>
    public const double HydrationAbsorptionLitersPerHour =
        CharacterVitalsTuning.HydrationAbsorptionLitersPerHour;

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
    /// Полный объём пищеварения уходит за 2 игровых часа (задано автором).
    /// Отсюда выведена <see cref="DryThroughputLitersPerHour"/>.
    /// </summary>
    public const double FoodStomachEmptyHours =
        CharacterVitalsTuning.FoodStomachEmptyHours;

    /// <summary>
    /// ВО СКОЛЬКО РАЗ ВОДЯНИСТОСТЬ ПОВЫШАЕТ СКОРОСТЬ ПОРЦИИ (R в формуле).
    /// Выведено как отношение двух физиологических норм — 1,35 л/ч воды против
    /// 1000 мл/ч сухой пищи, — а не назначено круглым числом: если автор
    /// поправит одну из норм, «вода легче еды» поедет за ней само.
    ///
    /// Порция чистой воды получает k = 1,35, порция сухого хлеба (W ≈ 0,1) —
    /// k ≈ 1,035, шашлык (W = 0,4) — k ≈ 1,14. Порядок «вода → сок → мясо →
    /// хлеб» совпадает с документом; сами числа — из норм автора.
    /// </summary>
    public const double WaterContentSpeedFactor =
        HydrationAbsorptionLitersPerHour / DryThroughputLitersPerHour;

    /// <summary>Эталонные объёмы порций, к которым привязаны каталоги предметов.</summary>
    public const double ReferenceFoodGrams =
        CharacterVitalsTuning.ReferenceFoodGrams;

    public const double ReferenceDrinkMilliliters =
        CharacterVitalsTuning.ReferenceDrinkMilliliters;

    /// <summary>Ускорение при повышенном метаболизме (в 2,5 раза).</summary>
    public const double ElevatedMetabolismSpeedFactor =
        CharacterVitalsTuning.ElevatedMetabolismSpeedFactor;

    public const double ReducedMetabolismSpeedFactor =
        CharacterVitalsTuning.ReducedMetabolismSpeedFactor;

    /// <summary>Бонус/штраф объёма восстановления при отклонении метаболизма.</summary>
    public const double ElevatedMetabolismVolumeBonus =
        CharacterVitalsTuning.ElevatedMetabolismVolumeBonus;

    public const double ReducedMetabolismVolumePenalty =
        CharacterVitalsTuning.ReducedMetabolismVolumePenalty;

    private const double SecondsPerGameHour = 3600d;

    /// <summary>
    /// Объём порции как доля ВМЕСТИМОСТИ (0..1).
    ///
    /// Это и есть место, где живёт правило «пищеварение 2 литра»: 500 г еды
    /// занимают четверть, 500 мл напитка — столько же (миллилитр и грамм здесь
    /// приравнены: и то и другое — объём порции).
    /// </summary>
    public static double PortionFraction(double gramsOrMilliliters)
    {
        var grams = gramsOrMilliliters;
        if (!double.IsFinite(grams) || grams <= 0d)
            return 0d;

        return Math.Clamp(
            grams / CapacityMilliliters,
            0d,
            1d);
    }

    /// <summary>
    /// Начинает (или дополняет) переваривание порции.
    ///
    /// <paramref name="itemId"/> — ЧТО съедено: по нему интерфейс рисует иконку
    /// в пищеварении и подбирает название.
    ///
    /// <paramref name="portionMilliliters"/> — ОБЪЁМ порции (он же место в
    /// пищеварении), <paramref name="waterMilliliters"/> — вода в ней,
    /// <paramref name="kilocalories"/> — её энергия. Все три берутся из
    /// <see cref="CharacterConsumableCatalog"/>: это ФИЗИЧЕСКИЕ свойства предмета,
    /// и второй источник таких чисел разошёлся бы с каталогом.
    ///
    /// Модель очереди с ограничением вместимости: если игрок ест, пока предыдущая
    /// порция ещё переваривается, новая встаёт РЯДОМ, но ПИЩЕВАРЕНИЕ БОЛЬШЕ
    /// ВМЕСТИМОСТИ НЕ ПРИНИМАЕТ. Порция, которая целиком не влезает, НЕ
    /// СЪЕДАЕТСЯ вовсе. Ставки при этом НЕ «свои у каждой порции»: они
    /// ПЕРЕСЧИТЫВАЮТСЯ для всего содержимого (<see cref="Distribute"/>), потому
    /// что усвоение задано ОБЩЕЙ пропускной способностью, а не таймером порции.
    ///
    /// ПОЧЕМУ БОЛЬШЕ НЕ «ПРИНИМАЕМ ЧАСТЬ». Прежняя модель принимала порцию лишь в
    /// свободной части (`accepted = min(порция, свободно)`), и в порцию
    /// записывалась именно эта урезанная доля. Снаружи это выглядело как обман:
    /// автор выбирал банан «110 мл», а в пищеварении появлялась порция на 20 мл,
    /// потому что 90 мл были уже заняты. Числа в интерфейсе и содержимое
    /// пищеварения обязаны совпадать, поэтому действует правило «либо целиком,
    /// либо никак»: оно же ровно то, что показывает интерфейс — серые и
    /// неактивные пункты меню и «Использовать (пищеварение занято)» в инвентаре
    /// (<see cref="CharacterDigestionReport.Fits"/>).
    /// </summary>
    public static StomachContents Begin(
        StomachContents current,
        string itemId,
        double portionMilliliters,
        double waterMilliliters,
        double kilocalories,
        double metabolismPercent)
    {
        var contents = (current ?? StomachContents.Empty).Normalize();

        var mass = Math.Max(
            MinimumPortionFraction * CapacityMilliliters,
            double.IsFinite(portionMilliliters) ? portionMilliliters : 0d);
        mass = Math.Min(mass, CapacityMilliliters);

        // ВОДА И ОБЪЁМ — РАЗНЫЕ ВЕЛИЧИНЫ, и связывать их нельзя. Объём — это
        // МЕСТО в пищеварении (и время переваривания), вода — ПИТАТЕЛЬНОСТЬ по
        // шкале жидкости. Калибровка кафе задаёт их независимо: «Тархун»
        // утоляет 90% жажды (2700 мл) со стаканом 500 мл. Зажми вода по объёму,
        // и напиток давал бы впятеро меньше, чем задал автор.
        var water = Math.Max(
            0d,
            double.IsFinite(waterMilliliters) ? waterMilliliters : 0d);
        var kcal = Math.Max(
            0d,
            double.IsFinite(kilocalories) ? kilocalories : 0d);

        // Порция без питательности (таблетка, сигарета) пищеварение НЕ занимает:
        // иначе она «повисала» бы в нём навсегда с нулевой ставкой, и монитор
        // показывал бы вечную иконку.
        if (kcal <= 0d && water <= 0d)
            return contents;

        // Вместимость не резиновая: порция либо влезает целиком, либо не съедена.
        var free = Math.Max(0d, CapacityMilliliters - contents.OccupiedMilliliters);
        if (mass > free + 1e-9d)
            return contents;

        var portions = new List<StomachPortion>(contents.Portions.Count + 1);
        portions.AddRange(contents.Portions);
        portions.Add(new StomachPortion(
            ItemId: string.IsNullOrWhiteSpace(itemId) ? "unknown" : itemId,
            MassTotal: mass,
            MassRemaining: mass,
            KilocaloriesTotal: kcal,
            WaterMillilitersTotal: water,
            // Текущий метаболизм подставляется БЕЗ фиксации: поле переписывается
            // при каждом пересчёте состава (см. Materialize).
            YieldBonusFraction: MetabolismYieldBonus(metabolismPercent),
            PerGameSecond: 0d));

        return Materialize(portions, metabolismPercent);
    }

    /// <summary>
    /// Продвигает пищеварение на <paramref name="gameSeconds"/> игровых секунд.
    ///
    /// Возвращает новое содержимое и прибавку к шкалам в ЕДИНИЦАХ. Остаток
    /// никогда не «переполняется»: отдаётся ровно то, что успело усвоиться, и
    /// пищеварение пустеет ровно тогда, когда время вышло.
    ///
    /// Шаг произвольный: ставка на шаге ПОСТОЯННА (она вычислена по составу на
    /// начало шага), поэтому результат линеен по времени и не зависит от того,
    /// нарезан шаг на минуты или подан одним куском. Это важно для ускорения
    /// времени: при FF движок подаёт больше игровых секунд за тик.
    /// </summary>
    public static DigestionStep Advance(
        StomachContents current,
        double gameSeconds,
        double metabolismPercent)
    {
        var contents = (current ?? StomachContents.Empty).Normalize();
        var seconds = Math.Max(
            0d,
            double.IsFinite(gameSeconds) ? gameSeconds : 0d);

        if (contents.IsEmpty || seconds <= 0d)
            return new DigestionStep(contents, 0d, 0d);

        var energyGain = 0d;
        var hydrationGain = 0d;
        var portions = new List<StomachPortion>(contents.Portions.Count);

        foreach (var portion in contents.Portions)
        {
            var digested = Math.Min(
                Math.Max(0d, portion.MassRemaining),
                Math.Max(0d, portion.PerGameSecond) * seconds);

            // Прибавка считается как РАЗНОСТЬ остатков до и после усвоения, а не
            // вторым расчётом той же пропорции: два расчёта одного и того же
            // разошлись бы при первой правке формулы, и за всё переваривание
            // порция отдала бы не ровно то, что в ней есть. Через разность
            // «сумма прибавок = объём порции» выполняется ПО ПОСТРОЕНИЮ.
            var remaining = portion with
            {
                MassRemaining = Math.Max(0d, portion.MassRemaining - digested)
            };

            energyGain +=
                portion.EnergyRemainingUnits - remaining.EnergyRemainingUnits;
            hydrationGain +=
                portion.HydrationRemainingUnits - remaining.HydrationRemainingUnits;

            // Полностью усвоенная порция ИСЧЕЗАЕТ из пищеварения — вместе со
            // своим объёмом. Ноль остатка означает «еды больше нет», и держать её
            // в списке значило бы рисовать в мониторе пустую иконку.
            if (remaining.IsEmpty)
                continue;

            portions.Add(remaining);
        }

        return new DigestionStep(
            Materialize(portions, metabolismPercent),
            energyGain,
            hydrationGain);
    }

    /// <summary>
    /// Собирает содержимое из списка порций: ПЕРЕСЧИТЫВАЕТ ставки под текущий
    /// состав и ВЫВОДИТ суммы.
    ///
    /// Единственное место, где пищеварение «собирается», поэтому правило
    /// «суммы равны сумме порций, а ставки отвечают составу» соблюдается и после
    /// приёма, и после шага, и после загрузки сохранения.
    /// </summary>
    private static StomachContents Materialize(
        IReadOnlyList<StomachPortion> portions,
        double metabolismPercent,
        bool applyMetabolismYield = true)
    {
        var distributed = Distribute(
            portions,
            metabolismPercent,
            applyMetabolismYield);

        var energy = 0d;
        var hydration = 0d;
        var energyPerSecond = 0d;
        var hydrationPerSecond = 0d;
        var mass = 0d;

        foreach (var portion in distributed)
        {
            energy += portion.EnergyRemainingUnits;
            hydration += portion.HydrationRemainingUnits;
            energyPerSecond += portion.EnergyPerGameSecond;
            hydrationPerSecond += portion.HydrationPerGameSecond;
            mass += Math.Max(0d, portion.MassRemaining);
        }

        return new StomachContents(
            energy,
            hydration,
            energyPerSecond,
            hydrationPerSecond,
            Math.Clamp(mass / CapacityMilliliters, 0d, 1d))
        {
            Portions = distributed,
            MetabolismPercent = metabolismPercent
        };
    }

    /// <summary>
    /// РАСПРЕДЕЛЯЕТ общую пропускную способность между порциями — ядро модели.
    ///
    /// Единственная формула, из которой следует ВСЁ поведение пищеварения:
    ///
    ///     rate_i = V_dry · T · m_i · k_i / Σ(m_j),   k_i = 1 + (R − 1)·W_i
    ///
    /// где T — множитель метаболизма. Пять свойств видны прямо из неё, и каждое —
    /// требование автора:
    ///   1. ВОДА УХОДИТ БЫСТРЕЕ ЕДЫ. k воды (W = 1) равно R = 1,35, у сухого
    ///      хлеба k ≈ 1,04.
    ///   2. ЧЕМ БОЛЬШЕ ЛЕЖИТ, ТЕМ МЕДЛЕННЕЕ УХОДИТ КАЖДАЯ ПОРЦИЯ: Σm растёт, и
    ///      доля каждой порции падает. «Скорость не зависит от размера порции»
    ///      из прежней модели больше не верно — и это то, что просил автор.
    ///   3. СМЕСЬ ЗАМЕДЛЯЕТ ВСЕХ. Вода, добавленная к обеду, увеличивает Σm, но
    ///      не увеличивает числитель еды: обед начинает усваиваться медленнее.
    ///      Ровно это в документе названо «наличие слишком разного количества
    ///      веществ требует более сложной комбинации ферментов».
    ///   4. ОДНОТИПНАЯ ПИЩА УСВАИВАЕТСЯ ЛУЧШЕ: у единственной порции k_i/Σm = k/m,
    ///      то есть ставка пропорциональна её собственной лёгкости и не зависит от
    ///      чужих.
    ///   5. СМЕСЬ ПРОПУСКАЕТ БОЛЬШЕ: множитель Σ(mk)/Σm — средняя «лёгкость»
    ///      содержимого, и при большой доле воды он выше (вода «смазывает»
    ///      пищеварение). Это и есть та самая имитация, ради которой модель
    ///      задумана: однотипная пища без примесей уходит предсказуемо, а смесь
    ///      растягивает срок КАЖДОЙ порции.
    /// </summary>
    private static IReadOnlyList<StomachPortion> Distribute(
        IReadOnlyList<StomachPortion> portions,
        double metabolismPercent,
        bool applyMetabolismYield = true)
    {
        if (portions.Count == 0)
            return Array.Empty<StomachPortion>();

        var massTotal = 0d;
        foreach (var portion in portions)
            massTotal += Math.Max(0d, portion.MassRemaining);

        var result = new List<StomachPortion>(portions.Count);

        if (massTotal <= 0.001d)
        {
            foreach (var portion in portions)
                result.Add(portion with { PerGameSecond = 0d });

            return result;
        }

        var throughput =
            DryThroughputMillilitersPerSecond *
            MetabolismThroughputFactor(metabolismPercent);

        // Надбавка объёма ПЕРЕСЧИТЫВАЕТСЯ здесь, а не берётся из порции: именно
        // этот вызов обслуживает каждый шаг пищеварения, поэтому текущий
        // метаболизм правит и объёмом, и скоростью — без «запасённой» величины.
        //
        // Исключение — порция из СТАРОГО формата: там суммы шкал уже были
        // посчитаны вместе с бонусом, и вторая надбавка удвоила бы эффект.
        var yieldBonus = applyMetabolismYield
            ? MetabolismYieldBonus(metabolismPercent)
            : 0d;

        foreach (var portion in portions)
        {
            var mass = Math.Max(0d, portion.MassRemaining);
            var lightness = 1d +
                (WaterContentSpeedFactor - 1d) * portion.WaterContent;
            var rate = mass <= 0d
                ? 0d
                : throughput * mass * lightness / massTotal;

            result.Add(portion with
            {
                PerGameSecond = rate,
                YieldBonusFraction = yieldBonus
            });
        }

        return result;
    }

    /// <summary>
    /// Приводит содержимое к согласованному виду: чистит порции и ПЕРЕСЧИТЫВАЕТ
    /// суммы и ставки.
    ///
    /// Вызывается и движком (перед каждым шагом), и чтением сохранения, и
    /// <see cref="PlayerConditionState.Normalize"/>. Поэтому после загрузки
    /// ставки ОБЯЗАТЕЛЬНО отвечают составу: сохранение хранит только физические
    /// величины порций, а не скорости — иначе загруженный файл «запомнил» бы
    /// скорости на момент записи, и пищеварение вело бы себя иначе, чем в
    /// непрерывной игре.
    /// </summary>
    public static StomachContents Normalize(this StomachContents contents)
    {
        contents ??= StomachContents.Empty;

        var raw = contents.Portions ?? Array.Empty<StomachPortion>();
        var portions = new List<StomachPortion>(raw.Count);

        foreach (var portion in raw)
        {
            if (portion is null)
                continue;

            var massTotal = NormalizeNonNegative(portion.MassTotal);
            var massRemaining = Math.Min(
                NormalizeNonNegative(portion.MassRemaining),
                massTotal);

            if (massTotal <= 0d || massRemaining <= 0d)
                continue;

            portions.Add(portion with
            {
                ItemId = string.IsNullOrWhiteSpace(portion.ItemId)
                    ? "unknown"
                    : portion.ItemId,
                MassTotal = massTotal,
                MassRemaining = massRemaining,
                KilocaloriesTotal = NormalizeNonNegative(portion.KilocaloriesTotal),
                // Вода НЕ зажимается объёмом: это питательность по шкале, а не
                // место в пищеварении (см. CharacterDigestion.Begin).
                WaterMillilitersTotal = NormalizeNonNegative(
                    portion.WaterMillilitersTotal),
                YieldBonusFraction = NormalizeYield(portion.YieldBonusFraction),
                PerGameSecond = 0d
            });
        }
        // Фолбэк для СТАРЫХ сохранений (v13/v14): там порций ещё не было, и
        // пищеварение описывалось одними суммами. Превращаем их в одну порцию под
        // Id последнего предмета — иначе загрузка обнулила бы пищеварение, то есть
        // игрок потерял бы уже съеденное.
        var legacy = portions.Count == 0;
        if (legacy)
        {
            var legacyPortion = LegacyPortion(contents);
            if (legacyPortion is not null)
                portions.Add(legacyPortion);
        }

        return Materialize(
            portions,
            contents.MetabolismPercent,
            applyMetabolismYield: !legacy);
    }

    /// <summary>
    /// Порция из СУММ старого формата (v13/v14) — там отдельных порций не было.
    /// Объём берётся из <see cref="StomachContents.VolumeFraction"/>, энергия и
    /// вода — из остатков шкал через обратный пересчёт единиц в ккал и мл.
    /// </summary>
    private static StomachPortion? LegacyPortion(StomachContents contents)
    {
        var energy = NormalizeNonNegative(contents.EnergyRemaining);
        var hydration = NormalizeNonNegative(contents.HydrationRemaining);
        if (energy <= 0d && hydration <= 0d)
            return null;

        var mass = Math.Clamp(
            NormalizeNonNegative(contents.VolumeFraction),
            0d,
            1d) * CapacityMilliliters;

        if (mass <= 0d)
            mass = MinimumPortionFraction * CapacityMilliliters;

        return new StomachPortion(
            string.IsNullOrWhiteSpace(contents.LegacyItemId)
                ? "unknown"
                : contents.LegacyItemId,
            MassTotal: mass,
            MassRemaining: mass,
            KilocaloriesTotal: KilocaloriesFromUnits(energy),
            WaterMillilitersTotal: MillilitersFromUnits(hydration),
            // У порции из старого формата бонуса метаболизма нет: там суммы шкал
            // УЖЕ были посчитаны с его учётом, и вторая поправка удвоила бы эффект
            // (за это отвечает <c>applyMetabolismYield</c> в Materialize).
            YieldBonusFraction: 0d,
            PerGameSecond: 0d);
    }

    /// <summary>Единицы шкалы энергии → килокалории.</summary>
    public static double KilocaloriesFromUnits(double units) =>
        units / PlayerConditionScale.Maximum * EnergyScaleKilocalories;

    /// <summary>Единицы шкалы жидкости → миллилитры.</summary>
    public static double MillilitersFromUnits(double units) =>
        units / PlayerConditionScale.Maximum * HydrationScaleMilliliters;

    /// <summary>Килокалории → единицы шкалы энергии.</summary>
    public static double EnergyUnits(double kilocalories) =>
        kilocalories / EnergyScaleKilocalories * PlayerConditionScale.Maximum;

    /// <summary>Миллилитры → единицы шкалы жидкости.</summary>
    public static double HydrationUnits(double milliliters) =>
        milliliters / HydrationScaleMilliliters * PlayerConditionScale.Maximum;

    /// <summary>
    /// ВО СКОЛЬКО РАЗ метаболизм меняет пропускную способность. Повышенный —
    /// в 2,5 раза быстрее, пониженный — в 1,5 раза медленнее.
    ///
    /// Берёт ПРОЦЕНТ, а не флаги: ставки пересчитываются на каждом шаге под
    /// фактическое состояние (см. <see cref="StomachContents.MetabolismPercent"/>).
    /// </summary>
    public static double MetabolismThroughputFactor(double metabolismPercent) =>
        metabolismPercent >= CharacterVitalsTuning.ElevatedMetabolismPercent
            ? 1d / ElevatedMetabolismSpeedFactor
            : metabolismPercent < CharacterVitalsTuning.ReducedMetabolismPercent
                ? 1d / ReducedMetabolismSpeedFactor
                : 1d;

    /// <summary>
    /// ВО СКОЛЬКО РАЗ метаболизм меняет ОБЪЁМ восстановления из той же еды,
    /// как ДОЛЯ МАКСИМУМА ШКАЛЫ. Повышенный даёт +0,5% шкалы, пониженный
    /// отнимает столько же.
    ///
    /// Величина ОСТАЁТСЯ пороговой (а не растёт с процентом, как надбавка к
    /// здоровью): задана автором именно как «+0,5% объёма при повышенном».
    /// </summary>
    private static double MetabolismYieldBonus(double metabolismPercent) =>
        metabolismPercent >= CharacterVitalsTuning.ElevatedMetabolismPercent
            ? ElevatedMetabolismVolumeBonus
            : metabolismPercent < CharacterVitalsTuning.ReducedMetabolismPercent
                ? -ReducedMetabolismVolumePenalty
                : 0d;

    /// <summary>
    /// Нормализует БОНУС/ШТРАФ метаболизма, хранимый на порции, как ДОЛЮ ШКАЛЫ.
    ///
    /// Диапазон зажат осмысленными границами (±5% шкалы): значение приходит из
    /// сохранения, и порченый файл не должен позволить одной порции отдать
    /// половину шкалы. Ноль — это «без бонуса», поэтому прежняя проверка «годен
    /// только положительный» здесь была бы неверна: она обнуляла бы ШТРАФ
    /// пониженного метаболизма.
    /// </summary>
    private static double NormalizeYield(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, -0.05d, 0.05d) : 0d;

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) && value > 0.001d ? value : 0d;
}

/// <summary>
/// Порция в желудке «глазами интерфейса»: объём, физические вещества и остаток
/// времени.
///
/// Зачем отдельный тип, а не сам <see cref="StomachPortion"/>. В порции числа
/// живут в ЕДИНИЦАХ ШКАЛЫ (0..10000) и в игровых секундах — так их считает
/// движок. Интерфейсу нужны литры, килокалории и миллилитры: тултип обязан
/// показать то, что игрок узнаёт с этикетки, а не «3400 единиц». Пересчёт живёт
/// здесь, в домене, потому что второй такой же в JavaScript разошёлся бы с
/// движком при смене размера шкалы.
/// </summary>
public sealed record StomachPortionView(
    string ItemId,
    double VolumeFraction,
    double VolumeLiters,
    // Объём порции в МИЛЛИЛИТРАХ. Единственная величина объёма, которой
    // пользуется интерфейс: миллилитр и грамм здесь приравнены (и то и другое —
    // объём порции), а литры давали ровно те же числа, только с плавающей
    // запятой в подписи. Именно миллилитры показывает список содержимого, и
    // именно с ними сверяется этикетка предмета.
    double VolumeMilliliters,
    double EnergyKilocalories,
    double WaterMilliliters,
    double RemainingGameSeconds,
    // Доля воды в порции (0..1). Нужна подписи «вода усваивается в X раза
    // быстрее»: без неё игрок не понимает, почему одинаковые по объёму порции
    // перевариваются за разное время.
    double WaterContent,
    // Скорость по КАЖДОЙ шкале — СВОЯ у порции. Без неё монитор не может
    // показать «сколько несёт эта еда»: суммарная скорость желудка описывает
    // все порции сразу, и одна строка сказала бы «+N ккал/мин» про каждый
    // предмет без разбора, хотя апельсин даёт энергию, а вода — нет.
    //
    // В единицах шкалы за ИГРОВУЮ СЕКУНДУ, как хранится в домене; перевод в
    // «ккал/мин» и «мл/мин» делает страница той же формулой, что и для итога,
    // чтобы строка предмета и строка итога были в одной размерности.
    double EnergyPerGameSecond,
    double HydrationPerGameSecond)
{
    /// <summary>Сколько осталось перевариваться, в ИГРОВЫХ МИНУТАХ.</summary>
    public double RemainingGameMinutes => RemainingGameSeconds / 60d;
}

/// <summary>
/// Один фактор, влияющий на усвоение: текст для игрока и НАПРАВЛЕНИЕ.
///
/// Направление отдельным полем, а не «догадка по знаку в тексте»: цвет
/// подсказки обязан следовать смыслу («повышенный метаболизм ускоряет» —
/// полезно), а не разбору строки. Разбор строки сломался бы от первой правки
/// формулировки.
/// </summary>
public sealed record DigestionFactor(
    string Text,
    bool Useful);

/// <summary>
/// Пищеварение для интерфейса: порции, занятый объём и скорость усвоения —
/// ОДНИМ вызовом, посчитанным по тем же правилам, что и начисление.
///
/// Зачем это в домене. Монитор обязан показать «через сколько порция исчезнет»
/// и «что именно усвоится». Оба числа — ПРОИЗВОДНЫЕ от ставок и остатков,
/// которые живут в домене. Если бы их считал JavaScript, то при любой правке
/// физиологии (например «полный желудок за 2 часа») подсказка разошлась бы с
/// начислением, и таймер в желудке врал бы — ровно тот класс дефектов, который
/// автор уже ловил на «потолке 10000».
/// </summary>
public static class CharacterDigestionReport
{
    /// <summary>
    /// Вместимость пищеварения в МИЛЛИЛИТРАХ (2000).
    ///
    /// Миллилитры — рабочая величина интерфейса: и подпись «занято / всего», и
    /// вёрстка области пищеварения считаются в них, а не в долях и не в литрах.
    /// </summary>
    public static double StomachCapacityMilliliters =>
        CharacterDigestion.CapacityMilliliters;

    /// <summary>
    /// Поместится ли ОДИН предмет в свободную часть пищеварения.
    ///
    /// Зачем ответ в домене, а не в интерфейсе. Правило одно на всех: «объём
    /// порции больше свободного места — предмет употребить нельзя». По нему
    /// серятся пункты меню пищеварения, по нему же гаснет «Использовать» в
    /// инвентаре, и по нему же движок решает, принять ли порцию. Если бы
    /// каждая страница считала сама, правило разошлось бы с движком при первой
    /// правке, и «нельзя» в интерфейсе означало бы не то, что делает начисление.
    ///
    /// Сравнение ДОСЛОВНО повторяет <see cref="CharacterDigestion.Begin"/>,
    /// включая допуск 1e-9: порция принимается целиком либо не принимается
    /// вовсе, и признак «годится» обязан совпадать с приёмом на самой границе.
    /// Иначе пункт меню был бы живым при «не влезает ровно», а пищеварение
    /// порцию молча не принимало.
    /// </summary>
    public static bool Fits(
        StomachContents contents,
        string itemId)
    {
        var normalized = (contents ?? StomachContents.Empty).Normalize();
        var required = Math.Max(
            CharacterDigestion.MinimumPortionFraction *
            CharacterDigestion.CapacityMilliliters,
            CharacterConsumableCatalog.GetPortionMilliliters(itemId));
        var free = Math.Max(
            0d,
            CharacterDigestion.CapacityMilliliters - normalized.OccupiedMilliliters);

        return required <= free + 1e-9d;
    }

    /// <summary>Порции пищеварения в физических величинах, в порядке употребления.</summary>
    public static IReadOnlyList<StomachPortionView> Portions(
        StomachContents contents)
    {
        var normalized = (contents ?? StomachContents.Empty).Normalize();
        var result = new List<StomachPortionView>(
            normalized.Portions.Count);

        if (normalized.Portions.Count == 0)
            return result;

        foreach (var portion in normalized.Portions)
        {
            result.Add(new StomachPortionView(
                portion.ItemId,
                // Объём как доля вместимости: по ней вёрстка отмеряет место.
                portion.VolumeFraction,
                portion.VolumeLiters,
                portion.VolumeMilliliters,
                // Килокалории и миллилитры — то, что игрок узнаёт с этикетки.
                portion.KilocaloriesRemaining,
                portion.WaterMillilitersRemaining,
                portion.RemainingGameSeconds,
                portion.WaterContent,
                // Ставки — в ЕДИНИЦАХ ШКАЛ, потому что строка шкалы показывает
                // свой вклад той же величиной, что и общий итог. Берутся они у
                // САМОЙ порции, а не считаются здесь заново: иначе строка
                // предмета и строка итога могли бы разойтись.
                portion.EnergyPerGameSecond,
                portion.HydrationPerGameSecond));
        }

        return result;
    }

    /// <summary>
    /// Что влияет на усвоение ПРЯМО СЕЙЧАС, по-русски и с направлением.
    ///
    /// Список возвращает домен, а не интерфейс: правило «повышенный метаболизм
    /// ускоряет в 2,5 раза» задано здесь, и вторая его версия в JavaScript
    /// неизбежно разошлась бы с этой.
    ///
    /// Берётся ТЕКУЩИЙ метаболизм: он же правит ставками на каждом шаге, поэтому
    /// подсказка и начисление не могут разойтись даже через секунду после еды.
    /// </summary>
    public static IReadOnlyList<DigestionFactor> MetabolismFactors(
        double metabolismPercent)
    {
        var result = new List<DigestionFactor>();

        // ПРОПУСКНАЯ СПОСОБНОСТЬ — первым фактором, потому что теперь она
        // определяет ВРЕМЯ, а не отдельный таймер порции: игрок обязан видеть, с
        // какой скоростью пищеварение пропускает содержимое, иначе «почему эта
        // еда идёт дольше прежнего» остаётся без объяснения.
        result.Add(new DigestionFactor(
            "Пропускная способность: " +
            (CharacterDigestion.DryThroughputLitersPerHour * 1000d)
                .ToString("0.##") +
            " мл/ч сухого, " +
            (CharacterDigestion.HydrationAbsorptionLitersPerHour * 1000d)
                .ToString("0.##") +
            " мл/ч воды",
            Useful: true));

        if (metabolismPercent >=
            CharacterVitalsTuning.ElevatedMetabolismPercent)
        {
            result.Add(new DigestionFactor(
                "Метаболизм " +
                metabolismPercent.ToString("0.#") +
                "%: усвоение ×" +
                (1d / CharacterDigestion.ElevatedMetabolismSpeedFactor).ToString("0.##") +
                " и +" +
                (CharacterDigestion.ElevatedMetabolismVolumeBonus * 100d).ToString("0.#") +
                "% объёма",
                // Быстрее усваивается и больше восстанавливается — игроку выгодно.
                Useful: true));
        }
        else if (metabolismPercent <
            CharacterVitalsTuning.ReducedMetabolismPercent)
        {
            result.Add(new DigestionFactor(
                "Метаболизм " +
                metabolismPercent.ToString("0.#") +
                "%: усвоение ×" +
                (1d / CharacterDigestion.ReducedMetabolismSpeedFactor).ToString("0.##") +
                " и −" +
                (CharacterDigestion.ReducedMetabolismVolumePenalty * 100d).ToString("0.#") +
                "% объёма",
                // Медленнее и меньше — игрок теряет часть порции.
                Useful: false));
        }

        return result;
    }
}

/// <summary>Результат шага пищеварения.</summary>
public sealed record DigestionStep(
    StomachContents Contents,
    double EnergyGain,
    double HydrationGain)
{
    public bool IsEmpty => Contents.IsEmpty;
}
