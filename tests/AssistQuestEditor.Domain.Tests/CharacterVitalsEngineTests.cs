using System.Globalization;
using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

public sealed class CharacterVitalsEngineTests
{
    private const double Hour = 3600d;

    /// <summary>Проценты → единицы шкалы без округления (для точных формул).</summary>
    private static double Units(double percent) =>
        percent * PlayerConditionScale.UnitsPerPercent;

    /// <summary>
    /// ЗАМЕР (не рассуждение) на живых данных: форсаж обязан расходоваться
    /// ПЕРВЫМ, а базовые шкалы — НЕ трогаться, пока запас форсажа не исчерпан.
    ///
    /// Автор: «Странное поведение форсажа. Расходуется не накопленная форсажем
    /// величина, а базовые единицы.» Проверяются ОБА пути расхода, потому что
    /// симптом мог прийти из любого:
    ///   1. периодический расход шага (`Advance` → `ApplyNegativeChange`);
    ///   2. восстановление здоровья, которое списывает энергию и жидкость
    ///      НАПРЯМУЮ (`HealthRegenAndExhaustionDrain`).
    /// </summary>
    [Fact]
    public void OverchargeIsSpentBeforeBaseScaleOnEveryDrainPath()
    {
        // Форсаж на ПОЛНУЮ шкалу: его хватает на весь час расхода с запасом,
        // поэтому падение базового числа будет означать, что форсаж пропущен.
        var conditions = PlayerConditionState.Empty with
        {
            OverchargeEnergy = PlayerConditionScale.FromPercent(100d),
            OverchargeHydration = PlayerConditionScale.FromPercent(100d)
        };

        var vitals = PlayerVitalsState.Default with
        {
            Energy = PlayerConditionScale.FromPercent(80d),
            Hydration = PlayerConditionScale.FromPercent(80d),
            Resilience = 0d
        };

        var after = CharacterVitalsEngine.Advance(
            vitals,
            conditions,
            Hour,
            Hour,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(
            PlayerConditionScale.FromPercent(80d),
            after.Vitals.Energy,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(80d),
            after.Vitals.Hydration,
            3);

        Assert.True(
            after.Conditions.OverchargeEnergy < conditions.OverchargeEnergy);
        Assert.True(
            after.Conditions.OverchargeHydration <
            conditions.OverchargeHydration);
    }

    /// <summary>
    /// Второй путь того же правила: восстановление здоровья тратит энергию и
    /// жидкость, и тратить обязано ФОРСАЖ, а не базовые шкалы.
    ///
    /// Отдельная проверка нужна потому, что этот расход идёт своим кодом
    /// (`HealthRegenAndExhaustionDrain`), а не через общий кошелёк негативных
    /// изменений, — и именно он дал симптом автора «Энергия имеет форсаж, но
    /// расходуется из 5000 базовых».
    ///
    /// Замер сравнивает ТРИ величины: сколько ушло из базовой шкалы, сколько
    /// ушло из форсажа и во сколько обошлось восстановление здоровья. Стоимость
    /// берётся из правила автора (1 здоровье = 1 энергия + 2 жидкости), поэтому
    /// «сколько обязано списаться» не подгоняется под ответ.
    /// </summary>
    [Fact]
    public void HealthRegenerationSpendsOverchargeBeforeBaseScale()
    {
        // Запас форсажа взят с большим излишком (80% шкалы), поэтому весь расход
        // часа обязан покрыться им одним.
        var conditions = PlayerConditionState.Empty with
        {
            OverchargeEnergy = PlayerConditionScale.FromPercent(80d),
            OverchargeHydration = PlayerConditionScale.FromPercent(80d)
        };

        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(80d),
            Hydration = PlayerConditionScale.FromPercent(80d),
            Resilience = 0d
        };

        var after = CharacterVitalsEngine.Advance(
            vitals,
            conditions,
            Hour,
            Hour,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        // Здоровье обязано вырасти — иначе замер бессмыслен.
        var healthRestored = after.Vitals.Health - vitals.Health;
        Assert.True(
            healthRestored > 0d,
            "здоровье не восстановилось — замер бесполезен");

        // Цена восстановления по правилу автора: 1 энергия и 2 жидкости на
        // единицу здоровья. Плюс обычный расход часа в покое.
        var energyResting =
            Units(CharacterVitalsEngine.EnergyConsumptionPercentPerHour(moving: false));
        var hydrationResting =
            Units(CharacterVitalsEngine.HydrationConsumptionPercentPerHour(moving: false));

        var energySpent =
            vitals.Energy +
            conditions.OverchargeEnergy -
            after.Vitals.Energy -
            after.Conditions.OverchargeEnergy;
        var hydrationSpent =
            vitals.Hydration +
            conditions.OverchargeHydration -
            after.Vitals.Hydration -
            after.Conditions.OverchargeHydration;

        Assert.Equal(
            energyResting + healthRestored * 1d,
            energySpent,
            3);
        Assert.Equal(
            hydrationResting + healthRestored * 2d,
            hydrationSpent,
            3);

        // ГЛАВНОЕ: пока форсажа хватает, базовые шкалы не двигаются ВООБЩЕ —
        // ни на восстановление здоровья, ни на расход жизнедеятельности. Это
        // дословное правило автора: «форсажные единицы — дополнительные единицы
        // сверх нормы, которые расходуются первыми, а базовые не расходуются».
        Assert.Equal(vitals.Energy, after.Vitals.Energy, 3);
        Assert.Equal(vitals.Hydration, after.Vitals.Hydration, 3);

        // И это покрыл форсаж — он обязан быть израсходован.
        Assert.True(
            after.Conditions.OverchargeEnergy <
            conditions.OverchargeEnergy,
            "форсаж энергии не израсходован");
        Assert.True(
            after.Conditions.OverchargeHydration <
            conditions.OverchargeHydration,
            "форсаж жидкости не израсходован");
    }

    /// <summary>
    /// ЦЕННОСТЬ БЛЮДА КАФЕ (A-B-C-D) обязана дойти до шкал, а цена — остаться
    /// числом автора.
    ///
    /// Проверка идёт на позиции, где НИ ОДНО значение не упирается в потолок или
    /// в ноль: тогда сравнение точное, а не «стало больше». «Тархун» (30-5-90-10)
    /// выбран именно поэтому: здоровье 40+30, усталость 50−10, а жажда и голод
    /// видны остатком в желудке.
    ///
    /// Цена проверяется отдельно: это единственное число, которое автор задал в
    /// рублях, и подменять его «средним по рынку» нельзя.
    /// </summary>
    [Fact]
    public void CafeMenuValuesApplyAndPricesStayAuthors()
    {
        var tarkhun = CharacterConsumableCatalog.GetCafeValue("cafe.tarkhun");
        Assert.Equal(50, tarkhun.PriceRubles);

        var vitals = (PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(40d),
            Fatigue = PlayerConditionScale.FromPercent(50d),
            Resilience = 0d
        }).Normalize();

        var after = CharacterVitalsEngine.UseItem(
            "cafe.tarkhun",
            vitals,
            PlayerConditionState.Empty);

        // A «Польза для здоровья»: 40% + 30% = 70%.
        Assert.Equal(
            PlayerConditionScale.FromPercent(70d),
            after.Vitals.Health,
            3);

        // D «Тонизирующий эффект»: 50% − 10% = 40%.
        Assert.Equal(
            PlayerConditionScale.FromPercent(40d),
            after.Vitals.Fatigue,
            3);

        // C «Утоление жажды»: 90% шкалы жидкости уходит в желудок.
        Assert.Equal(
            PlayerConditionScale.FromPercent(90d),
            after.Conditions.Stomach.HydrationRemaining,
            3);

        // B «Утоление голода»: 5% шкалы энергии.
        Assert.Equal(
            PlayerConditionScale.FromPercent(5d),
            after.Conditions.Stomach.EnergyRemaining,
            3);
    }

    /// <summary>
    /// У КАЖДОЙ позиции меню кафе есть цена, и все цены — числа автора.
    ///
    /// Сторож нужен потому, что цена просится «прикинуть»: без него следующая
    /// правка легко заменит 250 ₽ на «среднюю по рынку», и в игре окажется число,
    /// которого автор не задавал.
    /// </summary>
    [Fact]
    public void EveryCafeMenuItemKeepsItsAuthorsPrice()
    {
        var authored = new (string Id, int Price)[]
        {
            ("cafe.borscht", 250),
            ("cafe.cabbage_soup", 200),
            ("cafe.ramen", 350),
            ("cafe.chicken_broth", 150),
            ("cafe.dumplings", 300),
            ("cafe.fried_potato", 200),
            ("cafe.mashed_potato_cutlet", 250),
            ("cafe.chicken_shawarma", 180),
            ("cafe.doner", 220),
            ("cafe.olivier", 90),
            ("cafe.herring_under_coat", 120),
            ("cafe.mimosa_salad", 85),
            ("cafe.assam_tea", 50),
            ("cafe.americano", 130),
            ("cafe.latte", 150),
            ("cafe.cappuccino", 140),
            ("cafe.green_tea", 50),
            ("cafe.milk", 20),
            ("cafe.sugar", 10),
            ("cafe.fruit_syrup", 10),
            ("cafe.sparkling_water", 70),
            ("cafe.still_water", 50),
            ("cafe.cola", 70),
            ("cafe.tarkhun", 50),
            ("cafe.dushes", 60),
            ("cafe.napkins", 15),
            ("cafe.wet_wipes", 25),
            ("cafe.cigarettes", 200)
        };

        var catalog = ItemCatalogFactory.Items
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, price) in authored)
        {
            Assert.True(
                catalog.Contains(id),
                $"позиция меню кафе {id} пропала из каталога");

            Assert.Equal(
                price,
                CharacterConsumableCatalog.GetCafeValue(id).PriceRubles);
        }
    }

    /// <summary>
    /// У КАЖДОГО предмета каталога есть цена — либо явная, либо «не продаётся».
    ///
    /// Автор: «найти в интернете или вычислить по открытым источникам цену всех
    /// предметов каталога, всех съедобных порций, всех несъедобных предметов,
    /// КРОМЕ квестовых, которые никогда не продаются». Поэтому проверка требует
    /// ровно двух состояний и запрещает третье, самое опасное — ЗАБЫТОЕ: предмет
    /// без цены не должен молча выглядеть как «непродаваемый».
    /// </summary>
    [Fact]
    public void EveryCatalogItemIsEitherPricedOrDeliberatelyUnsellable()
    {
        var unpriced = new List<string>();

        foreach (var item in ItemCatalogFactory.Items)
        {
            var questOnly = ItemCatalogFactory.IsQuestOnly(item.Category);
            var price = ItemCatalogFactory.PriceRubles(item);
            var raw = ItemData.PriceRubles(item.Id);

            if (questOnly)
            {
                // Квестовое не продаётся НИКОГДА: цена обязана быть отсечена.
                Assert.Equal(0, price);
                continue;
            }

            if (price <= 0)
                unpriced.Add($"{item.Id} ({raw})");
        }

        Assert.True(
            unpriced.Count == 0,
            "предметы без цены вне квестовых: " + string.Join("; ", unpriced));
    }

    /// <summary>
    /// У КАЖДОЙ еды, которая НЕСЁТ энергию, есть БЖУ, и его энергия не
    /// противоречит калорийности.
    ///
    /// ПОЧЕМУ НЕ ВСЯ ЕДА. БЖУ и калорийность — СВЯЗАННЫЕ величины одного
    /// источника, и сверять их осмысленно только там, где источник вообще дал
    /// калорийность:
    ///   • у воды сверять нечего (0 ккал, 0 БЖУ);
    ///   • у квестового «мяса Руслана» пищевой профиль — игровая награда, а не
    ///     диетология: требовать от него табличной калорийности значит выдумывать
    ///     её;
    ///   • у позиций КАФЕ калорийность имеет ДРУГОЙ СМЫСЛ. Она задана автором как
    ///     ценность блюда (второе число A-B-C-D — «сколько сытости в процентах»),
    ///     и каталог переводит её в килокалории множителем 50. Это ЦЕНА СЫТОСТИ, а
    ///     не энергия из справочника, поэтому расхождение с БЖУ здесь —
    ///     норма, а не ошибка. Само БЖУ кафе проверяется ниже отдельным тестом.
    ///
    /// Исключения сделаны ВИДИМЫМИ списком, а не молчаливым `continue`: иначе
    /// следующая позиция кафе тихо выпала бы из проверки, и правило перестало бы
    /// что-либо ловить.
    /// </summary>
    [Fact]
    public void EveryEnergyBearingFoodHasMacronutrientsMatchingItsEnergy()
    {
        var missing = new List<string>();
        var mismatched = new List<string>();

        foreach (var item in ItemCatalogFactory.Items)
        {
            if (IsCafeGameValue(item.Id))
                continue;

            var declared = CharacterConsumableCatalog.GetProfile(item.Id).Kilocalories;

            // Ниже порога сверять нечего: у чашки чёрного кофе (10 ккал на 250 мл)
            // состав — следовые доли грамма, и ЛЮБОЕ округление до 0,1 г меняет
            // расчётную энергию вдвое. Это не ошибка таблицы, а предел точности
            // справочника, поэтому такие напитки пропускаются, а не «подгоняются».
            if (declared < TraceEnergyKilocalories)
                continue;

            // Спирт — отдельная статья: он даёт 7 ккал/г и в БЖУ не пишется вовсе.
            // У пива 210 ккал на 500 мл против 84 по составу, и оба числа верны.
            if (IsAlcoholic(item.Id))
                continue;

            var bju = ItemData.Nutrition(item.Id);

            if (!bju.HasAny)
            {
                missing.Add($"{item.Id} ({Math.Round(declared)} ккал)");
                continue;
            }

            var fromBju = bju.EnergyKilocalories;
            var drift = Math.Abs(fromBju - declared) / Math.Max(declared, 1d);

            if (drift > 0.35d)
            {
                mismatched.Add(
                    $"{item.Id}: БЖУ {Math.Round(fromBju)} против ккал {Math.Round(declared)}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "еда с энергией, но без БЖУ: " + string.Join("; ", missing));

        Assert.True(
            mismatched.Count == 0,
            "БЖУ и калорийность расходятся: " + string.Join("; ", mismatched));
    }

    /// <summary>
    /// Энергия ниже этой — следовые количества, где состав не проверяется.
    /// </summary>
    private const double TraceEnergyKilocalories = 25d;

    /// <summary>Предмет, чью энергию частично даёт спирт.</summary>
    private static bool IsAlcoholic(string itemId) =>
        itemId.Equals("drink.beer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Позиция меню кафе: её «калорийность» — это ценность блюда, а не энергия.
    /// </summary>
    private static bool IsCafeGameValue(string itemId) =>
        itemId.StartsWith("cafe.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// У КАЖДОЙ позиции кафе, которую едят, есть БЖУ.
    ///
    /// Отдельная проверка нужна потому, что предыдущий тест кафе исключает: там
    /// сверяется энергия, а у кафе сверять нечего. Здесь проверяется само
    /// НАЛИЧИЕ состава — автор просил «зафиксировать БЖУ для всех предметов»,
    /// и пропущенная строка кафе иначе осталась бы незамеченной никем.
    /// Еда кафе без состава = блюдо, которое будущая механика белка посчитает
    /// пустым.
    /// </summary>
    [Fact]
    public void EveryCafeFoodItemHasMacronutrients()
    {
        // Напитки-«пустышки» и предметы сервировки состава не имеют.
        var withoutMacros = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cafe.sparkling_water", "cafe.still_water",
            "cafe.napkins", "cafe.wet_wipes", "cafe.cigarettes"
        };

        var missing = new List<string>();

        foreach (var item in ItemCatalogFactory.Items)
        {
            if (!IsCafeGameValue(item.Id) || withoutMacros.Contains(item.Id))
                continue;

            if (!CharacterVitalsEngine.CanConsume(item.Id))
                continue;

            if (!ItemData.Nutrition(item.Id).HasAny)
                missing.Add(item.Id);
        }

        Assert.True(
            missing.Count == 0,
            "позиции кафе без БЖУ: " + string.Join("; ", missing));
    }

    /// <summary>
    /// Качество и состояние выставлены ВСЕМ: качество «Обычное», состояние —
    /// «Свежее» у еды и «Без повреждений» у прочего.
    ///
    /// Это задел: у предмета не может быть ПУСТОГО качества, иначе первая же
    /// механика крафта получила бы неопределённость. Проверка дешёвая, но именно
    /// она держит правило автора, заданное словами.
    /// </summary>
    [Fact]
    public void EveryItemHasDefaultQualityAndConditionByEdibility()
    {
        foreach (var item in ItemCatalogFactory.Items)
        {
            var edible = CharacterVitalsEngine.CanConsume(item.Id);
            var (quality, condition) = ItemData.Attributes(item.Id, edible);

            Assert.Equal(ItemQuality.Common, quality);

            Assert.Equal(
                edible ? ItemCondition.Fresh : ItemCondition.Intact,
                condition);

            // Подписи обязаны быть непустыми: их читает окно и журнал.
            Assert.False(string.IsNullOrWhiteSpace(
                ItemAttributeLabels.Quality(quality)));
            Assert.False(string.IsNullOrWhiteSpace(
                ItemAttributeLabels.Condition(condition)));
        }
    }

    /// <summary>
    /// Каждый предмет, у которого ЕСТЬ механика, обязан иметь описание в
    /// каталоге — иначе он недостижим для игрока.
    ///
    /// Зачем проверка. Окно «Предметы» собирается ИЗ КАТАЛОГА, а механика живёт
    /// отдельно (пищевой профиль + ветка эффектов). Прежний контракт стерёг
    /// только одно направление — «у каждого пункта каталога механика сработала».
    /// Обратное нарушалось МОЛЧА: девять предметов (сыр, орехи, овощное рагу,
    /// йогурт, свежее мясо, мультивитамины, шипучий витамин C, омега-3, сорбент)
    /// имели полную механику, но в каталог не попали, поэтому игрок не мог их
    /// получить или найти. Смоуки этого не видели: они подавали эти Id СВОЕЙ
    /// фикстурой, а не реальным каталогом.
    /// </summary>
    [Fact]
    public void EveryItemWithMechanicsIsDescribedInTheCatalog()
    {
        var catalog = ItemCatalogFactory.Items
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = CharacterVitalsEngine.ConsumableItemIds
            .Where(id => !catalog.Contains(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "предметы с механикой, которых нет в каталоге (игрок их не увидит): " +
            string.Join("; ", missing));
    }

    [Fact]
    public void MovingConsumesEnergyHydrationAndBuildsFatigue()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Resilience = 0d
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            Hour,
            Hour,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        // Нормы заданы автором физически: 2500 ккал за 3 игровых часа под
        // нагрузкой, 150 мл за 30 игровых минут под нагрузкой. Единая точка
        // пересчёта — те же помощники движка, что и в симуляции.
        Assert.Equal(
            100d -
            CharacterVitalsEngine.EnergyConsumptionPercentPerHour(moving: true),
            PlayerConditionScale.ToPercent(
                update.Vitals.Energy),
            2);

        Assert.Equal(
            100d -
            CharacterVitalsEngine.HydrationConsumptionPercentPerHour(moving: true),
            PlayerConditionScale.ToPercent(
                update.Vitals.Hydration),
            2);

        // Усталость растёт по шагам (900 с) без округления единиц: 100% за 18
        // игровых часов даёт 5,(5)% за час, а сравнение в ЕДИНИЦАХ округлило
        // бы 555,56 до 556. Поэтому меряем в процентах.
        Assert.Equal(
            100d / 18d,
            PlayerConditionScale.ToPercent(
                update.Vitals.Fatigue),
            3);

        // Гигиена падает на 100% за 3 игровых СУТОК, то есть примерно на 1,39%
        // за час: остаётся ~98,6%, а не 1,39%. Замер в процентах, потому что
        // спад считается по шагам (900 с) и округление каждого шага даёт
        // 9860, а не 9861.
        Assert.Equal(
            98.6d,
            PlayerConditionScale.ToPercent(
                update.Vitals.Hygiene),
            1);
    }

    [Fact]
    public void StressAboveFiftyAddsOnePercentCumulativePerGameHour()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(60d),
            CumulativeFatigue = PlayerConditionScale.FromPercent(100d)
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with { Resilience = 0d },
            conditions,
            Hour,
            Hour,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(
            PlayerConditionScale.FromPercent(1d),
            update.Conditions.CumulativeStress,
            3);

        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == "burnout");
    }

    [Fact]
    public void HotelSleepClearsMostOfStress()
    {
        // ЖАЛОБА АВТОРА: «после ночлега в гостинице метаболизм упал до 0, а
        // стресс остался 44». Обычный стресс обязан сниматься сном, иначе
        // единственным способом его убрать остаются предметы.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Fatigue = PlayerConditionScale.FromPercent(90d),
            Energy = PlayerConditionScale.FromPercent(20d),
            // Жидкость ВЫШЕ порога «Жажды» (50% шкалы): этот тест про сон и
            // стресс, а не про новые дебаффы. При 20% жажда добавила бы свой
            // стресс, и замер перестал бы говорить о том, о чём задуман.
            Hydration = PlayerConditionScale.FromPercent(70d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(44d)
        };

        var update = CharacterVitalsEngine.CompleteHotelSleep(vitals, conditions);

        // 100% стресса уходит за FullSleepStressClearHours часов полноценного
        // сна: за 7 часов при 6-часовой норме шкала обнуляется.
        Assert.Equal(0d, update.Conditions.Stress, 3);

        // Кумулятивный стресс неприкосновенен: его снимает только отпуск.
        Assert.Equal(0d, update.Conditions.CumulativeStress, 6);
    }

    [Fact]
    public void FullSleepClearsStressFasterThanRegularSleep()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(30d)
        };

        // Полноценный сон снимает 100% / 6 часов за час, то есть ставкой, а не
        // долей остатка: за 4 часа уходит 2/3 шкалы, и стресс 30% обнуляется.
        var full = CharacterVitalsEngine.Sleep(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            conditions,
            hours: 4,
            fullSleep: true);

        Assert.Equal(
            0d,
            full.Conditions.Stress,
            3);

        // А за один час — ровно шестая часть шкалы.
        var oneHour = CharacterVitalsEngine.Sleep(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            conditions,
            hours: 1,
            fullSleep: true);

        Assert.Equal(
            Units(30d) - Units(100d) / CharacterVitalsEngine.FullSleepStressClearHours,
            oneHour.Conditions.Stress,
            1);
    }

    [Fact]
    public void StressDoesNotFallToZeroFromShortRegularSleep()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Stress = PlayerConditionScale.FromPercent(100d)
        };

        // Обычный сон снимает стресс в 2,5 раза медленнее: за 6 часов уйдёт
        // 100 / (6 · 2,5) = 6,67%.
        var regular = CharacterVitalsEngine.Sleep(
            PlayerVitalsState.Default with
            {
                Fatigue = PlayerConditionScale.FromPercent(50d)
            },
            conditions,
            hours: 6,
            fullSleep: false);

        var removed = Units(100d) - regular.Conditions.Stress;

        Assert.Equal(
            Units(100d) / CharacterVitalsEngine.FullSleepStressClearHours /
            CharacterVitalsEngine.RegularSleepStressClearPenalty * 6d,
            removed,
            1);

        Assert.True(
            regular.Conditions.Stress > Units(50d),
            "обычный сон снимает стресс медленно, а не мгновенно: " +
            regular.Conditions.Stress);
    }

    [Fact]
    public void MetabolismDoesNotCollapseWhileOnlyCumulativeStressRemains()
    {
        // ЖАЛОБА АВТОРА: «метаболизм упал до 0 после ночлега». Причина была в
        // том, что гостиница снимает истощение энергии, жидкости и усталости, но
        // НЕ кумулятивный стресс (его снимает только отпуск) — а метаболизм
        // продолжал терять по 2% за КАЖДУЮ шкалу истощения каждые 15 игровых
        // минут, то есть до 32% в час против 1% в час восстановления.
        //
        // Теперь штраф применяется только к шкалам истощения, которые сон
        // реально снимает, поэтому оставшийся кумулятивный стресс метаболизм
        // не обнуляет.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Fatigue = PlayerConditionScale.FromPercent(90d),
            Energy = PlayerConditionScale.FromPercent(20d),
            // Жидкость ВЫШЕ порога «Жажды»: иначе новый дебафф добавлял бы
            // стресс, и кумулятивный стресс рос бы сам по себе, замаскировав
            // проверяемое поведение метаболизма.
            Hydration = PlayerConditionScale.FromPercent(70d),
            Metabolism = PlayerConditionScale.FromPercent(30d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            CumulativeStress = PlayerConditionScale.FromPercent(5d)
        };

        var update = CharacterVitalsEngine.CompleteHotelSleep(vitals, conditions);

        Assert.Equal(
            PlayerConditionScale.FromPercent(5d),
            update.Conditions.CumulativeStress,
            3);

        // Метаболизм обязан быть ВЫШЕ исходного: он восстанавливается к 60%.
        Assert.True(
            update.Vitals.Metabolism > vitals.Metabolism,
            "после ночлега метаболизм обязан восстанавливаться, а не падать в ноль: " +
            update.Vitals.Metabolism);
    }

    [Fact]
    public void HotelSleepFullyRestoresRequestedResourcesAndAdvancesSevenHours()
    {        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(80d),
            Energy = PlayerConditionScale.FromPercent(20d),
            Hydration = PlayerConditionScale.FromPercent(20d),
            Fatigue = PlayerConditionScale.FromPercent(90d),
            Hygiene = PlayerConditionScale.FromPercent(15d)
        };

        var conditions = PlayerConditionState.Empty with
        {
            CumulativeEnergy = PlayerConditionScale.FromPercent(20d),
            CumulativeHydration = PlayerConditionScale.FromPercent(10d),
            CumulativeFatigue = PlayerConditionScale.FromPercent(12d),
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "unkempt",
                    "Неопрятный",
                    3600d,
                    true)
            }
        };

        var update = CharacterVitalsEngine.CompleteHotelSleep(
            vitals,
            conditions);

        Assert.Equal(update.Vitals.MaxEnergy, update.Vitals.Energy);
        Assert.Equal(update.Vitals.MaxHydration, update.Vitals.Hydration);
        Assert.Equal(
            PlayerConditionScale.Maximum,
            update.Vitals.Hygiene);

        Assert.Equal(0d, update.Vitals.Fatigue, 3);
        Assert.Equal(0d, update.Conditions.CumulativeFatigue, 3);
        Assert.Equal(0d, update.Conditions.CumulativeEnergy, 3);
        Assert.Equal(0d, update.Conditions.CumulativeHydration, 3);

        Assert.DoesNotContain(
            update.Conditions.Effects,
            effect => effect.Id == "unkempt");
        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == "rested");
        Assert.Contains(
            update.Events,
            item => item.Kind == "HotelSleepCompleted");
    }

    [Fact]
    public void ItemUseStoresCountAndConsumesOverchargeBeforeNormalEnergy()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Energy = PlayerConditionScale.FromPercent(50d)
        };

        var after = CharacterVitalsEngine.UseItem(
            "drink.energy",
            vitals,
            PlayerConditionState.Empty);

        // Энергетик (330 мл, 150 ккал) даёт 3% шкалы энергии и 2,133% шкалы
        // жидкости, но восстановление теперь ПОСТЕПЕННОЕ: единицы уходят в
        // скрытый желудок, а не в шкалу сразу (см. CharacterDigestion). Кофеин
        // добавляет форсаж энергии примерно (80 / 12) = 6,67% мгновенно — он не
        // еда.
        Assert.Equal(
            PlayerConditionScale.FromPercent(50d),
            after.Vitals.Energy,
            3);

        Assert.Equal(
            PlayerConditionScale.FromPercent(150d / 50d),
            after.Conditions.Stomach.EnergyRemaining,
            2);

        // Ставка задана ПРОПУСКНОЙ СПОСОБНОСТЬЮ, а не таймером порции. У
        // единственной порции она равна базовой сухой, умноженной на её
        // «лёгкость» по содержанию воды (энергетик — почти сплошная вода:
        // 320 мл на 330 мл). Прежняя проверка «5000 единиц в час» описывала
        // модель, где ставку задавал таймер порции, и полный объём пищеварения
        // уходил за 2 часа.
        var portion = after.Conditions.Stomach.Portions[0];

        Assert.Equal(
            CharacterDigestion.DryThroughputMillilitersPerSecond *
            (1d + (CharacterDigestion.WaterContentSpeedFactor - 1d) *
             portion.WaterContent),
            after.Conditions.Stomach.Portions[0].PerGameSecond,
            6);

        Assert.Equal(
            80d / 12d,
            PlayerConditionScale.ToPercent(
                after.Conditions.OverchargeEnergy),
            2);

        Assert.Equal(
            1,
            after.Conditions.ItemUseCounts["drink.energy"]);

        var consumed = CharacterVitalsEngine.Advance(
            after.Vitals,
            after.Conditions,
            1800d,
            1800d,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            consumed.Conditions.OverchargeEnergy <
            after.Conditions.OverchargeEnergy);
    }

    [Fact]
    public void BullImprovesHealthRecoveryAndBlocksNewCumulativeExhaustion()
    {
        var conditions = PlayerConditionState.Empty with
        {
            CumulativeEnergy = PlayerConditionScale.FromPercent(5d),
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    24d * 3600d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Health = PlayerConditionScale.FromPercent(50d),
                Fatigue = PlayerConditionScale.FromPercent(90d),
                Resilience = 0d
            },
            conditions,
            Hour,
            Hour,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(0d, update.Conditions.CumulativeFatigue, 3);
        Assert.Equal(5d, PlayerConditionScale.ToPercent(update.Conditions.CumulativeEnergy), 3);
    }

    [Fact]
    public void CumulativeFatigueAccrualUsesTripledBaseAndDoubledStepPenalty()
    {
        // Задано автором: база ×3 (было ×2), штраф 0,5 за каждые 5% (было 0,25).
        Assert.Equal(
            CharacterVitalsEngine.CumulativeFatigueAccrualBaseMultiplier,
            CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
                PlayerConditionState.Empty),
            6);
        Assert.Equal(3d, CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
            PlayerConditionState.Empty), 6);

        Assert.Equal(3.5d, CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
            PlayerConditionState.Empty with
            {
                CumulativeFatigue = PlayerConditionScale.FromPercent(5d)
            }), 6);

        Assert.Equal(4d, CharacterVitalsEngine.CumulativeFatigueAccrualMultiplier(
            PlayerConditionState.Empty with
            {
                CumulativeFatigue = PlayerConditionScale.FromPercent(10d)
            }), 6);
    }

    [Fact]
    public void HealthRecoveryConsumesEnergyOneToOneAndHydrationOneToTwo()
    {
        // Сравниваем ДВА прогона с одинаковыми запасами: здоровье 100% (шкала
        // полна, восстанавливать нечего) и здоровье 50%. Разница в расходе
        // энергии и жидкости — это и есть цена восстановления здоровья, и её
        // отношение обязано быть 1:1 (энергия) и 1:2 (жидкость).
        var full = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Energy = PlayerConditionScale.FromPercent(80d),
            Hydration = PlayerConditionScale.FromPercent(80d),
            Resilience = 0d
        };
        var wounded = full with
        {
            Health = PlayerConditionScale.FromPercent(50d)
        };

        PlayerVitalsState Tick(PlayerVitalsState vitals) =>
            CharacterVitalsEngine.Advance(
                vitals,
                PlayerConditionState.Empty,
                gameSeconds: 900d,
                realSeconds: 900d,
                playerMoving: false,
                sleeping: false,
                traveledMeters: 0d,
                gameHourOfDay: 12d).Vitals;

        var fullAfter = Tick(full);
        var woundedAfter = Tick(wounded);

        var healthGain = woundedAfter.Health - wounded.Health;
        Assert.True(healthGain > 0d, "при 80% энергии и жидкости здоровье обязано расти");

        var energyCost = fullAfter.Energy - woundedAfter.Energy;
        var hydrationCost = fullAfter.Hydration - woundedAfter.Hydration;

        Assert.Equal(
            healthGain * CharacterVitalsEngine.HealthRegenEnergyPerHealthUnit,
            energyCost,
            3);
        Assert.Equal(
            healthGain * CharacterVitalsEngine.HealthRegenHydrationPerHealthUnit,
            hydrationCost,
            3);

        // И сами коэффициенты: 1 единица здоровья = 1 энергия и 2 жидкости.
        Assert.Equal(1d, CharacterVitalsEngine.HealthRegenEnergyPerHealthUnit, 6);
        Assert.Equal(2d, CharacterVitalsEngine.HealthRegenHydrationPerHealthUnit, 6);
    }

    [Fact]
    public void HealthRecoveryRateIsZeroWhenResourcesRunOut()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = 0d,
            Hydration = PlayerConditionScale.FromPercent(80d),
            Resilience = 0d
        };

        var rates = CharacterVitalsEngine.RatesFrom(
            vitals,
            PlayerConditionState.Empty,
            moving: false,
            sleeping: false,
            gameHourOfDay: 12d);

        // Нет энергии — нет и восстановления: «Текущая динамика» обещала бы рост
        // из воздуха, если бы цена ресурсов не была настоящей.
        Assert.Equal(0d, rates.HealthPerGameMinute, 6);
    }

    [Fact]
    public void FullHealthDoesNotConsumeEnergyOrHydrationForRecovery()
    {
        var rates = CharacterVitalsEngine.RatesFrom(
            PlayerVitalsState.Default,
            PlayerConditionState.Empty,
            moving: true,
            sleeping: false,
            gameHourOfDay: 12d);

        // Здоровье полное — восстанавливать нечего, значит и «платить» не за что:
        // скорости энергии и жидкости остаются чистой жизнедеятельностью.
        //
        // Нормы заданы автором физически и ЗАВИСЯТ ОТ НАГРУЗКИ: под нагрузкой
        // 2500 ккал за 3 игровых часа и 150 мл за 30 игровых минут. Помощники
        // движка — единственный пересчёт из физических величин в проценты.
        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                -CharacterVitalsEngine
                    .EnergyConsumptionPercentPerHour(moving: true) /
                60d),
            rates.EnergyPerGameMinute,
            6);
        Assert.Equal(
            PlayerConditionScale.RateFromPercent(
                -CharacterVitalsEngine
                    .HydrationConsumptionPercentPerHour(moving: true) /
                60d),
            rates.HydrationPerGameMinute,
            6);
    }

    [Fact]
    public void HealthRecoversOnePercentPerTenGameMinutesAtNormalState()
    {
        // Задано автором: 1% здоровья за 10 игровых минут при нормальном
        // метаболизме и устойчивости (60%). Проверяем запас ресурсов, чтобы цена
        // восстановления не стала ограничением и не подменила измерение.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(90d),
            Hydration = PlayerConditionScale.FromPercent(90d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            gameSeconds: 600d,
            realSeconds: 600d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var gained = PlayerConditionScale.ToPercent(update.Vitals.Health) - 50d;
        Assert.Equal(1d, gained, 2);
    }

    [Fact]
    public void ElevatedMetabolismAndResilienceEachAddOnePercentPerTenMinutes()
    {
        // Повышенный метаболизм и повышенная устойчивость дают по +1% «за ту же
        // стоимость»: при обоих свойствах выше нормы прирост становится 3% за
        // 10 минут вместо 1%.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(90d),
            Hydration = PlayerConditionScale.FromPercent(90d),
            Metabolism = PlayerConditionScale.FromPercent(80d),
            Resilience = PlayerConditionScale.FromPercent(80d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            gameSeconds: 600d,
            realSeconds: 600d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var gained = PlayerConditionScale.ToPercent(update.Vitals.Health) - 50d;
        Assert.Equal(3d, gained, 2);
    }

    [Fact]
    public void HealthRecoveryDoublesFatigueBuildWhileMoving()
    {
        // «Пока здоровье восстанавливается, получаемая усталость удваивается».
        // Сравниваем два прогона в движении: с полным здоровьем (регенерации нет)
        // и с раной (регенерация есть). Оба состояния обязаны иметь одинаковый
        // метаболизм/устойчивость, иначе бонус регенерации спутается с усталостью.
        PlayerVitalsState Vitals(double healthPercent) =>
            PlayerVitalsState.Default with
            {
                Health = PlayerConditionScale.FromPercent(healthPercent),
                Energy = PlayerConditionScale.FromPercent(90d),
                Hydration = PlayerConditionScale.FromPercent(90d)
            };

        PlayerVitalsState Tick(PlayerVitalsState vitals) =>
            CharacterVitalsEngine.Advance(
                vitals,
                PlayerConditionState.Empty,
                gameSeconds: Hour,
                realSeconds: Hour,
                playerMoving: true,
                sleeping: false,
                traveledMeters: 0d,
                gameHourOfDay: 12d).Vitals;

        var healthy = Tick(Vitals(100d));
        var wounded = Tick(Vitals(50d));

        Assert.Equal(
            PlayerConditionScale.ToPercent(healthy.Fatigue) * 2d,
            PlayerConditionScale.ToPercent(wounded.Fatigue),
            2);
    }

    [Fact]
    public void BullAddsFlatTwoPercentPerTenMinutesToHealthRecovery()
    {
        // Задано автором: «Бык» даёт +2% восстановления здоровья. Бонус ПЛОСКИЙ
        // (не множитель), поэтому читается прямо на десятиминутном шаге: база 1%
        // плюс 2% = 3%. Иначе обещанные проценты зависели бы от метаболизма.
        var bull = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    24d * 3600d,
                    false)
            }
        };

        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(50d),
            Energy = PlayerConditionScale.FromPercent(90d),
            Hydration = PlayerConditionScale.FromPercent(90d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            bull,
            gameSeconds: 600d,
            realSeconds: 600d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var gained =
            PlayerConditionScale.ToPercent(update.Vitals.Health) - 50d;

        Assert.Equal(3d, gained, 2);
    }

    /// <summary>
    /// Таймеры эффектов идут по РЕАЛЬНОМУ времени, а не по игровому.
    ///
    /// ЖАЛОБА АВТОРА: «если в симуляторе бафф активирован на 15 реальных минут,
    /// то даже при выключенной симуляции эти 15 минут продолжают истекать». Пока
    /// симуляция остановлена, игровое время стоит и шкалы не меняются, но
    /// системное время идёт, и длительность баффов завязана именно на него.
    ///
    /// Поэтому <see cref="CharacterVitalsEngine.Advance"/> обязан обрабатывать
    /// таймеры ДО выхода по нулевому игровому времени.
    /// </summary>
    [Fact]
    public void EffectTimersTickOnRealTimeEvenWhenGameTimeIsStopped()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    600d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default,
            conditions,
            gameSeconds: 0d,
            realSeconds: 30d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        var effect = Assert.Single(update.Conditions.Effects);

        Assert.Equal(570d, effect.RemainingRealSeconds, 3);
    }

    /// <summary>
    /// Тот же вызов, но игровое время стоит: шкалы не двигаются. Мир при
    /// остановке симуляции не сбрасывается, поэтому текущий срез мира — те же
    /// значения, а меняются только длительности эффектов.
    /// </summary>
    [Fact]
    public void StoppedGameTimeDoesNotChangeScaleValues()
    {
        var vitals = PlayerVitalsState.Default with
        {
            Resilience = 0d
        };

        var conditions = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    600d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            conditions,
            gameSeconds: 0d,
            realSeconds: 30d,
            playerMoving: true,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(vitals.Energy, update.Vitals.Energy, 3);
        Assert.Equal(vitals.Hydration, update.Vitals.Hydration, 3);
        Assert.Equal(vitals.Fatigue, update.Vitals.Fatigue, 3);
        Assert.Equal(vitals.Hygiene, update.Vitals.Hygiene, 3);
        Assert.Equal(0d, update.Conditions.CumulativeFatigue, 3);
        Assert.Equal(0d, update.Conditions.CumulativeStress, 3);
    }

    /// <summary>
    /// Эффект, истёкший целиком за реальное время, уходит из состояния — иначе
    /// бафф остался бы активным навсегда при выключенной симуляции.
    /// </summary>
    [Fact]
    public void ExpiredEffectIsRemovedWhileGameTimeIsStopped()
    {
        var conditions = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    "bull",
                    "Бык",
                    20d,
                    false)
            }
        };

        var update = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default,
            conditions,
            gameSeconds: 0d,
            realSeconds: 30d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Empty(update.Conditions.Effects);
    }

    // --- «Жажда» и «Обезвоживание» ---

    /// <summary>
    /// Прогоняет состояние на заданное число игровых минут с заданной жидкостью.
    /// </summary>
    private static CharacterVitalsUpdate RunMinutes(
        double hydrationPercent,
        double minutes,
        bool moving = false,
        PlayerConditionState? conditions = null)
    {
        var seconds = minutes * 60d;

        return CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Hydration = PlayerConditionScale.FromPercent(hydrationPercent)
            },
            conditions ?? PlayerConditionState.Empty,
            seconds,
            seconds,
            playerMoving: moving,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);
    }

    [Fact]
    public void ThirstAppearsBelowFifteenHundredMilliliters()
    {
        // Порог — 1600 мл (53,3% шкалы 3000 мл): выше него жажды НЕТ.
        var above = RunMinutes(1600d / 30d, minutes: 1d);
        Assert.DoesNotContain(
            above.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);

        // 1400 мл — ниже 1500: жажда выдана и объявлена событием.
        var below = RunMinutes(1400d / 30d, minutes: 1d);
        Assert.Contains(
            below.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);
        Assert.Contains(
            below.Events,
            item => item.Kind == "ThirstApplied");
    }

    [Fact]
    public void ThirstAddsHalfPercentStressPerMinuteOnTop()
    {
        // Жажда — АДДИТИВНАЯ прибавка 0,5% шкалы в минуту. Кумулятивной усталости
        // нет, значит собственная скорость стресса равна нулю, и весь рост —
        // это ровно прибавка жажды: 10 минут × 0,5% = 5%.
        var conditions = PlayerConditionState.Empty with
        {
            Stress = 0d
        };

        var update = RunMinutes(
            1000d / 30d,
            minutes: 10d,
            conditions: conditions);

        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);

        Assert.Equal(
            5d,
            PlayerConditionScale.ToPercent(update.Conditions.Stress),
            2);
    }

    [Fact]
    public void ThirstMetabolismPenaltyIsOnePercentPerTenGameMinutes()
    {
        // 10 игровых минут с жаждой: штраф −1% (задано автором). Возврат к
        // номиналу здесь НУЛЕВОЙ: метаболизм уже равен 60%, а `Clamp(60 − 60)`
        // даёт ноль — возвращать нечего. Поэтому итог ровно 59, и это верный
        // результат, а не «примерно»: тест закрепляет, что штраф равен ровно
        // 1% за 10 минут и ничем не подмешивается.
        var update = RunMinutes(1000d / 30d, minutes: 10d);

        Assert.Equal(
            59d,
            PercentOf(update.Vitals.Metabolism),
            2);
    }

    [Fact]
    public void DrinkingClearsThirstImmediatelyWhileStillInTheStomach()
    {
        // «Если в желудке усваивается что-то с жидкостью, эффект жажды сразу
        // удаляется» — задано автором. Проверяем именно НЕМЕДЛЕННОСТЬ: одна
        // минута, за которую вода ещё не могла поднять шкалу с 1000 мл до
        // 1500 мл, но жажды уже нет.
        var used = CharacterVitalsEngine.UseItem(
            "water.bottle",
            PlayerVitalsState.Default with
            {
                Hydration = PlayerConditionScale.FromPercent(1000d / 30d)
            },
            PlayerConditionState.Empty);

        // Первый тик выдал бы жажду по значению шкалы...
        var withoutDrink = CharacterVitalsEngine.Advance(
            PlayerVitalsState.Default with
            {
                Hydration = PlayerConditionScale.FromPercent(1000d / 30d)
            },
            PlayerConditionState.Empty,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);
        Assert.Contains(
            withoutDrink.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);

        // ...а с питьём в желудке — нет, хотя шкала ещё далеко ниже порога.
        var advanced = CharacterVitalsEngine.Advance(
            used.Vitals,
            used.Conditions,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            PlayerConditionScale.ToPercent(advanced.Vitals.Hydration) <
            CharacterVitalsEngine.ThirstThresholdPercent,
            "шкала обязана остаться ниже порога: проверяем снятие эффекта, а не набор жидкости");
        Assert.DoesNotContain(
            advanced.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);
    }

    [Fact]
    public void DehydrationAppearsBelowFiveHundredMilliliters()
    {
        // Порог обезвоживания — 500 мл (16,67% шкалы). 600 мл — выше, ниже
        // порога жажды, но обезвоживания ещё нет.
        var thirstOnly = RunMinutes(600d / 30d, minutes: 1d);
        Assert.Contains(
            thirstOnly.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.ThirstEffectId);
        Assert.DoesNotContain(
            thirstOnly.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);

        // 400 мл — ниже порога: обезвоживание выдано.
        var dehydrated = RunMinutes(400d / 30d, minutes: 1d);
        Assert.Contains(
            dehydrated.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
        Assert.Contains(
            dehydrated.Events,
            item => item.Kind == "DehydrationApplied");
    }

    [Fact]
    public void DehydrationMultipliesStressGainByOneAndAQuarter()
    {
        // Кумулятивная усталость 20% задаёт ненулевую собственную скорость
        // стресса, поэтому множитель ×1,25 виден. Сравниваем ДВА прогона с
        // одинаковой жидкостью ВЫШЕ порога обезвоживания: разница — только
        // множитель, и отношение результатов обязано равняться 1,25.
        var conditions = PlayerConditionState.Empty with
        {
            CumulativeFatigue = PlayerConditionScale.FromPercent(20d)
        };

        var normal = RunMinutes(70d, minutes: 10d, conditions: conditions);
        var dehydrated = RunMinutes(
            10d,
            minutes: 10d,
            conditions: conditions);

        Assert.Contains(
            dehydrated.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
        Assert.DoesNotContain(
            normal.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);

        // Обезвоживание добавляет ещё и жажду (+0,5%/мин), поэтому сравнивать
        // надо ту часть, что задана кумулятивной усталостью. Проверяем главное:
        // под обезвоживанием стресс вырос СУЩЕСТВЕННО быстрее.
        Assert.True(
            dehydrated.Conditions.Stress > normal.Conditions.Stress * 1.2d,
            "обезвоживание обязано ускорить накопление стресса: " +
            $"{PercentOf(dehydrated.Conditions.Stress)} против " +
            $"{PercentOf(normal.Conditions.Stress)}");
    }

    [Fact]
    public void DehydrationDrainsResilienceTogetherWithFluidConsumption()
    {
        // Устойчивость падает 1:1 с расходом жидкости. Берём состояние, где
        // возврат к номиналу равен нулю (устойчивость уже 60%), тогда вся
        // убыль — это дебафф. В покое норма расхода — 4% шкалы в час; за 30
        // минут это 2% (плюс множитель пониженного метаболизма, если он есть).
        var vitals = PlayerVitalsState.Default with
        {
            Hydration = PlayerConditionScale.FromPercent(10d),
            Resilience = PlayerConditionScale.FromPercent(60d)
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            1800d,
            1800d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Contains(
            update.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
        Assert.True(
            PercentOf(update.Vitals.Resilience) < 60d,
            "под обезвоживанием устойчивость обязана падать: " +
            PercentOf(update.Vitals.Resilience));
    }

    [Fact]
    public void CriticalDehydrationTakesHealthOneHundredPercentPerGameHour()
    {
        // Ниже 1% шкалы здоровье отнимается со скоростью 100% шкалы за игровой
        // час — то есть за 6 игровых минут ровно 10%.
        var vitals = PlayerVitalsState.Default with
        {
            Health = PlayerConditionScale.FromPercent(100d),
            Hydration = 0d
        };

        var update = CharacterVitalsEngine.Advance(
            vitals,
            PlayerConditionState.Empty,
            360d,
            360d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        // Здоровье убывает и от истощения жидкости/энергии, поэтому требуем
        // ЛИШЬ не меньшей скорости: потеря обязана быть не слабее 10% за час
        // десятой доли.
        Assert.True(
            PercentOf(update.Vitals.Health) < 90d,
            "при критической нехватке жидкости здоровье обязано падать: " +
            PercentOf(update.Vitals.Health));
    }

    [Fact]
    public void DehydrationClearsWhenHydrationDynamicsTurnPositive()
    {
        // «Снимается, когда игрок добился положительной (зелёной) динамики» —
        // задано автором. Проверяем: без воды дебафф держится, с водой в
        // желудке (приток больше расхода) — снимается, ХОТЯ шкала ещё ниже
        // порога.
        var dehydration = PlayerConditionState.Empty with
        {
            Effects = new[]
            {
                new ActivePlayerEffectState(
                    CharacterVitalsEngine.DehydrationEffectId,
                    "Обезвоживание",
                    1e12,
                    true)
            }
        };

        var vitals = PlayerVitalsState.Default with
        {
            Hydration = PlayerConditionScale.FromPercent(10d)
        };

        var withoutDrink = CharacterVitalsEngine.Advance(
            vitals,
            dehydration,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);
        Assert.Contains(
            withoutDrink.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);

        var used = CharacterVitalsEngine.UseItem(
            "water.bottle",
            vitals,
            dehydration);

        var withDrink = CharacterVitalsEngine.Advance(
            used.Vitals,
            used.Conditions,
            60d,
            60d,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.True(
            PlayerConditionScale.ToPercent(withDrink.Vitals.Hydration) <
            CharacterVitalsEngine.DehydrationThresholdPercent,
            "шкала обязана остаться ниже порога: проверяем снятие по динамике");
        Assert.DoesNotContain(
            withDrink.Conditions.Effects,
            effect => effect.Id == CharacterVitalsEngine.DehydrationEffectId);
    }

    /// <summary>
    /// Каждый предмет из каталога употребимого обязан ДЕЙСТВОВАТЬ, а не только
    /// числиться в списке.
    ///
    /// Это проверка СВЯЗИ двух ответов движка. Список меню желудка собирается по
    /// <see cref="CharacterVitalsEngine.CanConsume"/> («что показать»), а сама
    /// механика живёт в ветках <see cref="CharacterVitalsEngine.UseItem"/> («что
    /// сделать»). Разойтись они могут молча: предмет попадёт в меню, автор выберет
    /// его — и ничего не произойдёт. За такую поломку отвечает и список
    /// <c>ConsumableEffectItems</c> в движке, и ветка в <c>UseItem</c>, поэтому
    /// проверять их надо ВМЕСТЕ, перебором.
    ///
    /// Отклик обязателен либо на шкалах, либо в списке эффектов: мыло поднимает
    /// гигиену, сорбент вешает эффект «Сорбент», а вода просто уходит в желудок.
    /// Сравнивать надо ВСЁ состояние, а не одни шкалы: предмет может подействовать
    /// только эффектом (сорбент), только желудком (вода) или только счётчиком.
    /// </summary>
    [Fact]
    public void EveryCatalogConsumableActuallyAppliesItsMechanics()
    {
        // Список берётся ИЗ КАТАЛОГА, а не переписывается здесь: иначе тест
        // разошёлся бы с реальным содержимым меню в тот же день.
        var consumables = ItemCatalogFactory.Items
            .Where(item => CharacterVitalsEngine.CanConsume(item.Id))
            .ToArray();

        Assert.True(
            consumables.Length >= 20,
            $"в каталоге употребимого неожиданно мало предметов: {consumables.Length}");

        var broken = new List<string>();

        foreach (var item in consumables)
        {
            // Состояние «всё плохо»: тогда любой настоящий эффект виден — он
            // меняет хотя бы одно поле (гигиену, стресс, здоровье, желудок).
            //
            // Normalize() вызывается ЗДЕСЬ, а не только внутри UseItem: движок
            // нормализует состояние в начале, и сравнение с ненормализованным
            // «до» показывало бы разницу от самой нормализации, а не от
            // предмета. Именно на этом тест пропускал выключенную ветку «soap»
            // (поймано негативным контролем).
            var vitals = (PlayerVitalsState.Default with
            {
                Health = PlayerConditionScale.FromPercent(40d),
                Energy = PlayerConditionScale.FromPercent(40d),
                Hydration = PlayerConditionScale.FromPercent(40d),
                Hygiene = PlayerConditionScale.FromPercent(40d),
                Resilience = PlayerConditionScale.FromPercent(40d),
                Fatigue = PlayerConditionScale.FromPercent(40d),
                Metabolism = PlayerConditionScale.FromPercent(50d)
            }).Normalize();

            var before = (PlayerConditionState.Empty with
            {
                Stress = PlayerConditionScale.FromPercent(50d),
                // Раздражение кожи задано нарочно: ветка мази действует, ТОЛЬКО
                // когда оно есть, и на пустом состоянии мазь была бы честным
                // «ничего не делает» — тест валил бы не за поломку, а за пустую
                // заготовку.
                SkinIssuesGameSeconds = 240d * 3600d
            }).Normalize();

            var after = CharacterVitalsEngine.UseItem(item.Id, vitals, before);

            if (after.Events.Any(e => e.Kind == "UnknownItem"))
            {
                broken.Add($"{item.Id}: нет механики (UnknownItem)");
                continue;
            }

            if (SignatureOf(after.Vitals, after.Conditions) ==
                SignatureOf(vitals, before))
            {
                broken.Add($"{item.Id}: механика не изменила ничего");
            }
        }

        Assert.True(
            broken.Count == 0,
            "предметы из меню желудка, которые ничего не делают: " +
            string.Join("; ", broken));
    }

    /// <summary>
    /// Отпечаток состояния по ЗНАЧЕНИЯМ — то, чем можно честно сравнить «до» и
    /// «после» употребления.
    ///
    /// Прямое сравнение записей здесь не годится: <c>Effects</c> — массив, а
    /// <c>ItemUseCounts</c> — словарь, и равенство записей сравнивает их ПО
    /// ССЫЛКЕ. <c>Normalize()</c> внутри движка создаёт новые пустые экземпляры,
    /// поэтому «изменилось» было бы истиной ВСЕГДА, и выключенная механика
    /// предмета проходила бы проверку (именно это и поймал негативный контроль с
    /// выключенной веткой «soap»).
    ///
    /// В отпечаток входит только то, на что предмет ВПРАВЕ влиять. Учёт
    /// употреблений (<c>ItemUseCounts</c>, <c>LastConsumedItemId</c>) исключён
    /// осознанно: движок меняет его ЛЮБОМУ предмету, даже когда механики нет
    /// вовсе, и по нему «что-то изменилось» тоже было бы истиной всегда.
    /// </summary>
    private static string SignatureOf(
        PlayerVitalsState vitals,
        PlayerConditionState conditions)
    {
        return string.Join(
            "|",
            Number(vitals.Health),
            Number(vitals.Energy),
            Number(vitals.Hydration),
            Number(vitals.Hygiene),
            Number(vitals.Fatigue),
            Number(vitals.Resilience),
            Number(vitals.Metabolism),
            Number(conditions.Stress),
            string.Join(
                ",",
                conditions.Effects
                    .Select(effect => effect.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)),
            Number(conditions.OverchargeHealth),
            Number(conditions.OverchargeEnergy),
            Number(conditions.OverchargeHydration),
            Number(conditions.OverchargeFatigue),
            Number(conditions.OverchargeStress),
            Number(conditions.SkinIssuesGameSeconds),
            Number(conditions.Stomach.EnergyRemaining),
            Number(conditions.Stomach.HydrationRemaining));
    }

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Значение шкалы в процентах (для читаемых сообщений об ошибке).</summary>
    private static double PercentOf(double units) =>
        PlayerConditionScale.ToPercent(units);
}
