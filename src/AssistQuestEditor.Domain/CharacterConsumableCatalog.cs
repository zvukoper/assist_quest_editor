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
    /// </summary>
    public static ConsumableProfile GetProfile(string itemId) =>
        Profiles.TryGetValue(
            itemId ?? string.Empty,
            out var profile)
                ? profile
                : ConsumableProfile.None;

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
            ["skin.ointment"] = 0d
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

            // Еда: масса порции и калорийность на неё.
            ["food.meal"] = new(400d, 600d, 250d),
            ["food.soup"] = new(400d, 220d, 350d),
            ["food.oatmeal"] = new(300d, 320d, 240d),
            ["food.buckwheat"] = new(300d, 330d, 200d),
            ["food.egg_sandwich"] = new(180d, 350d, 60d),
            ["food.vegetable_stew"] = new(350d, 240d, 280d),
            ["food.cheese"] = new(100d, 360d, 40d),
            ["food.nuts"] = new(60d, 360d, 5d),
            ["food.yogurt"] = new(150d, 100d, 130d),
            ["food.banana"] = new(150d, 130d, 110d),
            ["food.apple"] = new(180d, 90d, 155d),
            ["food.orange"] = new(200d, 90d, 170d),
            ["food.honey"] = new(30d, 90d, 5d),
            ["food.dark_chocolate"] = new(30d, 170d, 2d),
            ["meat"] = new(300d, 600d, 180d),
            ["gosha.homemade_sausage"] = new(200d, 500d, 90d)
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
            "supplement.multivitamin",
            "Мультивитамины",
            "Комбинированный витаминный комплекс. Регулярный приём может привести к перку «Бык».",
            "БАД",
            "#c7a64b"),
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
            "#c4973e")
    ];
}
