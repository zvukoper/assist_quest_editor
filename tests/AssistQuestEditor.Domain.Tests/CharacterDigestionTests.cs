using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Пищеварение: еда и питьё восстанавливают шкалы ПОСТЕПЕННО, а пропускная
/// способность ДЕЛИТСЯ между тем, что лежит.
///
/// Тесты держат ФИЗИЧЕСКИЕ величины автора, а не «эталонные минуты» прежней
/// модели:
///   • шкала энергии — 5000 ккал (10000 единиц хранения), жидкость — 3000 мл;
///   • объём пищеварения — 2 литра (задано автором: «с 1000 до 2000»);
///   • базовая пропускная способность — полный объём за 2 игровых часа, то есть
///     1000 мл/ч сухой пищи;
///   • ЖИДКОСТЬ усваивается 1,35 литра в час — это ФИЗИОЛОГИЯ, а не доля шкалы,
///     поэтому на трёхлитровой шкале та же ставка читается как 45% шкалы в час
///     (4500 единиц в час);
///   • при нормальной нагрузке расход энергии 16,7%, жидкости 10% в час.
///
/// ГЛАВНОЕ ОТЛИЧИЕ ОТ ПРЕЖНЕЙ МОДЕЛИ — ставка НЕ своя у каждой порции. Она
/// выводится из ОБЩЕЙ пропускной способности и «веса» каждой порции, поэтому
/// смесь продуктов удлиняет срок КАЖДОЙ из них, а вода обгоняет сухую еду.
///
/// Числовая ошибка здесь не видна ни сборке, ни статической проверке, поэтому
/// проверять надо расчётом — и, что важнее, суммой за ВСЁ переваривание: объём
/// обязан совпасть с порцией, иначе игрок теряет еду.
/// </summary>
public sealed class CharacterDigestionTests
{
    private const double Minute = 60d;
    private const double Hour = 3600d;

    /// <summary>Объём всей шкалы в единицах хранения (как и всё состояние).</summary>
    private const double Scale = PlayerConditionScale.Maximum;

    /// <summary>Сколько литров вмещает полная шкала жидкости.</summary>
    private const double HydrationScaleLiters =
        CharacterDigestion.HydrationScaleMilliliters / 1000d;

    /// <summary>
    /// Кладёт в пищеварение порцию с ЗАДАННЫМИ физическими величинами: объём в
    /// миллилитрах, вода и калории напрямую. Тесты говорят на языке документа
    /// (миллилитры, килокалории), а не единиц шкалы.
    /// </summary>
    private static StomachContents Begin(
        double milliliters,
        double waterMilliliters,
        double kilocalories,
        bool elevated = false,
        bool reduced = false,
        string itemId = "test.item") =>
        CharacterDigestion.Begin(
            StomachContents.Empty,
            itemId,
            milliliters,
            waterMilliliters,
            kilocalories,
            elevated,
            reduced);

    /// <summary>Порция ЕДЫ «как с этикетки»: объём и калорийность, воды нет.</summary>
    private static StomachContents BeginFood(
        double milliliters,
        double kilocalories,
        bool elevated = false,
        bool reduced = false) =>
        Begin(milliliters, 0d, kilocalories, elevated, reduced, "test.food");

    /// <summary>Порция ПИТЬЯ «как с этикетки»: объём и вода равны, калорий нет.</summary>
    private static StomachContents BeginWater(
        double milliliters,
        bool elevated = false,
        bool reduced = false)
    {
        var contents = Begin(
            milliliters,
            milliliters,
            0d,
            elevated,
            reduced,
            "test.water");

        // Инвариант фикстуры: у чистой воды объём и вода совпадают, поэтому её
        // доля воды равна единице. Если это сломается, проверки ниже станут
        // бессмысленными — падать надо здесь.
        Assert.Equal(1d, contents.Portions[0].WaterContent, 6);

        return contents;
    }

    /// <summary>Прогоняет переваривание до пустого пищеварения и складывает прибавки.</summary>
    private static (double Energy, double Hydration, double Seconds) Drain(
        StomachContents contents)
    {
        var energy = 0d;
        var hydration = 0d;
        var seconds = 0d;

        // Шаг в минуту: переваривание не зависит от размера шага (ставка
        // постоянна), поэтому длительность набирается точно.
        while (!contents.IsEmpty && seconds < 200d * Hour)
        {
            var step = CharacterDigestion.Advance(contents, Minute);
            energy += step.EnergyGain;
            hydration += step.HydrationGain;
            seconds += Minute;
            contents = step.Contents;
        }

        return (energy, hydration, seconds);
    }

    // --- Вместимость ---

    [Fact]
    public void PortionFractionIsShareOfTwoLitreStomach()
    {
        // 500 г — четверть двухлитрового пищеварения, 2000 г — всё.
        Assert.Equal(0.25d, CharacterDigestion.PortionFraction(500d), 6);
        Assert.Equal(1d, CharacterDigestion.PortionFraction(2000d), 6);

        // Больше двух литров пищеварение не вмещает: доля ограничена единицей.
        Assert.Equal(1d, CharacterDigestion.PortionFraction(5000d), 6);

        // Нечего переваривать — нулевая доля.
        Assert.Equal(0d, CharacterDigestion.PortionFraction(0d), 6);
        Assert.Equal(0d, CharacterDigestion.PortionFraction(-10d), 6);
    }

    [Fact]
    public void StomachCapacityIsTwoLitresAndSetsThroughput()
    {
        // Число задано автором и является и «сколько влезает», и источником
        // пропускной способности: полный объём уходит за 2 часа.
        Assert.Equal(2d, CharacterDigestion.StomachVolumeLiters, 6);
        Assert.Equal(2000d, CharacterDigestion.CapacityMilliliters, 6);
        Assert.Equal(
            1d,
            CharacterDigestion.DryThroughputLitersPerHour,
            6);
        Assert.Equal(
            1000d,
            CharacterDigestion.DryThroughputMillilitersPerSecond * Hour,
            6);
    }

    // --- Пропускная способность ---

    [Fact]
    public void FullFoodStomachEmptiesInTwoGameHours()
    {
        // Полный объём пищеварения сухой пищей уходит ровно за 2 игровых часа —
        // это МЕРА автора, и она сохранена как эталон. Мерой служит ОБЪЁМ, а не
        // калории: таймер обязан отвечать занимаемому месту.
        var food = BeginFood(CharacterDigestion.CapacityMilliliters, 5000d);

        Assert.Equal(
            CharacterDigestion.FoodStomachEmptyHours * Hour,
            food.Portions[0].RemainingGameSeconds,
            0.05d);

        // 1000 мл/ч сухого: 2000 мл за 2 часа.
        Assert.Equal(
            CharacterDigestion.DryThroughputMillilitersPerSecond,
            food.Portions[0].PerGameSecond,
            6);
        Assert.Equal(
            1000d,
            food.Portions[0].PerGameSecond * Hour,
            3);
    }

    [Fact]
    public void WaterOutpacesDryFoodByTheConfiguredRatio()
    {
        // Отношение «вода против сухой еды» выведено из норм автора (1,35 л/ч
        // против 1 л/ч), а не назначено «в 4–5 раз», как в справочном документе:
        // иначе вода получила бы 5 л/ч и переписала согласованную физиологию.
        Assert.Equal(
            CharacterDigestion.HydrationAbsorptionLitersPerHour /
            CharacterDigestion.DryThroughputLitersPerHour,
            CharacterDigestion.WaterContentSpeedFactor,
            6);

        var water = BeginWater(1000d);
        var food = BeginFood(1000d, 2500d);

        Assert.True(
            water.Portions[0].PerGameSecond > food.Portions[0].PerGameSecond,
            "вода обязана уходить быстрее сухой еды того же объёма");
    }

    [Fact]
    public void LonePortionGetsTheWholeThroughputRegardlessOfSize()
    {
        // СЛЕДСТВИЕ ФОРМУЛЫ, которое надо знать: у единственной порции ставка
        // равна ПОЛНОЙ пропускной способности (она забирает весь лимит),
        // поэтому срок пропорционален объёму, а не одинаков. Иначе маленький
        // перекус на 5 г висел бы в пищеварении столько же, сколько обед, и
        // «съел батончик — динамика красная два часа».
        var small = BeginFood(500d, 1250d);
        var large = BeginFood(2000d, 5000d);

        Assert.Equal(
            small.Portions[0].PerGameSecond,
            large.Portions[0].PerGameSecond,
            6);

        Assert.Equal(
            small.Portions[0].RemainingGameSeconds * 4d,
            large.Portions[0].RemainingGameSeconds,
            0.05d);

        // В МИНУТАХ это ровно 120 игровых минут для полного объёма — то же, что
        // показывает монитор.
        Assert.Equal(
            CharacterDigestion.FoodStomachEmptyHours * 60d,
            large.Portions[0].RemainingGameMinutes,
            0.01d);
    }

    [Fact]
    public void TotalAbsorptionNeverExceedsThroughput()
    {
        // ИНВАРИАНТ МОДЕЛИ: за час пищеварение не пропустит больше, чем его
        // пропускная способность. Сумма ставок ограничена сверху средней
        // «лёгкостью» состава, и это то, что делает «запас топлива» конечным:
        // игрок не может съесть ведро еды и получить мгновенный эффект.
        var contents = BeginWater(1000d);
        contents = CharacterDigestion.Begin(
            contents,
            "test.food",
            1000d,
            0d,
            2500d,
            false,
            false);

        var totalPerHour = 0d;
        foreach (var portion in contents.Portions)
            totalPerHour += portion.PerGameSecond * Hour;

        var cap =
            CharacterDigestion.DryThroughputMillilitersPerSecond *
            Hour *
            CharacterDigestion.WaterContentSpeedFactor;

        Assert.True(
            totalPerHour <= cap + 1e-6d,
            $"пищеварение пропускает {totalPerHour} мл/ч при пределе {cap} мл/ч");
    }

    // --- Смесь: то, ради чего модель переписана ---

    [Fact]
    public void MixedContentSlowsDownEveryPortion()
    {
        // «Чем больше намешано, тем дольше каждая усваивается» — главное
        // требование документа и автора. Одна и та же порция еды уходит
        // МЕДЛЕННЕЕ, если рядом лежит вода: часть общей пропускной способности
        // забирает она.
        var alone = BeginFood(1000d, 2500d);
        var aloneSeconds = alone.Portions[0].RemainingGameSeconds;

        var withWater = BeginWater(1000d);
        withWater = CharacterDigestion.Begin(
            withWater,
            "test.food",
            1000d,
            0d,
            2500d,
            false,
            false);

        var food = withWater.Portions[1];

        Assert.True(
            food.RemainingGameSeconds > aloneSeconds,
            $"в смеси еда обязана усваиваться ДОЛЬШЕ, а вышло " +
            $"{food.RemainingGameSeconds} против {aloneSeconds}");

        // И у воды ровно то же: смесь замедляет обе стороны.
        var waterAlone = BeginWater(1000d);

        Assert.True(
            withWater.Portions[0].RemainingGameSeconds >
            waterAlone.Portions[0].RemainingGameSeconds,
            "вода рядом с едой обязана уходить медленнее, чем вода сама по себе");
    }

    [Fact]
    public void WaterTakesTheLargerShareOfThroughput()
    {
        // Доля пропускной способности пропорциональна объёму и «лёгкости»:
        // вода легче, поэтому в смеси с равным по объёму куском еды она
        // забирает больше. Именно это имитирует «вода усваивается быстрее».
        var contents = BeginWater(1000d);
        contents = CharacterDigestion.Begin(
            contents,
            "test.food",
            1000d,
            0d,
            2500d,
            false,
            false);

        Assert.True(
            contents.Portions[0].PerGameSecond >
            contents.Portions[1].PerGameSecond,
            "в смеси равных объёмов вода обязана пропускаться быстрее еды");
    }

    [Fact]
    public void UnusedCapacityIsRedistributedInsteadOfWasted()
    {
        // НЕСТЫКОВКА ДОКУМЕНТА, устранённая в реализации. Документ предлагал
        // «остаток лимита сгорает»: если доля порции не выбрана, она теряется.
        // Здесь лимит ВСЕГДА выбирается целиком и распределяется между
        // оставшимися порциями, поэтому еда не пропадает «впустую».
        var contents = BeginWater(200d);
        contents = CharacterDigestion.Begin(
            contents,
            "test.food",
            1800d,
            0d,
            4500d,
            false,
            false);

        var expected = 0d;
        var massTotal = 0d;
        foreach (var portion in contents.Portions)
            massTotal += portion.MassRemaining;

        foreach (var portion in contents.Portions)
        {
            var lightness =
                1d + (CharacterDigestion.WaterContentSpeedFactor - 1d) *
                portion.WaterContent;

            expected +=
                CharacterDigestion.DryThroughputMillilitersPerSecond *
                portion.MassRemaining * lightness / massTotal;
        }

        var sum = 0d;
        foreach (var portion in contents.Portions)
            sum += portion.PerGameSecond;

        Assert.Equal(expected, sum, 6);
    }

    // --- Физиологические нормы автора ---

    [Fact]
    public void FullDrinkAbsorbsAtTheMeasuredRate()
    {
        // Ставка ФИЗИОЛОГИЧЕСКАЯ и от размера шкалы не зависит: 1,35 л/ч. Полный
        // объём пищеварения (2 л) воды уходит по этой норме: 2000 / 1350 ≈
        // 1,48 игрового часа.
        var drink = BeginWater(CharacterDigestion.CapacityMilliliters);

        Assert.Equal(
            CharacterDigestion.CapacityMilliliters /
            (CharacterDigestion.HydrationAbsorptionLitersPerHour * 1000d / Hour),
            drink.Portions[0].RemainingGameSeconds,
            1d);

        // Ставка — в единицах шкалы: «1,35 л/ч» переводится в единицы той же
        // формулой, что и остаток, поэтому 1350 мл в час читаются как 45% шкалы
        // в час. Порция на 2 литра уходит за ≈1,48 часа ровно потому, что её
        // ставка отвечает физиологии, а не калибровке сухой пищи.
        Assert.Equal(
            CharacterDigestion.HydrationUnits(1350d),
            drink.HydrationPerGameSecond * Hour,
            3);

        // ГЛАВНОЕ: два литра воды уходят за время, которое считает сама норма
        // физиологии, а не калибровка сухой пищи.
        Assert.Equal(
            2000d,
            drink.Portions[0].PerGameSecond *
            drink.Portions[0].RemainingGameSeconds,
            3);
    }

    [Fact]
    public void DrinkingOutpacesConsumptionUnderReducedMetabolism()
    {
        // При пониженном метаболизме литр всё равно даёт 3000 единиц в час
        // против расхода 360 единиц в час (4% шкалы в час в покое ×0,9), то
        // есть запас почти десятикратный: динамика обязана быть положительной
        // в любом нормальном состоянии.
        var stomach = BeginWater(1000d, reduced: true);

        var netPerHour =
            stomach.HydrationPerGameSecond * Hour -
            PlayerConditionScale.FromPercent(
                CharacterVitalsEngine
                    .HydrationConsumptionPercentPerHour(moving: false)) *
            0.9d;

        Assert.True(
            netPerHour > 0d,
            $"при пониженном метаболизме питьё обязано давать положительную " +
            $"динамику, а даёт {netPerHour} ед./ч");
    }

    [Fact]
    public void DrinkOutpacesItsOwnConsumption()
    {
        // 1,35 литра в час = 4500 ед./ч обязаны быть БОЛЬШЕ расхода шкалы
        // жидкости (10% шкалы в час под нагрузкой = 1000 ед./ч). Иначе выпитая
        // вода не давала бы положительной динамики — именно на это жаловался
        // автор.
        var absorptionPerHour =
            CharacterDigestion.HydrationAbsorptionLitersPerHour *
            1000d /
            CharacterDigestion.HydrationScaleMilliliters *
            Scale;

        var consumptionPerHour =
            PlayerConditionScale.FromPercent(
                CharacterVitalsEngine
                    .HydrationConsumptionPercentPerHour(moving: true));

        Assert.True(
            absorptionPerHour > consumptionPerHour,
            $"усвоение {absorptionPerHour} ед./ч обязано перекрывать расход " +
            $"{consumptionPerHour} ед./ч");
    }

    [Fact]
    public void FoodOutpacesItsOwnConsumption()
    {
        // ЕДА ОБЯЗАНА ПЕРЕКРЫВАТЬ СОБСТВЕННЫЙ РАСХОД. Это инвариант модели, а не
        // балансная цифра: если полный объём отдаёт меньше, чем тратит организм,
        // то даже плотный обед оставляет динамику красной.
        //
        // ЗАПАС 1,5×, а не строгое неравенство: при расходе 25%/ч под нагрузкой
        // 5000 ед./ч дают ровно двукратный запас, и проверка «строго больше 2×»
        // падала бы на равенстве, то есть валила не дефект, а свою цифру.
        var absorptionPerHour =
            Scale / CharacterDigestion.FoodStomachEmptyHours;
        var consumptionPerHour =
            PlayerConditionScale.FromPercent(
                CharacterVitalsEngine
                    .EnergyConsumptionPercentPerHour(moving: true));

        Assert.True(
            absorptionPerHour > consumptionPerHour * 1.5d,
            $"усвоение {absorptionPerHour} ед./ч обязано перекрывать расход " +
            $"{consumptionPerHour} ед./ч с запасом");
    }

    [Fact]
    public void WaterBottleGivesPositiveHydrationDynamic()
    {
        // Бутылка воды 500 мл: 500/3000 шкалы = 16,67% = 1666,7 единицы, и она
        // поступает со скоростью 4500 ед./ч против расхода 1000 ед./ч.
        var profile = CharacterConsumableCatalog.GetProfile("water.bottle");

        Assert.Equal(500d, profile.WaterMilliliters, 3);
        Assert.Equal(500d / 30d, profile.HydrationPercent, 3);
        Assert.Equal(0d, profile.EnergyPercent, 3);

        var stomach = CharacterDigestion.Begin(
            StomachContents.Empty,
            "water.bottle",
            CharacterConsumableCatalog.GetPortionMilliliters("water.bottle"),
            profile.WaterMilliliters,
            profile.Kilocalories,
            false,
            false);

        var netPerHour =
            stomach.HydrationPerGameSecond * Hour -
            PlayerConditionScale.FromPercent(
                CharacterVitalsEngine
                    .HydrationConsumptionPercentPerHour(moving: true));

        Assert.True(
            netPerHour > 0d,
            $"бутылка воды обязана давать положительную динамику, а даёт {netPerHour} ед./ч");
    }

    [Fact]
    public void EggSandwichGivesPositiveEnergyDynamic()
    {
        // Бутерброд 180 г / 320 ккал (12,9/12,9/1,1 на 100 г × 1,8). Числа
        // профиля берутся ИЗ КАТАЛОГА, а не переписываются: переписав их, тест
        // перестал бы ловить смену калорийности.
        var profile = CharacterConsumableCatalog.GetProfile("food.egg_sandwich");

        Assert.Equal(320d, profile.Kilocalories, 3);
        Assert.Equal(6.4d, profile.EnergyPercent, 3);

        var stomach = CharacterDigestion.Begin(
            StomachContents.Empty,
            "food.egg_sandwich",
            CharacterConsumableCatalog.GetPortionMilliliters("food.egg_sandwich"),
            profile.WaterMilliliters,
            profile.Kilocalories,
            false,
            false);

        var netPerHour =
            stomach.EnergyPerGameSecond * Hour -
            PlayerConditionScale.FromPercent(
                CharacterVitalsEngine
                    .EnergyConsumptionPercentPerHour(moving: true));

        Assert.True(
            netPerHour > 0d,
            $"бутерброд обязан давать положительную динамику, а даёт {netPerHour} ед./ч");
    }

    // --- Сумма порции ---

    [Fact]
    public void DigestionReturnsWholePortionAndEmptiesStomach()
    {
        var contents = Begin(1000d, 300d, 500d);
        var expectedEnergy = PlayerConditionScale.FromPercent(10d);
        var expectedHydration = PlayerConditionScale.FromPercent(10d);

        Assert.Equal(expectedEnergy, contents.EnergyRemaining, 3);
        Assert.Equal(expectedHydration, contents.HydrationRemaining, 3);

        var result = Drain(contents);

        // Объём порции обязан дойти до шкал ЦЕЛИКОМ — ни единицы не теряется.
        Assert.Equal(expectedEnergy, result.Energy, 3);
        Assert.Equal(expectedHydration, result.Hydration, 3);

        // Срок считаем по формуле, а не константой: у смеси он свой, и
        // «ожидаемое число» обязано считаться тем же правилом, что и движок.
        var lightness =
            1d + (CharacterDigestion.WaterContentSpeedFactor - 1d) *
            (300d / 1000d);

        var expectedSeconds =
            1000d /
            (CharacterDigestion.DryThroughputMillilitersPerSecond * lightness);

        // Допуск — одна минута, потому что Drain шагает МИНУТАМИ и
        // останавливается на первом пустом пищеварении: последний шаг законно
        // перескакивает границу.
        Assert.InRange(
            result.Seconds,
            expectedSeconds - Minute,
            expectedSeconds + Minute);
    }

    [Fact]
    public void EnergyArrivesGraduallyNotInstantly()
    {
        var contents = BeginFood(500d, 1250d);

        // За первую МИНУТУ пищеварение отдаёт ровно одну шестидесятую часа, то
        // есть меньше всей порции, поэтому восстановление постепенное.
        var step = CharacterDigestion.Advance(contents, Minute);

        Assert.Equal(
            contents.EnergyPerGameSecond * Minute,
            step.EnergyGain,
            4);
        Assert.True(
            step.EnergyGain < contents.EnergyRemaining,
            "за минуту не может усвоиться вся порция");
        Assert.False(step.IsEmpty);
        Assert.True(step.Contents.EnergyRemaining > 0d);
    }

    // --- Метаболизм ---

    [Fact]
    public void ElevatedMetabolismDigestsFasterAndAddsVolumeBonus()
    {
        var normal = BeginWater(1000d);
        var elevated = BeginWater(1000d, elevated: true);

        // ПРОПУСКНАЯ СПОСОБНОСТЬ строго в 2,5 раза выше: это задано автором и НЕ
        // зависит от размера порции (ставка считается из пропускной способности,
        // а не из питательности). Поэтому и объём уходит быстрее во столько же
        // раз — срок сокращается ровно в 2,5 раза.
        Assert.Equal(
            2.5d,
            elevated.Portions[0].PerGameSecond /
            normal.Portions[0].PerGameSecond,
            6);

        Assert.Equal(
            normal.Portions[0].RemainingGameSeconds / 2.5d,
            elevated.Portions[0].RemainingGameSeconds,
            4);

        // Плюс 0,5% ОТ МАКСИМУМА ШКАЛЫ к ОБЪЁМУ восстановления — ОТДЕЛЬНО от
        // скорости. У свежей порции остаток и есть весь её объём, поэтому
        // разность остатков и есть бонус; отдельная ветка расчёта тут была бы
        // вторым источником правды об одном и том же числе.
        Assert.Equal(
            PlayerConditionScale.Maximum *
            CharacterDigestion.ElevatedMetabolismVolumeBonus,
            elevated.Portions[0].HydrationRemainingUnits -
                normal.Portions[0].HydrationRemainingUnits,
            3);
    }

    [Fact]
    public void ReducedMetabolismDigestsSlowerAndTakesVolumePenalty()
    {
        var normal = BeginWater(1000d);
        var reduced = BeginWater(1000d, reduced: true);

        // У единственной порции воды ставка равна пропускной способности,
        // умноженной на её «лёгкость» (W = 1 → k = WaterContentSpeedFactor).
        var expected = CharacterDigestion.DryThroughputMillilitersPerSecond *
            CharacterDigestion.WaterContentSpeedFactor;

        Assert.Equal(expected, normal.Portions[0].PerGameSecond, 6);

        // Пропускная способность В 1,5 РАЗА МЕНЬШЕ — ровно то, что задал автор.
        Assert.Equal(
            expected / 1.5d,
            reduced.Portions[0].PerGameSecond,
            6);

        // Штраф −0,5% от максимума шкалы к ОБЪЁМУ восстановления.
        Assert.Equal(
            PlayerConditionScale.Maximum *
            CharacterDigestion.ReducedMetabolismVolumePenalty,
            normal.Portions[0].HydrationRemainingUnits -
                reduced.Portions[0].HydrationRemainingUnits,
            3);
    }

    // --- Сохранение и загрузка ---

    [Fact]
    public void StomachPortionsSurviveSaveAndLoadNormalization()
    {
        // Порции обязаны пережить Normalize (его вызывает и движок, и чтение
        // сохранения). Проверяем, что суммы остаются согласованы с порциями —
        // это и был механизм дефекта: подпись менялась, а суммы оставались.
        var contents = BeginWater(500d);
        contents = CharacterDigestion.Begin(
            contents,
            "food.apple",
            200d,
            40d,
            95d,
            false,
            false);

        var normalized = contents.Normalize();

        Assert.Equal(2, normalized.Portions.Count);

        var energySum = 0d;
        var hydrationSum = 0d;
        var volumeSum = 0d;
        foreach (var portion in normalized.Portions)
        {
            energySum += CharacterDigestion.EnergyUnits(
                portion.KilocaloriesRemaining);
            hydrationSum += CharacterDigestion.HydrationUnits(
                portion.WaterMillilitersRemaining);
            volumeSum += CharacterDigestion.PortionFraction(
                portion.MassRemaining);
        }

        Assert.Equal(normalized.EnergyRemaining, energySum, 6);
        Assert.Equal(normalized.HydrationRemaining, hydrationSum, 6);
        Assert.Equal(normalized.VolumeFraction, volumeSum, 6);
    }

    [Fact]
    public void MetabolismIsRememberedWithContentsAndRestoresRatesAfterNormalize()
    {
        // Метаболизм меняет ПРОПУСКНУЮ СПОСОБНОСТЬ, а ставки выводятся из
        // состава. Поэтому признак метаболизма хранится ВМЕСТЕ с содержимым:
        // без него загрузка сохранения пересчитала бы ставки по «нормальному»
        // метаболизму, и монитор показывал бы не то, что начисляется.
        var elevated = BeginWater(1000d, elevated: true);
        var reduced = BeginWater(1000d, reduced: true);

        Assert.True(elevated.ElevatedMetabolism);
        Assert.False(elevated.ReducedMetabolism);
        Assert.True(reduced.ReducedMetabolism);
        Assert.False(reduced.ElevatedMetabolism);

        // Пересчёт после Normalize сохраняет поправку метаболизма.
        var normalizedElevated = elevated.Normalize();
        Assert.True(normalizedElevated.ElevatedMetabolism);
        Assert.Equal(
            elevated.Portions[0].PerGameSecond,
            normalizedElevated.Portions[0].PerGameSecond,
            6);

        // Повышенный быстрее пониженного в 3,75 раза: 2,5 (быстрее нормы) × 1,5
        // (норма быстрее пониженного).
        Assert.Equal(
            2.5d * 1.5d,
            normalizedElevated.Portions[0].PerGameSecond /
            reduced.Normalize().Portions[0].PerGameSecond,
            6);
    }

    // --- Границы ---

    [Fact]
    public void AdvanceClampsToWhatIsLeftAndNeverOverfills()
    {
        var contents = BeginFood(200d, 500d);
        var expected = PlayerConditionScale.FromPercent(10d);

        // 200 мл еды при 500 ккал — это 10% шкалы энергии (5000 ккал).
        Assert.Equal(expected, contents.EnergyRemaining, 3);

        // Шаг БОЛЬШЕ времени переваривания: отдать больше остатка невозможно.
        var step = CharacterDigestion.Advance(contents, 60d * Hour);

        Assert.Equal(expected, step.EnergyGain, 3);
        Assert.True(step.Contents.IsEmpty);
        Assert.Equal(0d, step.Contents.EnergyPerGameSecond);

        // Объём освободился вместе с содержимым: пустое пищеварение снова
        // вмещает два литра.
        Assert.Equal(0d, step.Contents.VolumeFraction, 6);

        // Пустое пищеварение ничего не отдаёт даже на огромном шаге.
        var idle = CharacterDigestion.Advance(step.Contents, 100d * Hour);
        Assert.Equal(0d, idle.EnergyGain);
        Assert.Equal(0d, idle.HydrationGain);
    }

    [Fact]
    public void SteppingDoesNotDependOnStepSize()
    {
        // Общая пропускная способность на шаге ПОСТОЯННА, поэтому шаг можно
        // резать как угодно — сумма прибавок не зависит от нарезки. Это важно
        // для ускорения времени: при FF движок подаёт больше игровых секунд за
        // тик, и результат обязан совпасть с тем же временем, набранным мелкими
        // шагами. СОСТАВ при этом меняется (порции исчезают),, поэтому шаг
        // делится ДО первого исчезновения: здесь обе порции достаточно велики.
        var contents = BeginWater(1200d);
        contents = CharacterDigestion.Begin(
            contents,
            "test.food",
            1800d,
            0d,
            4000d,
            false,
            false);

        var oneHour = CharacterDigestion.Advance(contents, Hour);

        var firstHalf = CharacterDigestion.Advance(contents, Hour / 2d);
        var secondHalf = CharacterDigestion.Advance(
            firstHalf.Contents,
            Hour / 2d);

        Assert.Equal(
            oneHour.EnergyGain,
            firstHalf.EnergyGain + secondHalf.EnergyGain,
            4);
        Assert.Equal(
            oneHour.HydrationGain,
            firstHalf.HydrationGain + secondHalf.HydrationGain,
            3);
    }

    [Fact]
    public void ZeroPortionDoesNotStartDigestion()
    {
        var contents = Begin(0d, 0d, 0d);

        Assert.True(contents.IsEmpty);
        Assert.Equal(0d, contents.EnergyPerGameSecond);
        Assert.Equal(0d, contents.HydrationPerGameSecond);
    }

    [Fact]
    public void PortionWithoutNutritionNeverEntersStomach()
    {
        // Таблетка и сигарета кормят «побочно»: калорий в пищеварении нет, и она
        // не должна висеть в нём вечной иконкой с нулевой ставкой.
        var pill = CharacterDigestion.Begin(
            StomachContents.Empty,
            "painkiller",
            100d,
            0d,
            0d,
            false,
            false);

        Assert.True(pill.IsEmpty);
        Assert.Empty(pill.Portions);
    }

    [Fact]
    public void StomachRejectsPortionThatDoesNotFit()
    {
        // Вместимость — 2 литра. Две порции по 1,5 литра уместиться не могут:
        // вторая НЕ ПРИНИМАЕТСЯ ВОВСЕ.
        //
        // Прежняя модель принимала её «по частям» (0,5 из 1,5), и в порцию
        // записывалась эта урезанная доля. Именно это видел автор: он выбирал
        // банан «110 мл», а в пищеварении появлялась порция на 20 мл. Теперь
        // правило «целиком или никак», и объём порции всегда равен выбранному
        // предмету.
        var first = BeginFood(1500d, 3750d);

        Assert.Equal(0.75d, first.VolumeFraction, 6);

        var second = CharacterDigestion.Begin(
            first,
            "test.second",
            1500d,
            0d,
            3750d,
            false,
            false);

        // Объём и энергия не изменились: порция не съедена.
        Assert.Equal(0.75d, second.VolumeFraction, 6);
        Assert.Equal(first.EnergyRemaining, second.EnergyRemaining, 6);
        Assert.Single(second.Portions);

        // В свободные пол-литра мелкая порция (500 мл) влезает ЦЕЛИКОМ — и
        // записывается ровно своим объёмом, а не остатком.
        var third = CharacterDigestion.Begin(
            first,
            "test.third",
            500d,
            0d,
            1250d,
            false,
            false);

        Assert.Equal(1d, third.VolumeFraction, 6);
        Assert.Equal(2, third.Portions.Count);
        Assert.Equal(0.25d, third.Portions[1].VolumeFraction, 6);

        // Полное пищеварение больше не принимает ничего: лишнее не съедается.
        var fourth = CharacterDigestion.Begin(
            third,
            "test.fourth",
            1500d,
            0d,
            3750d,
            false,
            false);

        Assert.Equal(third.EnergyRemaining, fourth.EnergyRemaining, 6);
    }

    // --- Порции по отдельности (дефект автора: «Апельсин стёр Воду») ---

    [Fact]
    public void DrinkingWaterThenEatingOrangeKeepsBothPortions()
    {
        // Автор: «Использовал порцию воды — динамика жидкости правильная, затем
        // использовал Апельсин — вода исчезла, остался только Апельсин».
        // Причина была в модели: желудок помнил ОДНО имя (последний предмет) и
        // одни суммы, поэтому вторая порция ПОДМЕНЯЛА подпись первой, хотя вода
        // продолжала перевариваться. Теперь порции хранятся списком.
        var water = CharacterDigestion.Begin(
            StomachContents.Empty,
            "water.bottle",
            CharacterConsumableCatalog.GetPortionMilliliters("water.bottle"),
            CharacterConsumableCatalog.GetProfile("water.bottle").WaterMilliliters,
            0d,
            false,
            false);

        var withOrange = CharacterDigestion.Begin(
            water,
            "food.orange",
            CharacterConsumableCatalog.GetPortionMilliliters("food.orange"),
            CharacterConsumableCatalog.GetProfile("food.orange").WaterMilliliters,
            CharacterConsumableCatalog.GetProfile("food.orange").Kilocalories,
            false,
            false);

        // ОБЕ порции в пищеварении, в порядке употребления.
        Assert.Equal(2, withOrange.Portions.Count);
        Assert.Equal("water.bottle", withOrange.Portions[0].ItemId);
        Assert.Equal("food.orange", withOrange.Portions[1].ItemId);

        var portions = CharacterDigestionReport.Portions(withOrange);

        // Вода по-прежнему даёт жидкость, апельсин — и то и другое.
        // Калорийность апельсина — справочная: 43 ккал/100 г × 2,0 = 86.
        Assert.Equal(500d, portions[0].WaterMilliliters, 3);
        Assert.Equal(0d, portions[0].EnergyKilocalories, 3);
        Assert.Equal(86d, portions[1].EnergyKilocalories, 3);
        Assert.Equal(170d, portions[1].WaterMilliliters, 3);

        // Объём пищеварения — сумма обеих порций, а не объём последней.
        Assert.Equal(
            CharacterDigestion.PortionFraction(
                CharacterConsumableCatalog.GetPortionMilliliters("water.bottle")) +
            CharacterDigestion.PortionFraction(
                CharacterConsumableCatalog.GetPortionMilliliters("food.orange")),
            withOrange.VolumeFraction,
            6);

        // Таймер порции — положительный и РАЗНЫЙ у воды и еды (разные скорости).
        Assert.True(portions[0].RemainingGameSeconds > 0d);
        Assert.True(portions[1].RemainingGameSeconds > 0d);
        Assert.NotEqual(
            portions[0].RemainingGameSeconds,
            portions[1].RemainingGameSeconds);
    }

    [Fact]
    public void PortionDisappearsOnlyWhenFullyAbsorbed()
    {
        // Порция исчезает из пищеварения СВОИМ сроком, а не когда «её перестали
        // называть последней». Вода уходит быстрее апельсина, поэтому первым из
        // списка пропадает вода.
        var water = BeginWater(1000d);

        var both = CharacterDigestion.Begin(
            water,
            "food.orange",
            200d,
            68d,
            86d,
            false,
            false);

        Assert.Equal(2, both.Portions.Count);

        // Малый шаг: обе порции ещё в пищеварении.
        var early = CharacterDigestion.Advance(both, Minute * 5);
        Assert.Equal(2, early.Contents.Portions.Count);

        // Полное переваривание очищает список целиком.
        var finished = CharacterDigestion.Advance(both, 60d * Hour);
        Assert.True(finished.Contents.IsEmpty);
        Assert.Empty(finished.Contents.Portions);
        Assert.Equal(0d, finished.Contents.VolumeFraction, 6);
    }

    [Fact]
    public void OneEatenItemIsExactlyOneStomachPortion()
    {
        // ОДИН съеденный предмет — РОВНО ОДНА порция, даже когда он даёт и
        // энергию, и жидкость. Прямой тест дефекта, который увидел автор:
        // «домашняя колбаса добавляется два раза», «апельсин тоже два раза»,
        // «добавил шоколад, он не появился в желудке».
        //
        // Проверяется по ВСЕМУ каталогу еды и питья: дефект был общим для
        // профиля, и одиночная проверка пропустила бы предмет с нулевым
        // остатком по одной из шкал (у шоколада 2 мл воды).
        foreach (var item in ItemCatalogFactory.Items)
        {
            var profile = CharacterConsumableCatalog.GetProfile(item.Id);
            if (!profile.Feeds)
                continue;

            var update = CharacterVitalsEngine.UseItem(
                item.Id,
                PlayerVitalsState.Default.Normalize(),
                PlayerConditionState.Empty.Normalize());

            var portions = update.Conditions.Stomach.Portions;

            Assert.True(
                portions.Count == 1,
                $"«{item.Name}» ({item.Id}) даёт {portions.Count} порции в пищеварении, " +
                "а должен ровно одну: предмет съеден один раз");

            Assert.Equal(item.Id, portions[0].ItemId);

            // ОБЪЁМ — свойство КУСКА, а не шкалы: он берётся по массе предмета
            // один раз. Две порции описывали бы вдвое больше съеденного.
            Assert.Equal(
                Math.Max(0.01d, profile.PortionFraction),
                portions[0].VolumeFraction,
                6);

            // Обе ставки живут в ОДНОЙ порции: еда идёт по сухой пропускной
            // способности, вода — по своей, и разные скорости обязаны
            // сохраниться, иначе таймер перестал бы отвечать содержимому.
            if (profile.Kilocalories > 0d)
                Assert.True(portions[0].EnergyPerGameSecond > 0d);
            if (profile.WaterMilliliters > 0d)
                Assert.True(portions[0].HydrationPerGameSecond > 0d);

            // Остаток по каждой шкале есть только там, где шкала восполняется:
            // у воды энергия НУЛЕВАЯ. Именно из-за ненулевого остатка по «чужой»
            // шкале раньше появлялась лишняя порция.
            Assert.Equal(
                profile.Kilocalories > 0d,
                portions[0].KilocaloriesRemaining > 0d);
            Assert.Equal(
                profile.WaterMilliliters > 0d,
                portions[0].WaterMillilitersRemaining > 0d);
        }
    }

    [Fact]
    public void ConsumableProfileExistsForEveryFoodAndDrink()
    {
        // Физический профиль обязан быть у каждой еды и питья каталога: без него
        // предмет не восстанавливает шкалы вовсе (профиль — единственный
        // источник питательности после перехода на калории и миллилитры).
        foreach (var item in ItemCatalogFactory.Items)
        {
            var isFoodOrDrink =
                item.Category.Equals("Еда", StringComparison.OrdinalIgnoreCase) ||
                item.Category.Equals("Напиток", StringComparison.OrdinalIgnoreCase);

            if (!isFoodOrDrink)
                continue;

            var profile = CharacterConsumableCatalog.GetProfile(item.Id);

            Assert.True(
                profile.Feeds,
                $"у предмета «{item.Name}» ({item.Id}) нет физического профиля: " +
                "он не восстановит ни энергию, ни жидкость");
        }
    }

    [Fact]
    public void NonFoodItemsGetARealPortionVolume()
    {
        // Объём порции есть и у предметов БЕЗ пищевого профиля: иначе движок
        // подставлял минимум, и таблетка занимала в пищеварении столько же,
        // сколько обед.
        var pill = CharacterConsumableCatalog.GetPortionFractionOrDefault("painkiller");
        var meal = CharacterConsumableCatalog.GetPortionFractionOrDefault("food.meal");
        var water = CharacterConsumableCatalog.GetPortionFractionOrDefault("water.bottle");

        Assert.True(pill > 0d);
        Assert.True(pill < meal);
        Assert.True(pill < water);

        // Бытовые порции заданы явно и читаются как объём.
        Assert.Equal(1000d, CharacterConsumableCatalog.GetPortionGrams("medicine.recovery_salts"), 3);
        Assert.Equal(1d, CharacterConsumableCatalog.GetPortionGrams("smoke.cigarette"), 3);

        // Пищевой профиль имеет приоритет над таблицей бытовых порций.
        Assert.Equal(
            CharacterConsumableCatalog.GetProfile("food.meal").Grams,
            CharacterConsumableCatalog.GetPortionGrams("food.meal"),
            3);
    }

    // --- Связь с движком ---

    [Fact]
    public void UseItemRoutesRecoveryThroughStomachInsteadOfInstant()
    {
        var vitals = PlayerVitalsState.Default with
        {
            // Метаболизм в норме: 60% лежит между 45% и 75%.
            Metabolism = PlayerConditionScale.FromPercent(
                CharacterVitalsEngine.DefaultMetabolismPercent),
            Hydration = PlayerConditionScale.FromPercent(40d)
        };

        var used = CharacterVitalsEngine.UseItem(
            "water.bottle",
            vitals,
            PlayerConditionState.Empty);

        // Мгновенного восстановления больше нет: вода ждёт в пищеварении.
        Assert.Equal(
            PlayerConditionScale.FromPercent(40d),
            used.Vitals.Hydration,
            3);

        // Бутылка 500 мл = 16,67% шкалы (шкала — 3000 мл).
        Assert.Equal(
            PlayerConditionScale.UnitsPerPercent * 500d / 30d,
            used.Conditions.Stomach.HydrationRemaining,
            2);

        // Четверть часа усвоения отдаёт 1,35 / 4 ≈ 337,5 мл. Остаток считаем от
        // фактической доли усвоенного, а не вторым «ожидаемым» числом: два
        // расчёта одного и того же разошлись бы при следующей правке скорости.
        var quarterSeconds = Hour / 4d;
        var absorbed =
            used.Conditions.Stomach.HydrationPerGameSecond * quarterSeconds;

        var advanced = CharacterVitalsEngine.Advance(
            used.Vitals,
            used.Conditions,
            quarterSeconds,
            quarterSeconds,
            playerMoving: false,
            sleeping: false,
            traveledMeters: 0d,
            gameHourOfDay: 12d);

        Assert.Equal(
            used.Conditions.Stomach.HydrationRemaining - absorbed,
            advanced.Conditions.Stomach.HydrationRemaining,
            2);

        // Само число тоже закрепляем: 1,35 л/ч в четверть часа — это 337,5 мл.
        Assert.Equal(
            PlayerConditionScale.UnitsPerPercent * 337.5d / 30d,
            absorbed,
            2);

        // Шкала за это время выросла ОТНОСИТЕЛЬНО исходной: приток 337,5 мл в час
        // перекрывает расход шкалы (400 единиц в час в покое).
        Assert.True(
            advanced.Vitals.Hydration > used.Vitals.Hydration,
            "вода обязана дать НЕТТО-прирост жидкости, а не только компенсировать расход: " +
            advanced.Vitals.Hydration + " против " + used.Vitals.Hydration);
    }

    [Fact]
    public void UseItemRemembersConsumedItemForMonitor()
    {
        var used = CharacterVitalsEngine.UseItem(
            "water.bottle",
            PlayerVitalsState.Default,
            PlayerConditionState.Empty);

        // Монитор обязан назвать пункт переваривания: без Id он показал бы
        // безымянный остаток, и игрок не понял бы, ЧТО именно усваивается.
        Assert.Equal("water.bottle", used.Conditions.LastConsumedItemId);
    }

    // --- Отчёт для интерфейса ---

    [Fact]
    public void ReportExposesCapacityAndFactorsWithThroughput()
    {
        // Вместимость и подсказки живут в домене: вторая их версия в JavaScript
        // разошлась бы с начислением при первой же правке физиологии.
        Assert.Equal(2000d, CharacterDigestionReport.StomachCapacityMilliliters, 6);

        var factors = CharacterDigestionReport.MetabolismFactors(
            CharacterVitalsEngine.DefaultMetabolismPercent,
            elevatedMetabolism: true,
            reducedMetabolism: false);

        var text = string.Join(" ", factors.Select(f => f.Text));

        Assert.Contains("Пропускная способность", text);
        Assert.All(factors, f => Assert.False(string.IsNullOrWhiteSpace(f.Text)));

        // Повышенный метаболизм добавляет свой фактор и помечен как полезный.
        Assert.Equal(2, factors.Count);
        Assert.True(factors[1].Useful);
    }

    // --- Объём порции: одно число на всех ---

    /// <summary>
    /// ОБЪЁМ ПОРЦИИ — ОДНО ЧИСЛО на все места: меню добавления, признак
    /// «поместится», записанная порция и объём в интерфейсе.
    ///
    /// Прямой тест сообщения автора: «Банан 110 мл, а в желудок добавилось 150.
    /// Паёк 250, а добавилось 400». Механика была верна — 150 и 400 это ОБЪЁМЫ
    /// порций; ошибочной была ПОДПИСЬ, она печатала содержимое ВОДЫ (110 и 250).
    /// Тест держит инвариант, который эту подмену делает невозможной.
    /// </summary>
    [Fact]
    public void PortionMillilitresIsTheSingleNumberEverywhere()
    {
        var checkedAny = false;

        foreach (var item in ItemCatalogFactory.Items)
        {
            var portion = CharacterConsumableCatalog.GetPortionMilliliters(item.Id);

            // Число, которое интерфейс подписывает как объём порции, есть
            // миллилитры порции — не «г», не литры и не что-то ещё.
            Assert.True(
                portion >= 0d,
                $"у «{item.Name}» ({item.Id}) отрицательный объём порции: {portion}");

            if (!CharacterVitalsEngine.CanConsume(item.Id))
                continue;

            // Ноль бывает только у предметов без места в пищеварении (мыло).
            // Тогда движок подставляет минимум, и это описано отдельно — здесь
            // проверяем именно ПОЛОЖИТЕЛЬНЫЕ объёмы.
            if (portion <= 0d)
                continue;

            var profile = CharacterConsumableCatalog.GetProfile(item.Id);
            var applied = Math.Max(
                CharacterDigestion.MinimumPortionFraction *
                CharacterDigestion.CapacityMilliliters,
                portion);

            var stomach = CharacterDigestion.Begin(
                StomachContents.Empty,
                item.Id,
                applied,
                profile.WaterMilliliters,
                profile.Kilocalories,
                false,
                false);

            // Предмет без питательности порции не заводит — это законно.
            if (stomach.Portions.Count == 0)
                continue;

            checkedAny = true;

            // 1. ПОДПИСЬ: миллилитры порции дают ту долю вместимости, которую
            //    записал домен.
            Assert.Equal(
                CharacterDigestion.PortionFraction(applied),
                stomach.Portions[0].VolumeFraction,
                6);

            // 2. ЗАПИСЬ: записанная доля, переведённая обратно в миллилитры,
            //    равна подписанным миллилитрам. Это и есть «добавилось ровно
            //    столько, сколько указано в списке».
            Assert.Equal(
                applied,
                CharacterDigestionReport.Portions(stomach)[0].VolumeMilliliters,
                3);

            // 3. ПРИЗНАК: объём порции не превышает ВМЕСТИМОСТЬ, поэтому порция
            //    помещается в пустое пищеварение. Список и движок судят одним
            //    числом.
            Assert.True(
                CharacterDigestionReport.Fits(StomachContents.Empty, item.Id),
                $"«{item.Name}» ({item.Id}) не поместился в ПУСТОЕ пищеварение — " +
                "объём порции больше вместимости");
        }

        Assert.True(
            checkedAny,
            "ни один предмет каталога не проверен: фикстура вырождена");
    }

    /// <summary>
    /// Объём порции в пищеварении ВСЕГДА равен объёму выбранного предмета, даже
    /// когда пищеварение уже чем-то занято.
    ///
    /// Прямой тест дефекта, который увидел автор: «указанный объём в списке не
    /// соответствует добавленному. 5 мл мёда добавилось как 30, 110 мл банана
    /// стало 20 мл, вода добавилась правильно — 500 мл». Механизм был такой:
    /// прежняя модель принимала порцию «по частям» и записывала в порцию ИМЕННО
    /// УРЕЗАННУЮ долю.
    ///
    /// Проверка идёт по ВСЕМУ каталогу еды и питья И при нескольких уровнях
    /// занятости, с ЗАМЕРОМ ДОБАВЛЕННОГО объёма, а не только абсолютного:
    /// «записано ровно столько, сколько влезло» — это и есть та подмена, которую
    /// автор назвал обманом.
    /// </summary>
    [Theory]
    [InlineData(0.0d)]
    [InlineData(0.25d)]
    [InlineData(0.5d)]
    [InlineData(0.75d)]
    [InlineData(0.95d)]
    public void RecordedPortionVolumeAlwaysEqualsItemVolume(double occupancy)
    {
        foreach (var item in ItemCatalogFactory.Items)
        {
            var profile = CharacterConsumableCatalog.GetProfile(item.Id);
            if (!profile.Feeds)
                continue;

            var expected = Math.Max(
                CharacterDigestion.MinimumPortionFraction *
                CharacterDigestion.CapacityMilliliters,
                CharacterConsumableCatalog.GetPortionMilliliters(item.Id));

            // Занимаем пищеварение на заданную долю порцией-заглушкой: место уже
            // ограничено, и «урезающая» модель тут же показала бы свой дефект.
            var occupied = occupancy > 0d
                ? BeginFood(
                    occupancy * CharacterDigestion.CapacityMilliliters,
                    occupancy * 5000d)
                : StomachContents.Empty.Normalize();

            var fits = CharacterDigestionReport.Fits(occupied, item.Id);

            var contents = CharacterDigestion.Begin(
                occupied,
                item.Id,
                expected,
                profile.WaterMilliliters,
                profile.Kilocalories,
                false,
                false);

            var added = contents.Portions
                .Where(p => p.ItemId == item.Id)
                .ToList();

            Assert.True(
                added.Count <= 1,
                $"«{item.Name}» ({item.Id}) дал {added.Count} порции за одно употребление");

            // Признак «поместится» и фактический приём обязаны совпадать: по
            // первому интерфейс серым гасит пункт, по второму начисляет движок.
            Assert.True(
                fits == (added.Count == 1),
                $"при занятости {occupancy:P0} у «{item.Name}» ({item.Id}) признак " +
                $"«поместится» ({fits}) разошёлся с фактическим приёмом");

            var portionFraction =
                CharacterDigestion.PortionFraction(expected);
            var volumeDelta = contents.VolumeFraction - occupied.VolumeFraction;

            if (added.Count == 0)
            {
                // Не помещается — пищеварение НЕ изменилось вовсе.
                Assert.Equal(0d, volumeDelta, 6);
                continue;
            }

            // ГЛАВНОЕ: записан объём ПРЕДМЕТА, а не остаток вместимости.
            Assert.Equal(portionFraction, added[0].VolumeFraction, 6);
            Assert.Equal(portionFraction, volumeDelta, 6);
        }
    }
}
