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
/// <summary>
/// ОДНА порция в желудке: что именно съедено, сколько ещё не усвоено и с какой
/// скоростью усваивается.
///
/// Зачем по отдельности, а не одной суммой. Суммы достаточно, чтобы начислять
/// шкалы, но по ней нельзя ответить на вопросы, которые задаёт интерфейс: ЧТО
/// лежит в желудке, сколько места занимает каждая порция и сколько ей осталось.
/// Прежняя модель складывала всё в четыре числа и помнила лишь Id ПОСЛЕДНЕГО
/// предмета — поэтому монитор показывал «Апельсин» вместо воды сразу после воды.
///
/// Ставки у каждой порции СВОИ и не смешиваются: еда отдаёт энергию со скоростью
/// «полный желудок за 2 часа», питьё — жидкость со скоростью 1,35 л/ч. В прежней
/// модели ставки лежали рядом с СУММОЙ, поэтому питьё после еды подменяло ставку
/// еды (и наоборот), хотя остатки были разными.
/// </summary>
public sealed record StomachPortion(
    string ItemId,
    double VolumeFraction,
    double EnergyRemaining,
    double HydrationRemaining,
    double EnergyPerGameSecond,
    double HydrationPerGameSecond)
{
    /// <summary>
    /// Порция усвоена. Тот же порог в тысячную долю единицы, что и у желудка:
    /// дробные ставки оставляют 1e-13 вместо нуля, и строгое сравнение с нулём
    /// держало бы пустую порцию в желудке лишний шаг.
    /// </summary>
    public bool IsEmpty =>
        EnergyRemaining <= 0.001d &&
        HydrationRemaining <= 0.001d;

    /// <summary>
    /// Сколько ИГРОВЫХ СЕКУНД осталось до исчезновения порции.
    ///
    /// Берётся максимум по двум шкалам, потому что порция исчезает, когда
    /// кончится ОБЕ: у еды с водой запас жидкости может пережить запас энергии.
    /// Считается из СОБСТВЕННЫХ остатка и ставки — второго счётчика времени нет,
    /// иначе он разошёлся бы с остатком после округления.
    /// </summary>
    public double RemainingGameSeconds
    {
        get
        {
            var byEnergy = EnergyPerGameSecond > 0d
                ? EnergyRemaining / EnergyPerGameSecond
                : 0d;
            var byHydration = HydrationPerGameSecond > 0d
                ? HydrationRemaining / HydrationPerGameSecond
                : 0d;
            var remaining = Math.Max(byEnergy, byHydration);
            return double.IsFinite(remaining) && remaining > 0d ? remaining : 0d;
        }
    }
}

public sealed record StomachContents(
    double EnergyRemaining,
    double HydrationRemaining,
    double EnergyPerGameSecond,
    double HydrationPerGameSecond,
    double VolumeFraction = 0d)
{
    public static StomachContents Empty => new(0d, 0d, 0d, 0d, 0d);

    /// <summary>
    /// Порции по отдельности — то, из чего складываются четыре числа выше.
    ///
    /// Суммы ОСТАЮТСЯ полями записи, а не вычисляются из списка: их пишет и
    /// читает сохранение, а также читает существующий код движка. Совпадение
    /// сумм и списка держит <see cref="CharacterDigestion.Normalize"/>.
    ///
    /// СТАВКА У КАЖДОЙ ПОРЦИИ СВОЯ. Соблазн сделать «одну ставку на желудок»
    /// выглядит физиологичнее, но он ломает главное, ради чего порции и заведены:
    /// таймер. При общей ставке (убывающей пропорционально) ВСЕ порции исчезают в
    /// один момент — вода и апельсин показывали бы одинаковый обратный отсчёт и
    /// пропадали вместе, хотя вода уходит быстрее. Поэтому ставка хранится на
    /// порции: у воды она «1,35 л/ч», у еды — «полный желудок за 2 часа», и
    /// каждая порция отдаёт своё за свой срок.
    /// </summary>
    public IReadOnlyList<StomachPortion> Portions { get; init; } =
        Array.Empty<StomachPortion>();

    /// <summary>
    /// Id предмета для ПОРЦИЙ ИЗ СТАРЫХ СОХРАНЕНИЙ (форматы до v15).
    ///
    /// В тех версиях желудок описывался одними суммами, а имя предмета лежало
    /// отдельно (<c>PlayerConditionState.LastConsumedItemId</c>). Читатель
    /// сохранения кладёт его сюда, и <see cref="CharacterDigestion.Normalize"/>
    /// строит из сумм одну порцию с этим именем. Иначе загрузка старого файла
    /// обнулила бы желудок — игрок потерял бы уже съеденное.
    /// </summary>
    public string LegacyItemId { get; init; } = string.Empty;

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
/// Модель взята из физических величин, заданных автором, а не назначена
/// «на глаз»:
///   • шкала энергии — 5000 ккал, шкала жидкости — 3000 мл (в единицах
///     хранения это по-прежнему 0..10000);
///   • объём желудка — 1 литр;
///   • полный желудок ЕДЫ опустошается за 2 игровых часа;
///   • жидкость усваивается со скоростью 1,35 литра в час.
///
/// Отсюда следуют ОБЕ ставки, и они постоянны:
///   • энергия — весь объём шкалы за 2 часа (5000 единиц в час);
///   • жидкость — 1350 мл в час. В процентах шкалы это 45% в час, потому что
///     шкала уменьшена автором с 15 000 до 3000 мл (1350 / 3000). В единицах
///     хранения ставка ОГРОМНА (4500 единиц в час) именно из-за уменьшения
///     шкалы: организм по-прежнему усваивает 1,35 л/ч, но теперь это почти
///     половина шкалы за час.
///
/// ВНИМАНИЕ (осознанное следствие). Уменьшая шкалу жидкости, автор НЕ менял
/// физиологию: литр воды остаётся литром. На 3000 мл шкале бутылка воды 500 мл
/// — это уже 16,7% шкалы (3333 единицы) вместо прежних 3,33%, и усвоение идёт с
/// той же абсолютной скоростью (1,35 л/ч), то есть шкала заполняется в 5 раз
/// быстрее в процентах. Это НЕ ошибка пересчёта: тесты, привязанные к «+10
/// единиц в минуту» и «900 единиц в час», пересчитаны на новые числа, а
/// «положительная динамика» остаётся верной с ещё большим запасом.
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
    /// НИЖНЯЯ ГРАНИЦА ДОЛИ ПОРЦИИ: доля литрового желудка, которую занимает даже
    /// самая мелкая позиция (пакет сахара 5 г, таблетка).
    ///
    /// Зачем граница. Без неё мелкая позиция занимала бы в желудке НОЛЬ и
    /// усваивалась мгновенно: место не тратится, таймер пуст, а признак «влезет»
    /// становится бессмысленным. Прежде это число стояло литералом (0,01) в
    /// нескольких местах, и проверки повторяли литерал за движком; теперь минимум
    /// назван один раз — по нему сверяются и приём, и признак «влезет», и тесты.
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

    /// <summary>Объём желудка: 1 литр (задано автором).</summary>
    public const double StomachVolumeLiters =
        CharacterVitalsTuning.StomachVolumeLiters;

    /// <summary>
    /// Жидкость усваивается со скоростью 1,35 литра в час.
    ///
    /// Величина задана ФИЗИОЛОГИЧЕСКИ (литрами), а не долей шкалы: литр воды
    /// остаётся литром независимо от того, сколько миллилитров вмещает шкала.
    /// Отсюда и «побочный эффект» уменьшения шкалы до 3000 мл: в ПРОЦЕНТАХ шкалы
    /// то же усвоение стало в 5 раз быстрее (45% в час вместо 9%), потому что
    /// делится на меньший объём. Пересчёт в единицы живёт в одном месте —
    /// <see cref="Begin"/>, а «литр за час» в подсказке читается из
    /// <see cref="HoursPerLitreOfWater"/>.
    ///
    /// Час ИГРОВОЙ: игровое время идёт 1:1 с реальным, но при ускорении FF
    /// желудок обязан переваривать быстрее вместе с миром.
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
    /// Полный желудок еды опустошается за 2 игровых часа (задано автором).
    /// Жидкость уходит по скорости усвоения — литр за
    /// <see cref="HoursPerLitreOfWater"/> игрового часа.
    /// </summary>
    public const double FoodStomachEmptyHours =
        CharacterVitalsTuning.FoodStomachEmptyHours;

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
    /// <paramref name="itemId"/> — ЧТО съедено: по нему интерфейс рисует иконку
    /// в желудке и подбирает название. Без него монитор показывал бы остаток
    /// безымянным, а «Вода» подменялась бы «Апельсином» (прежняя модель помнила
    /// только Id ПОСЛЕДНЕГО предмета).
    ///
    /// <paramref name="energyPercent"/> и <paramref name="hydrationPercent"/> —
    /// НОМИНАЛ порции в процентах шкалы (то, что раньше начислялось мгновенно),
    /// а <paramref name="portionFraction"/> — её объём внутри желудка.
    /// Ноль означает «порция этого ресурса не даёт» — тогда и ставка, и объём по
    /// этой шкале нулевые.
    ///
    /// Модель очереди с ограничением вместимости: если игрок ест, пока предыдущая
    /// порция ещё переваривается, остаток складывается, но ЖЕЛУДОК БОЛЬШЕ ЛИТРА
    /// НЕ ВМЕЩАЕТ. Порция, которая целиком не влезает, НЕ СЪЕДАЕТСЯ вовсе:
    /// принимается только та, для которой свободного места хватает. Ставка
    /// усвоения при этом НЕ меняется: она определяется физиологией, а не
    /// количеством съеденного.
    ///
    /// ПОЧЕМУ БОЛЬШЕ НЕ «ПРИНИМАЕМ ЧАСТЬ». Прежняя модель принимала порцию лишь в
    /// свободной части (`accepted = min(порция, свободно)`), и в порцию
    /// записывалась именно эта урезанная доля. Снаружи это выглядело как обман:
    /// автор выбирал банан «110 мл», а в желудке появлялась порция на 20 мл,
    /// потому что 90 мл были уже заняты. Числа в интерфейсе и содержимое желудка
    /// обязаны совпадать, поэтому теперь действует правило «либо целиком, либо
    /// никак»: оно же ровно то, что показывает интерфейс — серые и неактивные
    /// пункты меню и «Использовать (желудок полон)» в инвентаре
    /// (<see cref="CharacterDigestionReport.Fits"/>).
    /// </summary>
    public static StomachContents Begin(
        StomachContents current,
        string itemId,
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

        // Желудок не резиновый: порция либо влезает целиком, либо не съедена.
        var freeFraction = Math.Max(
            0d,
            1d - contents.VolumeFraction);

        if (portionFraction > freeFraction + 1e-9d)
            return contents;

        var energyAdd = Volume(
            energyPercent,
            energyMaximum,
            elevatedMetabolism,
            reducedMetabolism);
        var hydrationAdd = Volume(
            hydrationPercent,
            hydrationMaximum,
            elevatedMetabolism,
            reducedMetabolism);

        if (energyAdd <= 0d && hydrationAdd <= 0d)
        {
            // Порция без питательности (таблетка, сигарета) желудок НЕ занимает:
            // иначе она «повисала» бы в нём навсегда с нулевой ставкой, и монитор
            // показывал бы вечную иконку.
            return contents;
        }

        // Ставки у каждой порции СВОИ. Прежняя модель держала их рядом с суммой,
        // поэтому питьё после еды подменяло ставку еды: остаток был от обеда, а
        // скорость — от воды.
        var energyRate = RatePerSecond(
            energyMaximum,
            1d / FoodStomachEmptyHours,
            elevatedMetabolism,
            reducedMetabolism);
        var hydrationRate = RatePerSecond(
            hydrationMaximum,
            // Скорость усвоения жидкости задана ФИЗИЧЕСКИ — в литрах в час, — и
            // не зависит от того, сколько литров вмещает шкала. В долях шкалы
            // это HydrationAbsorptionLitersPerHour * 1000 /
            // HydrationScaleMilliliters (при 3000 мл = 45% в час).
            HydrationAbsorptionLitersPerHour * 1000d /
            HydrationScaleMilliliters,
            elevatedMetabolism,
            reducedMetabolism);

        var portion = new StomachPortion(
            ItemId: string.IsNullOrWhiteSpace(itemId)
                ? "unknown"
                : itemId,
            // В порции — ПОЛНЫЙ объём предмета, а не принятая доля: раз порция
            // принята целиком (см. правило выше), её объём и есть объём предмета.
            // Это и делает площадь плитки в мониторе равной подписи «110 мл».
            VolumeFraction: portionFraction,
            EnergyRemaining: energyAdd,
            HydrationRemaining: hydrationAdd,
            EnergyPerGameSecond: energyAdd > 0d ? energyRate : 0d,
            HydrationPerGameSecond: hydrationAdd > 0d ? hydrationRate : 0d);

        var portions = new List<StomachPortion>(
            contents.Portions.Count + 1);
        portions.AddRange(contents.Portions);
        portions.Add(portion);

        // Ставки в суммах — сумма ставок порций: их читает движок для подсказки
        // скорости, и они обязаны описывать то, что усваивается прямо сейчас, а
        // не «ставку последнего предмета».
        return new StomachContents(
            contents.EnergyRemaining + energyAdd,
            contents.HydrationRemaining + hydrationAdd,
            contents.EnergyPerGameSecond + portion.EnergyPerGameSecond,
            contents.HydrationPerGameSecond + portion.HydrationPerGameSecond,
            Math.Clamp(
                contents.VolumeFraction + portionFraction,
                0d,
                1d))
        {
            Portions = portions
        };
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

        // Каждая порция переваривается СВОЕЙ СТАВКОЙ и уходит САМА, когда её
        // остаток кончился. Именно поэтому «Апельсин» не стирает «Воду»: у них
        // разные записи, разные сроки и разные ставки, и вода остаётся в желудке
        // ровно столько, сколько ей положено, — даже если её перестали называть
        // «последним предметом».
        var energyGain = 0d;
        var hydrationGain = 0d;
        var portions = new List<StomachPortion>(contents.Portions.Count);

        foreach (var portion in contents.Portions)
        {
            var portionEnergy = Math.Min(
                portion.EnergyRemaining,
                portion.EnergyPerGameSecond * seconds);
            var portionHydration = Math.Min(
                portion.HydrationRemaining,
                portion.HydrationPerGameSecond * seconds);

            energyGain += portionEnergy;
            hydrationGain += portionHydration;

            var energyRemaining = Math.Max(0d, portion.EnergyRemaining - portionEnergy);
            var hydrationRemaining = Math.Max(0d, portion.HydrationRemaining - portionHydration);

            // Полностью усвоенная порция ИСЧЕЗАЕТ из желудка — вместе со своим
            // объёмом. Ноль остатка означает «еды больше нет», и держать её в
            // списке значило бы рисовать в мониторе пустую иконку.
            if (energyRemaining <= 0.001d && hydrationRemaining <= 0.001d)
                continue;

            portions.Add(portion with
            {
                EnergyRemaining = energyRemaining,
                HydrationRemaining = hydrationRemaining,
                EnergyPerGameSecond = energyRemaining > 0d
                    ? portion.EnergyPerGameSecond
                    : 0d,
                HydrationPerGameSecond = hydrationRemaining > 0d
                    ? portion.HydrationPerGameSecond
                    : 0d
            });
        }

        // СУММЫ И ОБЪЁМ ВЫВОДЯТСЯ ИЗ ПОРЦИЙ: держать их отдельными числами
        // рядом со списком значило бы позволить им разойтись, и расхождение было
        // бы невидимым — монитор рисовал бы одно, а движок начислял другое.
        return new DigestionStep(
            new StomachContents(0d, 0d, 0d, 0d, 0d)
            {
                Portions = portions
            }.Normalize(),
            energyGain,
            hydrationGain);
    }

    public static StomachContents Normalize(this StomachContents contents)
    {
        contents ??= StomachContents.Empty;

        var raw = contents.Portions ?? Array.Empty<StomachPortion>();
        var portions = new List<StomachPortion>(raw.Count);

        foreach (var portion in raw)
        {
            if (portion is null)
                continue;

            var energyRemaining = NormalizeNonNegative(portion.EnergyRemaining);
            var hydrationRemaining = NormalizeNonNegative(portion.HydrationRemaining);

            if (energyRemaining <= 0d && hydrationRemaining <= 0d)
                continue;

            var energyRate = NormalizeNonNegative(portion.EnergyPerGameSecond);
            var hydrationRate = NormalizeNonNegative(portion.HydrationPerGameSecond);

            if (energyRemaining <= 0d) energyRate = 0d;
            if (hydrationRemaining <= 0d) hydrationRate = 0d;

            var volume = double.IsFinite(portion.VolumeFraction)
                ? Math.Clamp(portion.VolumeFraction, 0d, 1d)
                : 0d;

            portions.Add(portion with
            {
                ItemId = string.IsNullOrWhiteSpace(portion.ItemId)
                    ? "unknown"
                    : portion.ItemId,
                EnergyRemaining = energyRemaining,
                HydrationRemaining = hydrationRemaining,
                EnergyPerGameSecond = energyRate,
                HydrationPerGameSecond = hydrationRate,
                VolumeFraction = volume
            });
        }

        // Фолбэк для СТАРЫХ сохранений (v13/v14): там порций ещё не было, и
        // желудок описывался одними суммами. Превращаем их в одну порцию под
        // Id последнего предмета — иначе загрузка обнулила бы желудок, то есть
        // игрок потерял бы уже съеденное. Объём берём из VolumeFraction: он и
        // был занят этой порцией.
        if (portions.Count == 0)
        {
            var legacyEnergy = NormalizeNonNegative(contents.EnergyRemaining);
            var legacyHydration = NormalizeNonNegative(contents.HydrationRemaining);

            if (legacyEnergy > 0d || legacyHydration > 0d)
            {
                portions.Add(new StomachPortion(
                    string.IsNullOrWhiteSpace(contents.LegacyItemId)
                        ? "unknown"
                        : contents.LegacyItemId,
                    double.IsFinite(contents.VolumeFraction)
                        ? Math.Clamp(contents.VolumeFraction, 0d, 1d)
                        : 0d,
                    legacyEnergy,
                    legacyHydration,
                    NormalizeNonNegative(contents.EnergyPerGameSecond),
                    NormalizeNonNegative(contents.HydrationPerGameSecond)));
            }
        }

        // СУММЫ И ОБЪЁМ ВЫВОДЯТСЯ ИЗ ПОРЦИЙ. Списки и суммы не могут разойтись,
        // потому что суммы больше не хранятся отдельно: они и есть сумма списка.
        // Это единственное место, где желудок «собирается», поэтому правило
        // соблюдается и после загрузки сохранения.
        var energy = 0d;
        var hydration = 0d;
        var energyPerSecond = 0d;
        var hydrationPerSecond = 0d;
        var volumeTotal = 0d;

        foreach (var portion in portions)
        {
            energy += portion.EnergyRemaining;
            hydration += portion.HydrationRemaining;
            energyPerSecond += portion.EnergyPerGameSecond;
            hydrationPerSecond += portion.HydrationPerGameSecond;
            volumeTotal += portion.VolumeFraction;
        }

        return new StomachContents(
            energy,
            hydration,
            energyPerSecond,
            hydrationPerSecond,
            Math.Clamp(volumeTotal, 0d, 1d))
        {
            Portions = portions
        };
    }

    /// <summary>
    /// Сколько ЕДИНИЦ В ИГРОВУЮ СЕКУНДУ поступает по шкале.
    ///
    /// <paramref name="shareOfScalePerHour"/> — доля ШКАЛЫ в час (для еды это
    /// 1/2, потому что полный желудок уходит за 2 часа; для жидкости —
    /// 1350/3000, потому что усваивается 1,35 литра в час из трёхлитровой
    /// шкалы). Метаболизм ускоряет или замедляет эту долю.
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
    double EnergyKilocalories,
    double WaterMilliliters,
    double RemainingGameSeconds,
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

    /// <summary>
    /// Объём порции в МИЛЛИЛИТРАХ.
    ///
    /// Единственная величина объёма, которой пользуется интерфейс: миллилитр и
    /// грамм здесь приравнены (и то и другое — объём порции), а литры давали
    /// ровно те же числа, только с плавающей запятой в подписи. Именно
    /// миллилитры показывает список содержимого, и именно с ними сверяется
    /// этикетка предмета.
    /// </summary>
    public double VolumeMilliliters => VolumeLiters * 1000d;
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
    /// Вместимость желудка в МИЛЛИЛИТРАХ (1 литр).
    ///
    /// Миллилитры — рабочая величина интерфейса: и подпись «занято / всего», и
    /// вёрстка области желудка считаются в них, а не в долях и не в литрах.
    /// </summary>
    public static double StomachCapacityMilliliters =>
        CharacterDigestion.StomachVolumeLiters * 1000d;

    /// <summary>
    /// Поместится ли ОДИН предмет в свободную часть желудка.
    ///
    /// Зачем ответ в домене, а не в интерфейсе. Правило одно на всех: «объём
    /// порции больше свободного места — предмет употребить нельзя». По нему
    /// серятся пункты меню желудка, по нему же гаснет «Использовать» в
    /// инвентаре, и по нему же движок решает, принять ли порцию. Если бы
    /// каждая страница считала сама, правило разошлось бы с движком при первой
    /// правке, и «нельзя» в интерфейсе означало бы не то, что делает начисление.
    ///
    /// Сравнение ДОСЛОВНО повторяет <see cref="CharacterDigestion.Begin"/>,
    /// включая допуск 1e-9: порция принимается целиком либо не принимается
    /// вовсе, и признак «годится» обязан совпадать с приёмом на самой границе.
    /// Иначе пункт меню был бы живым при «не влезает ровно», а желудок порцию
    /// молча не принимал.
    /// </summary>
    public static bool Fits(
        StomachContents contents,
        string itemId)
    {
        var normalized = (contents ?? StomachContents.Empty).Normalize();
        var required = Math.Max(
            CharacterDigestion.MinimumPortionFraction,
            CharacterConsumableCatalog.GetPortionFractionOrDefault(itemId));
        var free = Math.Max(0d, 1d - normalized.VolumeFraction);

        return required <= free + 1e-9d;
    }

    /// <summary>Порции желудка в физических величинах, в порядке употребления.</summary>
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
                portion.VolumeFraction,
                // Объём порции в литрах: доля литрового желудка.
                portion.VolumeFraction * CharacterDigestion.StomachVolumeLiters,
                // Килокалории: энергия — доля шкалы, шкала — EnergyScaleKilocalories.
                portion.EnergyRemaining /
                PlayerConditionScale.Maximum *
                CharacterDigestion.EnergyScaleKilocalories,
                // Миллилитры: жидкость — доля шкалы, шкала — HydrationScaleMilliliters.
                portion.HydrationRemaining /
                PlayerConditionScale.Maximum *
                CharacterDigestion.HydrationScaleMilliliters,
                portion.RemainingGameSeconds,
                // Ставки берём у САМОЙ порции, а не считаем от остатка: они
                // хранятся в домене и не меняются, пока порция не исчерпает эту
                // шкалу. Пересчёт здесь завёл бы вторую формулу скорости.
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
    /// </summary>
    public static IReadOnlyList<DigestionFactor> MetabolismFactors(
        double metabolismPercent,
        bool elevatedMetabolism,
        bool reducedMetabolism)
    {
        var result = new List<DigestionFactor>();

        if (elevatedMetabolism)
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
        else if (reducedMetabolism)
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
