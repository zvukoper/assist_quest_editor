using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Пищеварение: еда и питьё восстанавливают шкалы ПОСТЕПЕННО, а не мгновенно.
///
/// Тесты держат ФИЗИЧЕСКИЕ величины автора, а не «эталонные минуты» прежней
/// модели:
///   • шкала энергии — 5000 ккал (10000 единиц хранения), жидкость — 3000 мл;
///   • объём желудка — 1 литр;
///   • полный желудок ЕДЫ уходит за 2 игровых часа (5000 единиц в час);
///   • ЖИДКОСТЬ усваивается 1,35 литра в час — это ФИЗИОЛОГИЯ, а не доля
///     шкалы, поэтому на трёхлитровой шкале та же ставка читается как 45%
///     шкалы в час (4500 единиц в час). Прежняя шкала 15 000 мл давала те же
///     1350 мл в час «всего» 9% шкалы;
///   • при нормальной нагрузке расход энергии 16,7%, жидкости 10% в час.
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

    private static StomachContents Begin(
        double energyPercent,
        double hydrationPercent,
        bool elevated = false,
        bool reduced = false,
        double portionFraction = 1d,
        string itemId = "test.item") =>
        CharacterDigestion.Begin(
            StomachContents.Empty,
            itemId,
            energyPercent,
            hydrationPercent,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: elevated,
            reducedMetabolism: reduced,
            portionFraction: portionFraction);

    /// <summary>Прогоняет переваривание до пустого желудка и складывает прибавки.</summary>
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

    // --- Объём желудка ---

    [Fact]
    public void PortionFractionIsShareOfOneLitreStomach()
    {
        // 500 г — половина литрового желудка, 1000 г — весь.
        Assert.Equal(0.5d, CharacterDigestion.PortionFraction(500d), 6);
        Assert.Equal(1d, CharacterDigestion.PortionFraction(1000d), 6);

        // Больше литра желудок не вмещает: доля ограничена единицей.
        Assert.Equal(1d, CharacterDigestion.PortionFraction(5000d), 6);

        // Нечего переваривать — нулевая доля.
        Assert.Equal(0d, CharacterDigestion.PortionFraction(0d), 6);
        Assert.Equal(0d, CharacterDigestion.PortionFraction(-10d), 6);
    }

    // --- Скорости автора ---

    [Fact]
    public void FullFoodStomachEmptiesInTwoGameHours()
    {
        // Полный желудок еды даёт весь объём шкалы и переваривается 2 игровых часа.
        var food = Begin(100d, 0d);

        Assert.Equal(
            CharacterDigestion.FoodStomachEmptyHours * Hour,
            food.EnergyRemaining / food.EnergyPerGameSecond,
            3);

        // 5000 единиц в час: это и есть «полная шкала за 2 часа».
        Assert.Equal(Scale, food.EnergyRemaining, 3);
        Assert.Equal(Scale / 2d, food.EnergyPerGameSecond * Hour, 3);
    }

    [Fact]
    public void FullDrinkAbsorbsAtTheMeasuredRate()
    {
        // Ставка ФИЗИОЛОГИЧЕСКАЯ и от размера шкалы не зависит: 1,35 л/ч.
        // Полная шкала жидкости — 3 литра, поэтому её полный объём уходит за
        // 3 / 1,35 ≈ 2,22 игрового часа (было 11,1 ч на пятнадцатилитровой
        // шкале — та же физиология, меньший резервуар).
        var drink = Begin(0d, 100d);

        Assert.Equal(
            HydrationScaleLiters / CharacterDigestion.HydrationAbsorptionLitersPerHour * Hour,
            drink.HydrationRemaining / drink.HydrationPerGameSecond,
            3);

        // 1350 мл в час = 1350/3000 шкалы = 45% = 4500 единиц в час.
        Assert.Equal(
            1350d / CharacterDigestion.HydrationScaleMilliliters * Scale,
            drink.HydrationPerGameSecond * Hour,
            3);
    }

    [Fact]
    public void DrinkingGivesThePhysicalRateUnderReducedMetabolism()
    {
        // Литр при ПОНИЖЕННОМ метаболизме: он замедляет усвоение в 1,5 раза,
        // поэтому 45% шкалы в час превращаются в 30% (3000 единиц в час), то
        // есть 50 единиц в минуту. Число выведено из физиологии и размера
        // шкалы, а не задано отдельно.
        var litre = Begin(
            0d,
            1000d / 30d,
            reduced: true);

        Assert.Equal(
            50d,
            litre.HydrationPerGameSecond * 60d,
            2);

        var duration =
            litre.HydrationRemaining / litre.HydrationPerGameSecond;

        // Объём — литр минус штраф 0,5% шкалы (50 единиц), то есть 3283,3
        // единицы; на 3000 единиц в час это ≈ 3940 секунд.
        Assert.InRange(duration, 3800d, 4050d);
    }

    [Fact]
    public void DrinkingOutpacesConsumptionUnderReducedMetabolism()
    {
        // При пониженном метаболизме литр всё равно даёт 3000 единиц в час
        // против расхода 360 единиц в час (4% шкалы в час в покое ×0,9), то
        // есть запас почти десятикратный: динамика обязана быть положительной
        // в любом нормальном состоянии.
        var stomach = Begin(0d, 100d, reduced: true);

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
    public void AbsorptionRateIsConstantRegardlessOfPortion()
    {
        // Ставку задаёт физиология, а не размер порции: узкое место — усвоение.
        // Малое и большое питьё поступают в шкалу с ОДНОЙ скоростью, поэтому
        // мелкое питьё тоже даёт положительную динамику, а не «размазывается»
        // на то же время, что и большое (дефект прежней модели).
        var small = Begin(0d, 5d, portionFraction: 0.25d);
        var large = Begin(0d, 10d, portionFraction: 0.5d);

        Assert.Equal(
            large.HydrationPerGameSecond,
            small.HydrationPerGameSecond,
            6);
    }

    // --- Положительная динамика: то, ради чего шкалы перебалансированы ---

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
        // балансная цифра: если полный желудок отдаёт меньше, чем тратит
        // организм, то даже плотный обед оставляет динамику красной — ровно на
        // это жаловался автор в прошлой сессии.
        //
        // ЗАПАС СНИЖЕН С 3× ДО 1,5× (30.09.2026). Раньше под нагрузкой расход был
        // 1666,7 ед./ч, и усвоение 5000 ед./ч давало трёхкратный запас. Автор
        // поднял расход коэффициентом `EnergyLoadCoefficient = 1,5` (3750 ккал за
        // 3 часа = 25%/ч = 2500 ед./ч), и запас стал ровно двукратным. Проверка
        // «строго больше 2×» при этом падала на РАВЕНСТВЕ, то есть валила не
        // дефект, а собственную цифру. Здесь остаётся инвариант с запасом 1,5× —
        // он ловит поломку «еда не покрывает расход», но не спорит с балансом.
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
            0d,
            profile.HydrationPercent,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: profile.PortionFraction);

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
        // Бутерброд 180 г / 320 ккал (12,9/12,9/1,1 на 100 г × 1,8): 320/50 = 6,4%
        // шкалы, усвоение 5000 ед./ч (полный желудок еды уходит за 2 часа) против
        // расхода 1666,7 ед./ч. Числа профиля берутся ИЗ КАТАЛОГА, а не
        // переписываются: переписав их, тест перестал бы ловить смену калорийности.
        var profile = CharacterConsumableCatalog.GetProfile("food.egg_sandwich");

        Assert.Equal(320d, profile.Kilocalories, 3);
        Assert.Equal(6.4d, profile.EnergyPercent, 3);

        var stomach = CharacterDigestion.Begin(
            StomachContents.Empty,
            "food.egg_sandwich",
            profile.EnergyPercent,
            0d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: profile.PortionFraction);

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
        var contents = Begin(20d, 10d);
        var expectedEnergy = PlayerConditionScale.FromPercent(20d);
        var expectedHydration = PlayerConditionScale.FromPercent(10d);

        var result = Drain(contents);

        // Объём порции обязан дойти до шкал ЦЕЛИКОМ — ни единицы не теряется.
        Assert.Equal(expectedEnergy, result.Energy, 3);
        Assert.Equal(expectedHydration, result.Hydration, 3);

        // Желудок пустеет, когда кончится ПОСЛЕДНЯЯ шкала, а не жидкость:
        // теперь энергия усваивается 5000 ед./ч, жидкость — 4500 ед./ч, то есть
        // 20% энергии (2000 ед.) ждут 0,4 игрового часа, а 10% жидкости
        // (1000 ед.) — 0,22 часа. Длительность считаем по максимуму, чтобы
        // тест проверял физику, а не порядок величин сегодняшнего дня.
        var energySeconds =
            expectedEnergy /
            (Scale / CharacterDigestion.FoodStomachEmptyHours) *
            Hour;
        var hydrationSeconds =
            expectedHydration /
            (1350d / CharacterDigestion.HydrationScaleMilliliters * Scale) *
            Hour;
        var expectedSeconds =
            Math.Max(energySeconds, hydrationSeconds);

        // Допуск — одна минута, потому что Drain шагает МИНУТАМИ и
        // останавливается на первом пустом желудке: последний шаг законно
        // перескакивает границу. Проверять здесь доли секунды значило бы
        // требовать точности, которой у замера нет.
        Assert.InRange(
            result.Seconds,
            expectedSeconds - Minute,
            expectedSeconds + Minute);
    }
    [Fact]
    public void EnergyArrivesGraduallyNotInstantly()
    {
        var contents = Begin(20d, 0d, portionFraction: 0.25d);

        // Ставка = 5000 единиц в час (полная шкала за 2 часа).
        Assert.Equal(
            Scale / CharacterDigestion.FoodStomachEmptyHours,
            contents.EnergyPerGameSecond * Hour,
            3);

        // За первую МИНУТУ желудок отдаёт ровно одну шестидесятую часа, то есть
        // 5000/60 единиц — и это меньше всей порции (2000 единиц), поэтому
        // восстановление постепенное, а не мгновенное.
        var step = CharacterDigestion.Advance(contents, Minute);

        Assert.Equal(
            Scale / CharacterDigestion.FoodStomachEmptyHours / 60d,
            step.EnergyGain,
            3);
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
        var normal = Begin(20d, 0d);
        var elevated = Begin(20d, 0d, elevated: true);

        // В 2,5 раза быстрее: ставка выше во столько же раз.
        Assert.Equal(
            normal.EnergyPerGameSecond * 2.5d,
            elevated.EnergyPerGameSecond,
            3);

        // Плюс 0,5% ОТ МАКСИМУМА шкалы к объёму.
        Assert.Equal(
            normal.EnergyRemaining +
                PlayerConditionScale.Maximum *
                CharacterDigestion.ElevatedMetabolismVolumeBonus,
            elevated.EnergyRemaining,
            3);
    }

    [Fact]
    public void ReducedMetabolismDigestsSlowerAndTakesVolumePenalty()
    {
        var normal = Begin(0d, 20d);
        var reduced = Begin(0d, 20d, reduced: true);

        // В 1,5 раза медленнее.
        Assert.Equal(
            normal.HydrationPerGameSecond / 1.5d,
            reduced.HydrationPerGameSecond,
            3);

        // Штраф −0,5% от максимума шкалы.
        Assert.Equal(
            normal.HydrationRemaining -
                PlayerConditionScale.Maximum *
                CharacterDigestion.ReducedMetabolismVolumePenalty,
            reduced.HydrationRemaining,
            3);
    }

    // --- Очередь порций ---

    [Fact]
    public void SecondPortionQueuesInsteadOfSpeedingUp()
    {
        var first = Begin(20d, 0d, portionFraction: 0.25d);
        var half = CharacterDigestion.Advance(
            first,
            first.EnergyRemaining /
            first.EnergyPerGameSecond /
            2d);

        var second = CharacterDigestion.Begin(
            half.Contents,
            "test.second",
            20d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.25d);

        // Остаток сложился, а объём занят двумя порциями.
        Assert.Equal(
            PlayerConditionScale.FromPercent(30d),
            second.EnergyRemaining,
            3);
        Assert.Equal(0.5d, second.VolumeFraction, 6);

        // ВАЖНОЕ, что здесь охраняется: вторая порция НЕ УСКОРЯЕТ первую. У
        // каждой порции свой срок и своя ставка, поэтому у первой порции
        // оставшееся время не изменилось от того, что съели ещё одну.
        var firstPortionBefore = half.Contents.Portions[0].RemainingGameSeconds;
        var firstPortionAfter = second.Portions[0].RemainingGameSeconds;

        Assert.Equal(firstPortionBefore, firstPortionAfter, 6);

        // И вторая порция короче первой: она ещё целая, а первая уже наполовину
        // усвоена. Именно это и позволяет монитору показать у каждой иконки свой
        // таймер (см. отчёт автора о «Воде» и «Апельсине»).
        Assert.True(
            second.Portions[1].RemainingGameSeconds >
            second.Portions[0].RemainingGameSeconds);

        // Обе порции еды дают ОДИНАКОВУЮ ставку: она задаётся физиологией, а не
        // размером порции.
        Assert.Equal(
            second.Portions[0].EnergyPerGameSecond,
            second.Portions[1].EnergyPerGameSecond,
            6);
    }

    [Fact]
    public void StomachRejectsPortionThatDoesNotFit()
    {
        // Объём желудка — 1 литр. Две порции по 0,75 литра уместиться не могут:
        // вторая НЕ ПРИНИМАЕТСЯ ВОВСЕ.
        //
        // Прежняя модель принимала её «по частям» (0,25 из 0,75), и в порцию
        // записывалась эта урезанная доля. Именно это видел автор: он выбирал
        // банан «110 мл», а в желудке появлялась порция на 20 мл. Теперь правило
        // «целиком или никак», и объём порции всегда равен выбранному предмету.
        var first = Begin(10d, 0d, portionFraction: 0.75d);

        Assert.Equal(0.75d, first.VolumeFraction, 6);

        var second = CharacterDigestion.Begin(
            first,
            "test.second",
            10d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.75d);

        // Объём и энергия не изменились: порция не съедена.
        Assert.Equal(0.75d, second.VolumeFraction, 6);
        Assert.Equal(
            first.EnergyRemaining,
            second.EnergyRemaining,
            6);
        Assert.Single(second.Portions);

        // В свободную четверть литра мелкая порция (0,25 = 250 мл) влезает
        // ЦЕЛИКОМ — и записывается ровно своим объёмом, а не остатком.
        var third = CharacterDigestion.Begin(
            first,
            "test.third",
            10d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.25d);

        Assert.Equal(1d, third.VolumeFraction, 6);
        Assert.Equal(2, third.Portions.Count);
        Assert.Equal(0.25d, third.Portions[1].VolumeFraction, 6);

        // Полный желудок больше не принимает ничего: лишнее не съедается.
        var fourth = CharacterDigestion.Begin(
            third,
            "test.fourth",
            10d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.75d);

        Assert.Equal(
            third.EnergyRemaining,
            fourth.EnergyRemaining,
            6);
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
            0d,
            100d / 6d, // 500 мл из 3000
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: CharacterConsumableCatalog.GetPortionFraction("water.bottle"));

        var withOrange = CharacterDigestion.Begin(
            water,
            "food.orange",
            CharacterConsumableCatalog.GetProfile("food.orange").EnergyPercent,
            CharacterConsumableCatalog.GetProfile("food.orange").HydrationPercent,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: CharacterConsumableCatalog.GetPortionFraction("food.orange"));

        // ОБЕ порции в желудке, в порядке употребления.
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

        // Объём желудка — сумма обеих порций, а не объём последней.
        Assert.Equal(
            CharacterConsumableCatalog.GetPortionFraction("water.bottle") +
            CharacterConsumableCatalog.GetPortionFraction("food.orange"),
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
        // Порция исчезает из желудка СВОЕЙ скоростью, а не когда «её перестали
        // называть последней». Вода уходит быстрее апельсина (45%/ч жидкости
        // против ставки еды), поэтому первым из списка пропадает вода.
        var water = CharacterDigestion.Begin(
            StomachContents.Empty,
            "water.bottle",
            0d,
            100d / 6d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.5d);

        var both = CharacterDigestion.Begin(
            water,
            "food.orange",
            2d,
            5.67d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.2d);

        Assert.Equal(2, both.Portions.Count);

        // Малый шаг: обе порции ещё в желудке.
        var early = CharacterDigestion.Advance(both, Minute * 5);
        Assert.Equal(2, early.Contents.Portions.Count);

        // Полное переваривание очищает список целиком.
        var finished = CharacterDigestion.Advance(both, 60d * Hour);
        Assert.True(finished.Contents.IsEmpty);
        Assert.Empty(finished.Contents.Portions);
        Assert.Equal(0d, finished.Contents.VolumeFraction, 6);
    }

    [Fact]
    public void PortionWithoutNutritionNeverEntersStomach()
    {
        // Таблетка и сигарета кормят «побочно»: калорий в желудке нет, и она не
        // должна висеть в нём вечной иконкой с нулевой ставкой.
        var pill = CharacterDigestion.Begin(
            StomachContents.Empty,
            "painkiller",
            0d,
            0d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: CharacterConsumableCatalog.GetPortionFractionOrDefault("painkiller"));

        Assert.True(pill.IsEmpty);
        Assert.Empty(pill.Portions);
    }

    [Fact]
    public void NonFoodItemsGetARealPortionVolume()
    {
        // Объём порции теперь есть и у предметов БЕЗ пищевого профиля: иначе
        // движок подставлял минимум 0,01, и таблетка занимала в желудке столько
        // же, сколько обед (по таймеру они совпадали).
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

    [Fact]
    public void StomachPortionsSurviveSaveAndLoadNormalization()
    {
        // Порции обязаны пережить Normalize (его вызывает и движок, и чтение
        // сохранения). Проверяем, что суммы остаются согласованы с порциями —
        // это и был механизм дефекта: подпись менялась, а суммы оставались.
        var contents = CharacterDigestion.Begin(
            StomachContents.Empty,
            "water.bottle",
            0d,
            100d / 6d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.4d);

        contents = CharacterDigestion.Begin(
            contents,
            "food.apple",
            1.8d,
            3.1d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.2d);

        var normalized = contents.Normalize();

        Assert.Equal(2, normalized.Portions.Count);

        var energySum = 0d;
        var hydrationSum = 0d;
        var volumeSum = 0d;
        foreach (var portion in normalized.Portions)
        {
            energySum += portion.EnergyRemaining;
            hydrationSum += portion.HydrationRemaining;
            volumeSum += portion.VolumeFraction;
        }

        Assert.Equal(normalized.EnergyRemaining, energySum, 6);
        Assert.Equal(normalized.HydrationRemaining, hydrationSum, 6);
        Assert.Equal(normalized.VolumeFraction, volumeSum, 6);
    }

    // --- Границы ---

    [Fact]
    public void AdvanceClampsToWhatIsLeftAndNeverOverfills()
    {
        var contents = Begin(5d, 0d, portionFraction: 0.1d);
        var expected = PlayerConditionScale.FromPercent(5d);

        // Шаг БОЛЬШЕ времени переваривания: отдать больше остатка невозможно.
        var step = CharacterDigestion.Advance(contents, 60d * Hour);

        Assert.Equal(expected, step.EnergyGain, 3);
        Assert.True(step.Contents.IsEmpty);
        Assert.Equal(0d, step.Contents.EnergyPerGameSecond);

        // Объём освободился вместе с содержимым: пустой желудок снова вмещает литр.
        Assert.Equal(0d, step.Contents.VolumeFraction, 6);

        // Пустой желудок ничего не отдаёт даже на огромном шаге.
        var idle = CharacterDigestion.Advance(step.Contents, 100d * Hour);
        Assert.Equal(0d, idle.EnergyGain);
        Assert.Equal(0d, idle.HydrationGain);
    }

    [Fact]
    public void ZeroPortionDoesNotStartDigestion()
    {
        var contents = Begin(0d, 0d);

        Assert.True(contents.IsEmpty);
        Assert.Equal(0d, contents.EnergyPerGameSecond);
        Assert.Equal(0d, contents.HydrationPerGameSecond);
    }

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

        // Мгновенного восстановления больше нет: вода ждёт в желудке.
        Assert.Equal(
            PlayerConditionScale.FromPercent(40d),
            used.Vitals.Hydration,
            3);

        // Бутылка 500 мл = 16,67% шкалы (шкала — 3000 мл).
        Assert.Equal(
            PlayerConditionScale.UnitsPerPercent * 500d / 30d,
            used.Conditions.Stomach.HydrationRemaining,
            2);

        // Четверть часа усвоения отдаёт 1,35 / 4 ≈ 337,5 мл (было 250 мл при
        // литре в час). Остаток считаем от фактической доли усвоенного, а не
        // вторым «ожидаемым» числом: два расчёта одного и того же разошлись бы
        // при следующей правке скорости.
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

    /// <summary>
    /// ОДИН съеденный предмет — РОВНО ОДНА порция в желудке, даже когда он даёт
    /// и энергию, и жидкость.
    ///
    /// Прямой тест дефекта, который увидел автор: «домашняя колбаса добавляется два
    /// раза», «апельсин тоже два раза», «добавил шоколад, он не появился в
    /// желудке». Питание шло ДВУМЯ вызовами (энергия и жидкость), и каждый
    /// дописывал СВОЮ порцию: яблоко ложилось как «яблоко (энергия)» с таймером
    /// 2 мин и «яблоко (жидкость)» с таймером 7 мин, объём удваивался (0,36 л
    /// вместо 0,18 л), а в списке содержимого предмет стоял двумя строками.
    ///
    /// Проверяется по ВСЕМУ каталогу еды и питья, а не на одном предмете: дефект
    /// был общим для профиля, и одиночная проверка пропустила бы предмет с
    /// нулевым остатком по одной из шкал (у шоколада 2 мл воды — вторая порция
    /// жила 5 секунд и выглядела как «предмет не добавился»).
    /// </summary>
    [Fact]
    public void OneEatenItemIsExactlyOneStomachPortion()
    {
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
                $"«{item.Name}» ({item.Id}) даёт {portions.Count} порции в желудке, " +
                "а должен ровно одну: предмет съеден один раз");

            Assert.Equal(item.Id, portions[0].ItemId);

            // ОБЪЁМ — свойство КУСКА, а не шкалы: он берётся по массе предмета
            // один раз. Две порции описывали бы вдвое больше съеденного, и
            // желудок переполнялся бы вдвое быстрее реального.
            Assert.Equal(
                Math.Max(0.01d, profile.PortionFraction),
                portions[0].VolumeFraction,
                6);

            // Обе ставки живут в ОДНОЙ порции: еда усваивается «полный желудок
            // за 2 часа», жидкость — «1,35 л/ч», и разные скорости обязаны
            // сохраниться, иначе таймер перестал бы отвечать содержимому.
            if (profile.EnergyPercent > 0d)
                Assert.True(portions[0].EnergyPerGameSecond > 0d);
            if (profile.HydrationPercent > 0d)
                Assert.True(portions[0].HydrationPerGameSecond > 0d);

            // Остаток по каждой шкале есть только там, где шкала восполняется:
            // у воды энергия НУЛЕВАЯ. Именно из-за ненулевого остатка по «чужой»
            // шкале раньше появлялась лишняя порция.
            Assert.Equal(profile.EnergyPercent > 0d, portions[0].EnergyRemaining > 0d);
            Assert.Equal(
                profile.HydrationPercent > 0d,
                portions[0].HydrationRemaining > 0d);
        }
    }

    /// <summary>
    /// Объём порции в желудке ВСЕГДА равен объёму выбранного предмета, даже
    /// когда желудок уже чем-то занят.
    ///
    /// Прямой тест дефекта, который увидел автор: «указанный объём в списке не
    /// соответствует добавленному. 5 мл мёда добавилось как 30, 110 мл банана
    /// стало 20 мл, вода добавилась правильно — 500 мл». Механизм был такой:
    /// прежняя модель принимала порцию «по частям» (`accepted = min(порция,
    /// свободно)`) и записывала в порцию ИМЕННО УРЕЗАННУЮ долю. Вода проходила
    /// правильной потому, что её часто пили в пустой желудок — дефект виден
    /// только при ЗАНЯТОМ. Теперь правило «целиком или никак», поэтому объём
    /// порции всегда равен этикетке, а то, что не влезает, попросту не
    /// съедается.
    ///
    /// Проверка идёт по ВСЕМУ каталогу еды и питья И при нескольких уровнях
    /// занятости желудка, с ЗАМЕРОМ ДОБАВЛЕННОГО объёма, а не только
    /// абсолютного: «записано ровно столько, сколько влезло» — это и есть та
    /// подмена, которую автор назвал обманом.
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

            var expected = Math.Max(0.01d, profile.PortionFraction);

            // Занимаем желудок на заданную долю порцией-заглушкой: место уже
            // ограничено, и «урезающая» модель тут же показала бы свой дефект.
            var occupied = occupancy > 0d
                ? Begin(1d, 0d, portionFraction: occupancy)
                : StomachContents.Empty.Normalize();

            var fits = CharacterDigestionReport.Fits(occupied, item.Id);

            var contents = CharacterDigestion.Begin(
                occupied,
                item.Id,
                profile.EnergyPercent,
                profile.HydrationPercent,
                Scale,
                Scale,
                elevatedMetabolism: false,
                reducedMetabolism: false,
                portionFraction: expected);

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

            var volumeDelta = contents.VolumeFraction - occupied.VolumeFraction;

            if (added.Count == 0)
            {
                // Не помещается — желудок НЕ изменился вовсе.
                Assert.Equal(0d, volumeDelta, 6);
                continue;
            }

            // ГЛАВНОЕ: записан объём ПРЕДМЕТА, а не остаток желудка.
            Assert.Equal(expected, added[0].VolumeFraction, 6);
            Assert.Equal(expected, volumeDelta, 6);
            Assert.Equal(occupied.VolumeFraction + expected, contents.VolumeFraction, 6);
        }
    }

    /// <summary>
    /// ОБЪЁМ ПОРЦИИ — ОДНО ЧИСЛО на все места: меню добавления, признак
    /// «поместится», записанная порция и объём в интерфейсе.
    ///
    /// Прямой тест сообщения автора: «Банан 110 мл, а в желудок добавилось 150.
    /// Паёк 250, а добавилось 400». Механика была верна — 150 и 400 это ОБЪЁМЫ
    /// порций; ошибочной была ПОДПИСЬ, она печатала содержимое ВОДЫ (110 и 250).
    /// Тест держит инвариант, который эту подмену делает невозможной: сколько
    /// миллилитров показывает список, столько же занимает порция и столько же
    /// требует признак «влезет».
    ///
    /// Проверка идёт по ВСЕМУ каталогу: подмена возможна у ЛЮБОГО предмета, у
    /// которого вода и объём порции различаются, а таких — большинство еды.
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

            // Ноль бывает только у предметов без места в желудке (мыло). Тогда
            // движок подставляет минимум 0,01, и это описано отдельно — здесь
            // проверяем именно ПОЛОЖИТЕЛЬНЫЕ объёмы, где подпись обязана совпасть
            // с записью в точности.
            if (portion <= 0d)
                continue;

            var expected = CharacterDigestion.PortionFraction(portion);

            // Движок задаёт МИНИМУМ порции (0,01 л): иначе мелкая позиция (пакет
            // сахара) занимала бы ноль и усваивалась мгновенно, а признак
            // «влезет» становился бы бессмысленным. Здесь этот же минимум
            // применяется к ОЖИДАНИЮ, иначе проверка требовала бы от движка то,
            // чего он не обещает.
            var appliedFraction = Math.Max(
                CharacterDigestion.MinimumPortionFraction,
                expected);

            var stomach = CharacterDigestion.Begin(
                StomachContents.Empty,
                item.Id,
                CharacterConsumableCatalog.GetProfile(item.Id).EnergyPercent,
                CharacterConsumableCatalog.GetProfile(item.Id).HydrationPercent,
                Scale,
                Scale,
                elevatedMetabolism: false,
                reducedMetabolism: false,
                portionFraction: appliedFraction);

            // Предмет без питательности порции не заводит — это законно.
            if (stomach.Portions.Count == 0)
                continue;

            checkedAny = true;

            // 1. ПОДПИСЬ: миллилитры порции дают ту долю литра, которую записал
            //    домен. У позиций мельче минимума доля равна минимуму — и это
            //    правильное поведение: место в желудке есть, просто порция
            //    крошечная. Округление до целых миллилитров не должно расходиться
            //    больше чем на единицу округления ДОЛИ.
            Assert.Equal(
                appliedFraction,
                stomach.Portions[0].VolumeFraction,
                6);

            // 2. ЗАПИСЬ: записанная доля, переведённая обратно в миллилитры,
            //    равна подписанным миллилитрам. Это и есть «добавилось ровно
            //    столько, сколько указано в списке».
            Assert.Equal(
                portion,
                CharacterDigestionReport
                    .Portions(stomach)[0]
                    .VolumeMilliliters,
                3);

            // 3. ПРИЗНАК: объём порции не превышает ОБЪЁМ ЖЕЛУДКА, поэтому
            //    порция помещается в пустой желудок. Список и движок судят одним
            //    числом.
            //
            //    Проверка идёт против ПУСТОГО желудка, а не против желудка с этой
            //    порцией. Прежний вариант строил желудок из той же порции и
            //    спрашивал `Fits` — а `Fits` смотрит на СВОБОДНУЮ часть, поэтому
            //    он молча требовал, чтобы порция была не больше ПОЛОВИНЫ желудка
            //    (0,5 л из 1 л). Половинное правило ни автором не задано, ни в
            //    движке не живёт: там приём идёт по «целиком или никак» от
            //    свободного места. Дефект вскрылся, когда в каталог попали «Соли
            //    для восстановления организма» — 1000 мл, ровно на весь желудок.
            Assert.True(
                CharacterDigestionReport.Fits(
                    StomachContents.Empty,
                    item.Id),
                $"«{item.Name}» ({item.Id}) не поместился в ПУСТОЙ желудок — " +
                "объём порции больше объёма желудка");
        }

        Assert.True(
            checkedAny,
            "ни один предмет каталога не проверен: фикстура вырождена");
    }
}
