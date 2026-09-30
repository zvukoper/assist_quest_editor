namespace AssistQuestEditor.Domain;

/// <summary>
/// Физический профиль съедобного/питьевого предмета.
///
/// Зачем он нужен. Шкалы получили физический смысл — 5000 ккал энергии и
/// 3000 мл жидкости, — поэтому «сколько восстанавливает предмет» обязано быть
/// выражено в КАЛОРИЯХ и МИЛЛИЛИТРАХ, а не в абстрактных процентах. Иначе
/// бутылка питьевой воды на 500 мл восполняла бы 3,75 литра, а правило автора
/// «организм усваивает 1 литр в час» не выполнялось бы вовсе: 25% шкалы за час
/// — это 3,75 л в час.
///
/// Масса задаёт ещё и ВРЕМЯ усвоения: она определяет, какую долю литрового
/// желудка займёт порция (см. <see cref="CharacterDigestion.PortionFraction"/>).
/// </summary>
public sealed record ConsumableProfile(
    double Grams,
    double Kilocalories,
    double WaterMilliliters)
{
    public static ConsumableProfile None => new(0d, 0d, 0d);

    /// <summary>Сколько процентов шкалы энергии даёт предмет.</summary>
    public double EnergyPercent =>
        Kilocalories /
        (CharacterDigestion.EnergyScaleKilocalories / 100d);

    /// <summary>Сколько процентов шкалы жидкости даёт предмет.</summary>
    public double HydrationPercent =>
        WaterMilliliters /
        (CharacterDigestion.HydrationScaleMilliliters / 100d);

    /// <summary>Какую долю литрового желудка занимает порция.</summary>
    public double PortionFraction =>
        CharacterDigestion.PortionFraction(Grams);

    public bool Feeds => EnergyPercent > 0d || HydrationPercent > 0d;
}

/// <summary>
/// Расширение каталога предметов для Character Vitals.
/// Дозы кофеина живут в <see cref="CharacterItemTuning"/> — калибровочном
/// файле, а не здесь: они часть баланса персонажа, и правка баланса не должна
/// требовать поиска по каталогу.
/// </summary>
public static class CharacterConsumableCatalog
{
    public static double GetCaffeineMg(string itemId) =>
        CharacterItemTuning.CaffeineMgOf(itemId);

    /// <summary>
    /// ОБЪЁМ ПОРЦИИ предмета в граммах (для еды) или миллилитрах (для питья).
    ///
    /// Число нужно пищеварению: объём порции задаёт, какую долю литрового
    /// желудка она займёт, а значит и СКОЛЬКО ИГРОВЫХ ЧАСОВ будет восполняться
    /// шкала (см. <see cref="CharacterDigestion.PortionFraction"/>). Прежняя
    /// модель времени не учитывала, поэтому мелкая еда не успевала перекрыть
    /// расход, и динамика энергии оставалась красной.
    ///
    /// Значения — справочные бытовые порции, а не медицинские нормы: бутылка
    /// воды 500 мл, тарелка супа 400 г, банан 150 г.
    /// </summary>
    public static double GetPortionSize(string itemId) =>
        GetProfile(itemId).Grams;

    /// <summary>
    /// Физический профиль предмета: масса, калорийность, вода.
    ///
    /// Единственный источник чисел о еде и питье. Движок берёт из него объём
    /// восстановления, время усвоения и подпись состава в окне «Предметы», а
    /// тесты — эталон для проверки баланса. Второй таблицы быть не должно:
    /// разъехавшись, она показала бы игроку одно, а начислила другое.
    ///
    /// ПОЗИЦИИ КАФЕ — исключение по форме, но не по смыслу: у них питательность
    /// задана ЦЕННОСТЬЮ блюда (A-B-C-D), потому что автор задал её именно так и
    /// в процентах шкалы. Здесь она переводится в ккал и мл ровно один раз, чтобы
    /// дальше весь путь (желудок, окно предметов, подсказка монитора) шёл общим
    /// кодом и не знал о существовании формата ценности.
    ///
    /// Перевод: B процентов голода = B × 50 ккал (полная шкала 5000), C процентов
    /// жажды = C × 30 мл (полная шкала 3000). Это ТОТ ЖЕ пересчёт «процент ↔
    /// физическая величина», которым живёт движок, поэтому «суп утоляет голод» в
    /// кафе и «400 г супа» в окне предметов не расходятся.
    /// </summary>
    public static ConsumableProfile GetProfile(string itemId)
    {
        if (Profiles.TryGetValue(
                itemId ?? string.Empty,
                out var profile))
            return profile;

        var cafe = GetCafeValue(itemId);
        if (!cafe.HasEffect)
            return ConsumableProfile.None;

        // Объём берётся из таблицы НАПРЯМУЮ, а не через GetPortionGrams: тот
        // первым делом зовёт GetProfile, и вызов замкнулся бы сам на себя.
        var grams = NonFoodPortions.TryGetValue(
                        itemId ?? string.Empty,
                        out var portion)
                    && portion > 0d
                        ? portion
                        : DefaultNonFoodPortionGrams;

        return new ConsumableProfile(
            grams,
            cafe.HungerPercent * KilocaloriesPerHungerPercent,
            cafe.ThirstPercent * MillilitersPerThirstPercent);
    }

    /// <summary>Сколько ккал приходится на 1% «утоления голода» из ценности блюда.</summary>
    public const double KilocaloriesPerHungerPercent =
        CharacterDigestion.EnergyScaleKilocalories / 100d;

    /// <summary>Сколько мл приходится на 1% «утоления жажды» из ценности блюда.</summary>
    public const double MillilitersPerThirstPercent =
        CharacterDigestion.HydrationScaleMilliliters / 100d;

    /// <summary>
    /// Объём порции предмета как доля литрового желудка (0..1).
    ///
    /// Единое место пересчёта: и начисление (движок), и подсказка интерфейса
    /// обязаны брать одно число, иначе «съел 400 г» в окне не совпадёт со
    /// временем переваривания в мире.
    /// </summary>
    public static double GetPortionFraction(string itemId) =>
        GetProfile(itemId).PortionFraction;

    /// <summary>
    /// ОБЪЁМ ПОРЦИИ для ЛЮБОГО предмета, включая неедовые.
    ///
    /// Зачем отдельно от <see cref="GetProfile"/>. Пищевой профиль есть лишь у
    /// еды и питья: таблетка, сигарета и настойка кормят «побочно» — калорий у
    /// них нет, но место в желудке они занимают. В прежней модели объём брался
    /// из профиля, и у всего остального он был НУЛЕВОЙ: движок подставлял
    /// минимум 0,01, поэтому и всасываемость, и таймер в мониторе считались по
    /// одному и тому же крошечному числу для таблетки и для обеда.
    ///
    /// Здесь три источника, по порядку доверия:
    ///   1. пищевой профиль — настоящая масса порции (400 г супа, 500 мл воды);
    ///   2. явная таблица ниже — бытовой объём порции для неедовых предметов;
    ///   3. осторожный запас 100 мл — так предмет участвует в пищеварении,
    ///      но не раздувает желудок до размеров обеда.
    /// </summary>
    public static double GetPortionFractionOrDefault(string itemId)
    {
        var profile = GetProfile(itemId);
        if (profile.Grams > 0d)
            return profile.PortionFraction;

        return CharacterDigestion.PortionFraction(
            GetPortionGrams(itemId));
    }

    /// <summary>
    /// ОБЪЁМ ПОРЦИИ в МИЛЛИЛИТРАХ — сколько места предмет займёт в желудке.
    ///
    /// Это ЕДИНСТВЕННОЕ число объёма порции: и место в желудке
    /// (<see cref="CharacterDigestion.Begin"/>), и признак «влезет»
    /// (<see cref="CharacterDigestionReport.Fits"/>), и подпись в меню желудка, и
    /// объём в окне предметов обязаны брать его отсюда. Градуировка задана
    /// ОБЪЁМОМ, а грамм и миллилитр приравнены (порция воды 500 мл весит 500 г),
    /// поэтому отдельной «массы» у порции нет — иначе появилось бы второе число,
    /// которое рано или поздно разошлось бы с первым.
    ///
    /// Почему миллилитры, а не граммы, в ИМЕНИ. Автор сверяет объём с подписью
    /// «N мл» и в меню, и в желудке. Прежний код отдавал в интерфейс `grams`, и в
    /// меню рядом с едой печаталось содержимое ВОДЫ («Банан 110 мл» — это вода в
    /// банане), а не объём порции (150 мл). Правильная величина была здесь всё
    /// время; ошибочным было её ИМЯ в разметке.
    /// </summary>
    public static double GetPortionMilliliters(string itemId) =>
        GetPortionGrams(itemId);

    /// <summary>
    /// Сколько ГРАММОВ занимает порция ПРЕДМЕТА БЕЗ ПИЩЕВОГО ПРОФИЛЯ (включая
    /// бытовые). Таблетка — граммы, напитки не из профиля — миллилитры.
    ///
    /// Хранилище чисел; для показа объёма пользуйтесь
    /// <see cref="GetPortionMilliliters"/> — это то же число, но названное так,
    /// как его читает игрок.
    /// </summary>
    public static double GetPortionGrams(string itemId)
    {
        var profile = GetProfile(itemId);
        if (profile.Grams > 0d)
            return profile.Grams;

        return NonFoodPortions.TryGetValue(
            itemId ?? string.Empty,
            out var value)
                ? value
                : DefaultNonFoodPortionGrams;
    }

    /// <summary>Порция по умолчанию, если о предмете ничего не известно.</summary>
    private const double DefaultNonFoodPortionGrams = 100d;

    /// <summary>
    /// Объём порции (граммы≈миллилитры) для предметов БЕЗ пищевого профиля.
    ///
    /// Числа бытовые: таблетка ≈ 1 г, сигарета ≈ 1 г, стограммовый пакетик
    /// сорбента — 100 г, полная порция приготовленного молока — 250 г. Они
    /// задают только МЕСТО в желудке; питательность таких предметов считают
    /// эффекты (<see cref="CharacterItemTuning"/>), а не диетология.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> NonFoodPortions =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["food.milk"] = 250d,
            ["food.meal"] = 400d,
            ["meat"] = 300d,
            ["gosha.homemade_sausage"] = 200d,
            ["supplement.multivitamin"] = 3d,
            ["vitamin.c_effervescent"] = 5d,
            ["vitamin.c"] = 3d,
            ["supplement.omega3"] = 3d,
            ["vitamin.d3"] = 3d,
            ["mineral.magnesium"] = 3d,
            ["mineral.zinc"] = 3d,
            ["adaptogen.ashwagandha"] = 3d,
            ["adaptogen.tincture"] = 30d,
            ["medicine.valerian"] = 3d,
            ["medicine.sorbent"] = 100d,
            ["medicine.recovery_salts"] = 1000d,
            ["painkiller"] = 2d,
            ["smoke.cigarette"] = 1d,
            ["soap"] = 0d,
            ["skin.ointment"] = 0d,

            // ── Меню кафе «У Дороги» ─────────────────────────────────────────
            // Питательность этих позиций задана ЦЕННОСТЬЮ (A-B-C-D, см.
            // CafeValues), а не профилем, поэтому здесь только ОБЪЁМ ПОРЦИИ:
            // он отвечает за место в желудке и время усвоения. Числа бытовые —
            // тарелка супа, порция второго, чашка напитка.
            ["cafe.borscht"] = 400d,
            ["cafe.cabbage_soup"] = 350d,
            ["cafe.ramen"] = 500d,
            ["cafe.chicken_broth"] = 300d,
            ["cafe.dumplings"] = 300d,
            ["cafe.fried_potato"] = 250d,
            ["cafe.mashed_potato_cutlet"] = 350d,
            ["cafe.chicken_shawarma"] = 300d,
            ["cafe.doner"] = 350d,
            ["cafe.olivier"] = 150d,
            ["cafe.herring_under_coat"] = 150d,
            ["cafe.mimosa_salad"] = 120d,
            ["cafe.assam_tea"] = 250d,
            ["cafe.americano"] = 250d,
            ["cafe.latte"] = 300d,
            ["cafe.cappuccino"] = 250d,
            ["cafe.green_tea"] = 250d,
            ["cafe.milk"] = 200d,
            ["cafe.sugar"] = 10d,
            ["cafe.fruit_syrup"] = 10d,
            ["cafe.sparkling_water"] = 500d,
            ["cafe.still_water"] = 500d,
            ["cafe.cola"] = 330d,
            ["cafe.tarkhun"] = 500d,
            ["cafe.dushes"] = 500d,
            // Салфетки желудок не занимают — как мыло: это предмет применения,
            // а не еды, и «съесть салфетку» не должно быть возможно.
            ["cafe.napkins"] = 0d,
            ["cafe.wet_wipes"] = 0d,
            ["cafe.cigarettes"] = 1d
        };

    /// <summary>
    /// Профили еды и питья. Калорийность и содержание воды — справочные бытовые
    /// значения (на 100 г порции), масса — размер обычной порции.
    ///
    /// Лечебные средства (таблетки, настойки, БАДы) сюда НЕ входят: они дают
    /// эффект, но не кормят и не занимают желудок. Для них остаётся мгновенное
    /// действие прежних правил.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ConsumableProfile> Profiles =
        new Dictionary<string, ConsumableProfile>(StringComparer.OrdinalIgnoreCase)
        {
            // Напитки: масса ≈ миллилитры, вода из этикетки.
            ["water.bottle"] = new(500d, 0d, 500d),
            ["food.milk"] = new(250d, 130d, 220d),
            ["drink.lemon_tea"] = new(300d, 90d, 290d),
            ["tea.herbal"] = new(250d, 2d, 245d),
            ["drink.coffee"] = new(250d, 10d, 245d),
            ["drink.espresso"] = new(40d, 5d, 38d),
            ["drink.energy"] = new(330d, 150d, 320d),
            ["drink.black_tea"] = new(250d, 5d, 245d),
            ["drink.green_tea"] = new(250d, 2d, 245d),
            ["drink.decaf_coffee"] = new(250d, 8d, 245d),
            ["drink.cola"] = new(330d, 140d, 300d),
            ["drink.ginger_lemon_tea"] = new(300d, 80d, 290d),
            ["drink.kvass"] = new(500d, 150d, 470d),
            ["drink.sports"] = new(500d, 120d, 490d),
            ["drink.beer"] = new(500d, 210d, 460d),
            ["food.kefir"] = new(250d, 130d, 220d),

            // Растворы для восполнения жидкости: пакетик разводится в воде, и
            // игрок выпивает раствор целиком, поэтому воды в профиле больше, чем
            // весит сам порошок.
            ["electrolyte.sachet"] = new(500d, 60d, 500d),
            ["supplement.electrolyte"] = new(500d, 80d, 500d),
            ["medicine.recovery_salts"] = new(1000d, 120d, 1000d),

            // Еда: масса порции и калорийность НА ПОРЦИЮ, пересчитанная из
            // справочных «на 100 г» по массе порции (calorizator / health-diet).
            ["food.meal"] = new(400d, 630d, 250d),
            ["food.soup"] = new(400d, 245d, 350d),
            ["food.oatmeal"] = new(300d, 320d, 240d),
            ["food.buckwheat"] = new(300d, 300d, 200d),
            ["food.egg_sandwich"] = new(180d, 320d, 60d),
            ["food.vegetable_stew"] = new(350d, 230d, 280d),
            ["food.cheese"] = new(100d, 365d, 40d),
            ["food.nuts"] = new(60d, 330d, 5d),
            ["food.yogurt"] = new(150d, 100d, 130d),
            ["food.banana"] = new(150d, 145d, 110d),
            ["food.apple"] = new(180d, 78d, 155d),
            ["food.orange"] = new(200d, 86d, 170d),
            ["food.honey"] = new(30d, 99d, 5d),
            ["food.dark_chocolate"] = new(30d, 170d, 2d),
            ["meat"] = new(300d, 560d, 180d),
            ["gosha.homemade_sausage"] = new(200d, 600d, 90d),
            // «Легендарный шашлык Руслана» — квестовая награда, а не магазинная
            // еда: порция крупная (шампур), поэтому и энергии в ней больше
            // обычного мяса. Сам форсаж живёт в CharacterItemTuning.
            ["ruslan.legendary_shashlik"] = new(300d, 700d, 120d)
        };

    /// <summary>
    /// Все Id, о которых домен знает ХОТЬ ЧТО-ТО: пищевой профиль или объём
    /// порции. Это перечень предметов С МЕХАНИКОЙ.
    ///
    /// Зачем наружу. Окно «Предметы» собирается ИЗ КАТАЛОГА
    /// (<see cref="ItemCatalogFactory"/>), поэтому предмет может получить
    /// пищевой профиль и ветку эффектов, но не попасть в каталог — и станет
    /// НЕВИДИМЫМ и для игрока, и для Хоста. Именно так из каталога пропали
    /// девять предметов (сыр, орехи, овощное рагу, йогурт, свежее мясо,
    /// мультивитамины, шипучий витамин C, омега-3, сорбент, соли для
    /// восстановления): их механика работала, а увидеть их было негде. Смоуки
    /// этого не ловили, потому что подавали эти Id СВОЕЙ фикстурой, а не
    /// реальным каталогом. Сторож — тест «у каждого предмета с механикой есть
    /// описание в каталоге».
    /// </summary>
    public static IEnumerable<string> KnownItemIds =>
        Profiles.Keys
            .Concat(NonFoodPortions.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ЦЕННОСТЬ БЛЮДА по формату кафе «У Дороги»: четыре числа
    /// A-B-C-D = здоровье, голод, жажда, тонус (см. <see cref="CafeValue"/>).
    ///
    /// Числа заданы автором в плане кафе и являются МЕХАНИКОЙ блюда, а не его
    /// описанием. Поэтому пересчёт в нынешние шкалы (ккал/мл) живёт в
    /// <c>CharacterVitalsEngine.UseItem</c>: там же применяются эффекты, и вторая
    /// таблица «что начислить» разошлась бы с этой.
    /// </summary>
    public static CafeValue GetCafeValue(string? itemId) =>
        CafeValues.TryGetValue(
            itemId ?? string.Empty,
            out var value)
                ? value
                : CafeValue.None;

    /// <summary>
    /// Ценности блюд кафе. Ключ — Id позиции меню.
    ///
    /// Откуда числа. План «Кафе „У Дороги“» (копилка задач, 2026) задаёт блюда,
    /// ЦЕНЫ в рублях и ценность в формате A-B-C-D, причём ценность — по РАЗДЕЛАМ
    /// меню. Три блюда имеют её иначе, чем раздел: так в оригинале.
    ///
    /// Как значения ложатся на нынешнюю модель (без выдумывания новых чисел):
    ///   A «Польза для здоровья» → +здоровье, процент шкалы здоровья;
    ///   B «Утоление голода»     → энергия. Формула 1% ценности = 50 ккал даёт
    ///     для «сытного блюда 100» ровно 5000 ккал — полную шкалу, то есть
    ///     исходный смысл B как процента шкалы;
    ///   D «Тонизирующий эффект» → снятие усталости (у автора это «снимает 10%
    ///     усталости»), то есть 1:1;
    ///   C «Утоление жажды»      → вода, процент шкалы жидкости.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, CafeValue> CafeValues =
        new Dictionary<string, CafeValue>(StringComparer.OrdinalIgnoreCase)
        {
            // Раздел «Первые блюда»: 100-80-90-0.
            ["cafe.borscht"] = new(100d, 80d, 90d, 0d, 250),
            ["cafe.cabbage_soup"] = new(100d, 80d, 90d, 0d, 200),
            ["cafe.ramen"] = new(100d, 80d, 90d, 0d, 350),
            ["cafe.chicken_broth"] = new(100d, 80d, 90d, 0d, 150),

            // Раздел «Основные блюда»: 80-100-30-0.
            ["cafe.dumplings"] = new(80d, 100d, 30d, 0d, 300),
            ["cafe.fried_potato"] = new(80d, 100d, 30d, 0d, 200),
            ["cafe.mashed_potato_cutlet"] = new(80d, 100d, 30d, 0d, 250),
            ["cafe.chicken_shawarma"] = new(80d, 100d, 30d, 0d, 180),
            ["cafe.doner"] = new(80d, 100d, 30d, 0d, 220),

            // Раздел «Салаты»: 90-40-10-0.
            ["cafe.olivier"] = new(90d, 40d, 10d, 0d, 90),
            ["cafe.herring_under_coat"] = new(90d, 40d, 10d, 0d, 120),
            ["cafe.mimosa_salad"] = new(90d, 40d, 10d, 0d, 85),

            // Раздел «Напитки»: 80-5-80-80.
            ["cafe.assam_tea"] = new(80d, 5d, 80d, 80d, 50),
            ["cafe.americano"] = new(80d, 5d, 80d, 80d, 130),
            ["cafe.latte"] = new(80d, 5d, 80d, 80d, 150),
            ["cafe.cappuccino"] = new(80d, 5d, 80d, 80d, 140),
            ["cafe.green_tea"] = new(80d, 5d, 80d, 80d, 50),

            // Раздел «Добавки»: 0-5-0-0.
            ["cafe.milk"] = new(0d, 5d, 0d, 0d, 20),
            ["cafe.sugar"] = new(0d, 5d, 0d, 0d, 10),
            ["cafe.fruit_syrup"] = new(0d, 5d, 0d, 0d, 10),

            // Раздел «Вода»: 100-0-100-0 у воды и своя ценность у напитков.
            ["cafe.sparkling_water"] = new(100d, 0d, 100d, 0d, 70),
            ["cafe.still_water"] = new(100d, 0d, 100d, 0d, 50),
            ["cafe.cola"] = new(20d, 5d, 90d, 90d, 70),
            ["cafe.tarkhun"] = new(30d, 5d, 90d, 10d, 50),
            ["cafe.dushes"] = new(30d, 5d, 90d, 10d, 60),

            // Раздел «Разное»: ценность 0-0-0-0 (эффекта нет, есть применение).
            ["cafe.napkins"] = new(0d, 0d, 0d, 0d, 15),
            ["cafe.wet_wipes"] = new(0d, 0d, 0d, 0d, 25),
            ["cafe.cigarettes"] = new(0d, 0d, 0d, 0d, 200)
        };

    public static IReadOnlyList<ItemDefinition> Items =>
    [
        new ItemDefinition(
            "drink.espresso",
            "Эспрессо",
            "Кофейный шот. Быстрый небольшой энергетический подъём; содержит около 80 мг кофеина в игровой модели.",
            "Напиток",
            "#68452e"),
        new ItemDefinition(
            "drink.black_tea",
            "Чёрный чай",
            "Мягкий стимулятор. Около 50 мг кофеина в игровой модели; немного снижает стресс.",
            "Напиток",
            "#856344"),
        new ItemDefinition(
            "drink.green_tea",
            "Зелёный чай",
            "Напиток с мягким стимулирующим эффектом; снижает стресс и слегка повышает устойчивость.",
            "Напиток",
            "#728f48"),
        new ItemDefinition(
            "drink.decaf_coffee",
            "Кофе без кофеина",
            "Кофейный напиток с очень малым количеством кофеина; действует в основном как успокаивающий ритуал.",
            "Напиток",
            "#6f5a49"),
        new ItemDefinition(
            "drink.cola",
            "Кола",
            "Сладкий газированный напиток с кофеином и небольшим восполнением жидкости.",
            "Напиток",
            "#6a4a32"),
        new ItemDefinition(
            "drink.kvass",
            "Квас",
            "Традиционный напиток: немного жидкости и энергии.",
            "Напиток",
            "#a16d3e"),
        new ItemDefinition(
            "drink.sports",
            "Изотоник",
            "Напиток для восстановления жидкости и электролитов после нагрузки.",
            "Напиток",
            "#4c9e9a"),
        new ItemDefinition(
            "drink.ginger_lemon_tea",
            "Имбирный чай с лимоном",
            "Тёплый напиток: жидкость, небольшое снижение стресса и поддержка метаболизма.",
            "Напиток",
            "#b68d45"),
        new ItemDefinition(
            "electrolyte.sachet",
            "Электролитный пакетик",
            "Раствор для восстановления жидкости после нагрузки или обезвоживания.",
            "Средство",
            "#6b9db2"),
        new ItemDefinition(
            "supplement.electrolyte",
            "Электролитный концентрат",
            "Более концентрированная смесь электролитов для быстрого восполнения жидкости.",
            "БАД",
            "#527d8f"),
        new ItemDefinition(
            "food.kefir",
            "Кефир",
            "Кисломолочный продукт: немного энергии и жидкости, снижает стресс.",
            "Еда",
            "#e6dfc9"),
        new ItemDefinition(
            "food.oatmeal",
            "Овсяная каша",
            "Сытная еда с длительным восстановлением энергии и небольшим влиянием на метаболизм.",
            "Еда",
            "#c8a971"),
        new ItemDefinition(
            "food.buckwheat",
            "Гречка",
            "Сытная крупа: хорошо поддерживает энергию и устойчивость.",
            "Еда",
            "#997457"),
        new ItemDefinition(
            "food.soup",
            "Горячий суп",
            "Еда и жидкость одновременно; немного уменьшает усталость.",
            "Еда",
            "#b27b50"),
        new ItemDefinition(
            "food.egg_sandwich",
            "Бутерброд с яйцом",
            "Быстрый источник энергии.",
            "Еда",
            "#d0a24f"),
        new ItemDefinition(
            "food.banana",
            "Банан",
            "Быстрый источник энергии и немного жидкости.",
            "Еда",
            "#d3b83f"),
        new ItemDefinition(
            "food.apple",
            "Яблоко",
            "Лёгкая еда с небольшим восполнением энергии и жидкости.",
            "Еда",
            "#a75c54"),
        new ItemDefinition(
            "food.orange",
            "Апельсин",
            "Фрукт с жидкостью и витамином C в игровой модели.",
            "Еда",
            "#d78a36"),
        new ItemDefinition(
            "food.honey",
            "Мёд",
            "Быстрый источник энергии.",
            "Еда",
            "#c38a25"),
        new ItemDefinition(
            "food.dark_chocolate",
            "Тёмный шоколад",
            "Небольшая порция быстрого удовольствия и энергии; содержит немного кофеина.",
            "Еда",
            "#65423b"),
        new ItemDefinition(
            "food.vegetable_stew",
            "Овощное рагу",
            "Сытное тёплое блюдо: энергия, жидкость и прибавка устойчивости.",
            "Еда",
            "#7d9a4f"),
        new ItemDefinition(
            "food.cheese",
            "Сыр",
            "Питательный продукт с длительным восстановлением энергии; немного повышает устойчивость.",
            "Еда",
            "#e0c56a"),
        new ItemDefinition(
            "food.nuts",
            "Орехи",
            "Калорийная закуска; повышает устойчивость, но жидкости почти не даёт.",
            "Еда",
            "#a9793f"),
        new ItemDefinition(
            "food.yogurt",
            "Йогурт",
            "Кисломолочный продукт: энергия, жидкость и снижение стресса.",
            "Еда",
            "#efe6dc"),
        new ItemDefinition(
            "meat",
            "Свежее мясо",
            "Сырое мясо: сытная еда, но требует приготовления. Продаётся у мясника.",
            "Продукт",
            "#b04a3f"),
        new ItemDefinition(
            "supplement.multivitamin",
            "Мультивитамины",
            "Комбинированный витаминный комплекс. Регулярный приём может привести к перку «Бык».",
            "БАД",
            "#c7a64b"),
        new ItemDefinition(
            "vitamin.c_effervescent",
            "Витамин C (шипучий)",
            "Растворимая в воде форма витамина C: заметно повышает устойчивость и метаболизм, немного снижает стресс. Действует быстрее обычного витамина C, но не даёт «Быка».",
            "БАД",
            "#f2d95c"),
        new ItemDefinition(
            "supplement.omega3",
            "Омега-3",
            "Жирные кислоты в капсулах: снижают стресс и слегка повышают устойчивость.",
            "БАД",
            "#d8b56a"),
        new ItemDefinition(
            "vitamin.d3",
            "Витамин D3",
            "Длительно действующая поддержка общего состояния; в игре повышает устойчивость.",
            "БАД",
            "#d2b769"),
        new ItemDefinition(
            "mineral.magnesium",
            "Магний",
            "Минеральная добавка: снижает стресс и слегка повышает устойчивость и метаболизм.",
            "БАД",
            "#777f8c"),
        new ItemDefinition(
            "mineral.zinc",
            "Цинк",
            "Минеральная добавка для поддержки устойчивости. Избыточное регулярное употребление имеет штраф.",
            "БАД",
            "#8f8b72"),
        new ItemDefinition(
            "adaptogen.ashwagandha",
            "Ашваганда",
            "Адаптоген. В игре уменьшает стресс и слегка повышает устойчивость, но может вызывать сонливость.",
            "Адаптоген",
            "#8b6e4d"),
        new ItemDefinition(
            "medicine.valerian",
            "Валериана",
            "Успокаивающее средство. Снижает стресс и временно вызывает сонливость.",
            "Препарат",
            "#7e8a75"),
        new ItemDefinition(
            "medicine.sorbent",
            "Сорбент",
            "Поглотитель токсинов: разово восстанавливает здоровье, немного снижает стресс и действует 4 игровых часа.",
            "Препарат",
            "#8f9aa5"),
        new ItemDefinition(
            "medicine.recovery_salts",
            "Соли для восстановления организма",
            "Сухая смесь солей для приёма внутрь: повышает устойчивость к нагрузке. Разводится в литре воды и выпивается целиком.",
            "Препарат",
            "#9aa7b3"),
        new ItemDefinition(
            "smoke.cigarette",
            "Сигарета",
            "Кратковременно снижает субъективный стресс, но создаёт никотиновый откат и дополнительную нагрузку.",
            "Вещество",
            "#8a8277"),
        new ItemDefinition(
            "drink.beer",
            "Пиво",
            "Алкогольный напиток: временное снижение стресса с последующим обезвоживающим эффектом.",
            "Напиток",
            "#c4973e"),

        // ── Меню кафе «У Дороги» ─────────────────────────────────────────────
        // Перенесено из плана кафе (копилка задач). Цены и ценности — от автора,
        // не выдуманы. Ценность стоит в описании тем же форматом A-B-C-D, каким
        // её задал автор: A здоровье, B голод, C жажда, D тонус. Показывать её
        // как «+100% здоровья» было бы неверно — это ВКЛАД в шкалу, а не
        // мгновенное восстановление до максимума.
        new ItemDefinition(
            "cafe.borscht",
            "Борщ со сметаной",
            "Первое блюдо. Ценность 100-80-90-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#a8324a"),
        new ItemDefinition(
            "cafe.cabbage_soup",
            "Щи из свежей капусты со сметаной",
            "Первое блюдо. Ценность 100-80-90-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#8fa85a"),
        new ItemDefinition(
            "cafe.ramen",
            "Рамен с курицей",
            "Первое блюдо. Ценность 100-80-90-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#c9a04a"),
        new ItemDefinition(
            "cafe.chicken_broth",
            "Куриный бульон с яйцом",
            "Первое блюдо. Ценность 100-80-90-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#d8c07a"),
        new ItemDefinition(
            "cafe.dumplings",
            "Пельмени домашние",
            "Основное блюдо. Ценность 80-100-30-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#c98f5a"),
        new ItemDefinition(
            "cafe.fried_potato",
            "Картошка жареная",
            "Основное блюдо. Ценность 80-100-30-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#d0a24f"),
        new ItemDefinition(
            "cafe.mashed_potato_cutlet",
            "Пюре с котлетой",
            "Основное блюдо. Ценность 80-100-30-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#d9b877"),
        new ItemDefinition(
            "cafe.chicken_shawarma",
            "Шаурма куриная",
            "Основное блюдо. Ценность 80-100-30-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#d9a05a"),
        new ItemDefinition(
            "cafe.doner",
            "Донер",
            "Основное блюдо. Ценность 80-100-30-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#c98a4a"),
        new ItemDefinition(
            "cafe.olivier",
            "Оливье",
            "Салат. Ценность 90-40-10-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#c2c98f"),
        new ItemDefinition(
            "cafe.herring_under_coat",
            "Сельдь под шубой",
            "Салат. Ценность 90-40-10-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#b56b7c"),
        new ItemDefinition(
            "cafe.mimosa_salad",
            "Мимоза",
            "Салат. Ценность 90-40-10-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#e0c06a"),
        new ItemDefinition(
            "cafe.assam_tea",
            "Чай чёрный Ассам",
            "Напиток. Ценность 80-5-80-80 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#9a5f38"),
        new ItemDefinition(
            "cafe.americano",
            "Кофе Американо",
            "Напиток. Ценность 80-5-80-80 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#5f4030"),
        new ItemDefinition(
            "cafe.latte",
            "Кофе Латте",
            "Напиток. Ценность 80-5-80-80 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#a5836a"),
        new ItemDefinition(
            "cafe.cappuccino",
            "Кофе Капуччино",
            "Напиток. Ценность 80-5-80-80 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#8f6b52"),
        new ItemDefinition(
            "cafe.green_tea",
            "Чай зелёный",
            "Напиток. Ценность 80-5-80-80 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#84995c"),
        new ItemDefinition(
            "cafe.milk",
            "Молоко (добавка)",
            "Добавка к напитку. Ценность 0-5-0-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#efe9de"),
        new ItemDefinition(
            "cafe.sugar",
            "Сахар",
            "Добавка к напитку. Ценность 0-5-0-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#f2efe6"),
        new ItemDefinition(
            "cafe.fruit_syrup",
            "Сироп фруктовый",
            "Добавка к напитку. Ценность 0-5-0-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#c96a86"),
        new ItemDefinition(
            "cafe.sparkling_water",
            "Вода газированная",
            "Вода. Ценность 100-0-100-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#5aa8d8"),
        new ItemDefinition(
            "cafe.still_water",
            "Вода негазированная",
            "Вода. Ценность 100-0-100-0 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#4b8fe8"),
        new ItemDefinition(
            "cafe.cola",
            "Кола (кафе)",
            "Газированный напиток кафе. Ценность 20-5-90-90 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#7a4a2c"),
        new ItemDefinition(
            "cafe.tarkhun",
            "Тархун",
            "Газированный напиток. Ценность 30-5-90-10 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#5f9e4a"),
        new ItemDefinition(
            "cafe.dushes",
            "Дюшес",
            "Газированный напиток. Ценность 30-5-90-10 (здоровье-голод-жажда-тонус).",
            "Кафе",
            "#c9b24a"),
        new ItemDefinition(
            "cafe.napkins",
            "Салфетки",
            "Разное. Ценности нет: предмет применения, а не еды.",
            "Кафе",
            "#e6e6e6"),
        new ItemDefinition(
            "cafe.wet_wipes",
            "Салфетки влажные",
            "Разное. Ценности нет: предмет применения, а не еды.",
            "Кафе",
            "#cfd8dd"),
        new ItemDefinition(
            "cafe.cigarettes",
            "Сигареты",
            "Разное. Ценности нет. Покупается в кафе наравне с едой, но действует как обычная сигарета.",
            "Кафе",
            "#8a8277")
    ];
}
