using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Пищеварение: еда и питьё восстанавливают шкалы ПОСТЕПЕННО, а не мгновенно.
///
/// Тесты держат ФИЗИЧЕСКИЕ величины автора, а не «эталонные минуты» прежней
/// модели:
///   • шкала энергии — 5000 ккал (10000 единиц хранения), жидкость — 15000 мл;
///   • объём желудка — 1 литр;
///   • полный желудок ЕДЫ уходит за 2 игровых часа (5000 единиц в час);
///   • ЖИДКОСТЬ усваивается 1,35 литра в час (900 единиц в час), потому что
///     автор попросил «+10 единиц в минуту» на своём замере «+7,4» (пониженный
///     метаболизм замедляет пищеварение в 1,5 раза);
///   • при нормальной нагрузке расход энергии 33,3%, жидкости 11,1% в час.
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
        double portionFraction = 1d) =>
        CharacterDigestion.Begin(
            StomachContents.Empty,
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
        // Ставка выведена из замера автора: он видел +7,4 ед./мин при
        // пониженном метаболизме и попросил +10 ед./мин, отсюда 1,35 л/ч.
        // Полная шкала жидкости — 15 литров, поэтому опустошается примерно
        // за 15 / 1,35 ≈ 11,1 игрового часа.
        var drink = Begin(0d, 100d);

        Assert.Equal(
            HydrationScaleLiters / CharacterDigestion.HydrationAbsorptionLitersPerHour * Hour,
            drink.HydrationRemaining / drink.HydrationPerGameSecond,
            3);

        // 1350 мл в час = 1350/15000 шкалы = 900 единиц в час.
        Assert.Equal(
            1350d / CharacterDigestion.HydrationScaleMilliliters * Scale,
            drink.HydrationPerGameSecond * Hour,
            3);
    }

    [Fact]
    public void DrinkingGivesTenUnitsPerMinuteUnderReducedMetabolism()
    {
        // РОВНО ТО ЧИСЛО, которое назвал автор: «сейчас вода переваривается
        // +7,4 единицы в минуту, делаем +10». Замер был сделан при ПОНИЖЕННОМ
        // метаболизме (он замедляет пищеварение в 1,5 раза и превращал прежний
        // литр в час в 11,1 / 1,5 = 7,4), поэтому проверяем именно это
        // состояние: иначе новое число нельзя сверить с жалобой.
        var litre = Begin(
            0d,
            1000d / 150d,
            reduced: true);

        Assert.Equal(
            10d,
            litre.HydrationPerGameSecond * 60d,
            2);

        // ПРЕЖНЕЕ время для того же литра при том же состоянии: ставка была
        // 10000 × (1000/15000) / 1,5 = 444,4 единицы в час, а объём — 616,7
        // единиц (литр минус штраф 0,5% шкалы), то есть 4995 секунд. Теперь
        // литр уходит за 3700 секунд. Сравнение с прежним числом и есть смысл
        // правки: питьё перестало отставать от расхода, а не просто «стало
        // больше».
        var previousRatePerHour =
            Scale *
            (1000d / CharacterDigestion.HydrationScaleMilliliters) /
            CharacterDigestion.ReducedMetabolismSpeedFactor;
        var previousDuration =
            (PlayerConditionScale.UnitsPerPercent * 1000d / 150d -
             Scale * CharacterDigestion.ReducedMetabolismVolumePenalty) /
            previousRatePerHour *
            Hour;

        var duration =
            litre.HydrationRemaining / litre.HydrationPerGameSecond;

        Assert.True(
            duration < previousDuration,
            $"литр обязан усваиваться быстрее прежнего: {duration} с против " +
            $"{previousDuration} с");
        Assert.InRange(duration, 3600d, 3800d);
    }

    [Fact]
    public void DrinkingOutpacesConsumptionUnderReducedMetabolism()
    {
        // Прежний литр в час при пониженном метаболизме давал 7,4 ед./мин
        // против расхода 8,33 ед./мин (100% шкалы за 90 часов ×0,9) — то есть
        // питьё НЕ перекрывало расход, и динамика оставалась падающей. Это и
        // была жалоба автора; теперь запас есть в любом нормальном состоянии.
        var stomach = Begin(0d, 100d, reduced: true);

        var netPerHour =
            stomach.HydrationPerGameSecond * Hour -
            Scale / CharacterVitalsEngine.HydrationConsumptionHours * 0.9d;

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
        // 1 литр в час (666,67 ед./ч) обязан быть БОЛЬШЕ расхода шкалы жидкости
        // (вся шкала за HydrationConsumptionHours). Иначе выпитая вода не давала
        // бы положительной динамики — именно на это жаловался автор.
        var absorptionPerHour =
            CharacterDigestion.HydrationAbsorptionLitersPerHour *
            1000d /
            CharacterDigestion.HydrationScaleMilliliters *
            Scale;

        var consumptionPerHour =
            Scale / CharacterVitalsEngine.HydrationConsumptionHours;

        Assert.True(
            absorptionPerHour > consumptionPerHour,
            $"усвоение {absorptionPerHour} ед./ч обязано перекрывать расход " +
            $"{consumptionPerHour} ед./ч");
    }

    [Fact]
    public void FoodOutpacesItsOwnConsumption()
    {
        // Полный желудок еды отдаёт 5000 ед./ч против расхода 333,3 ед./ч —
        // пятнадцатикратный запас.
        var absorptionPerHour =
            Scale / CharacterDigestion.FoodStomachEmptyHours;
        var consumptionPerHour =
            Scale / CharacterVitalsEngine.EnergyConsumptionHours;

        Assert.True(
            absorptionPerHour > consumptionPerHour * 10d,
            $"усвоение {absorptionPerHour} ед./ч обязано многократно перекрывать " +
            $"расход {consumptionPerHour} ед./ч");
    }

    [Fact]
    public void WaterBottleGivesPositiveHydrationDynamic()
    {
        // Бутылка воды 500 мл: 500/15000 шкалы = 3,33% = 333,3 единицы, и она
        // поступает со скоростью 900 ед./ч против расхода 111,1 ед./ч.
        var profile = CharacterConsumableCatalog.GetProfile("water.bottle");

        Assert.Equal(500d, profile.WaterMilliliters, 3);
        Assert.Equal(500d / 150d, profile.HydrationPercent, 3);
        Assert.Equal(0d, profile.EnergyPercent, 3);

        var stomach = CharacterDigestion.Begin(
            StomachContents.Empty,
            0d,
            profile.HydrationPercent,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: profile.PortionFraction);

        var netPerHour =
            stomach.HydrationPerGameSecond * Hour -
            Scale / CharacterVitalsEngine.HydrationConsumptionHours;

        Assert.True(
            netPerHour > 0d,
            $"бутылка воды обязана давать положительную динамику, а даёт {netPerHour} ед./ч");
    }

    [Fact]
    public void EggSandwichGivesPositiveEnergyDynamic()
    {
        // Бутерброд 180 г / 350 ккал: 350/50 = 7% шкалы, усвоение ~1944 ед./ч
        // против расхода 333,3 ед./ч.
        var profile = CharacterConsumableCatalog.GetProfile("food.egg_sandwich");

        Assert.Equal(350d, profile.Kilocalories, 3);
        Assert.Equal(7d, profile.EnergyPercent, 3);

        var stomach = CharacterDigestion.Begin(
            StomachContents.Empty,
            profile.EnergyPercent,
            0d,
            Scale,
            Scale,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: profile.PortionFraction);

        var netPerHour =
            stomach.EnergyPerGameSecond * Hour -
            Scale / CharacterVitalsEngine.EnergyConsumptionHours;

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

        // Энергия усваивается быстрее (весь объём шкалы за 2 часа против
        // 1350 мл в час у жидкости), поэтому длительность — по ЖИДКОСТИ:
        // 10% шкалы из 900 единиц в час ≈ 1,11 игрового часа.
        //
        // Допуск — одна минута, потому что Drain шагает МИНУТАМИ и
        // останавливается на первом пустом желудке: последний шаг законно
        // перескакивает границу. Проверять здесь доли секунды значило бы
        // требовать точности, которой у замера нет.
        Assert.InRange(
            result.Seconds,
            expectedHydration / (1350d / CharacterDigestion.HydrationScaleMilliliters * Scale) * Hour - Minute,
            expectedHydration / (1350d / CharacterDigestion.HydrationScaleMilliliters * Scale) * Hour + Minute);
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
            20d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.25d);

        // Остаток сложился, а ставка НЕ выросла: две порции дают вдвое больше
        // объёма за вдвое большее время, а не «двойную скорость».
        Assert.Equal(
            PlayerConditionScale.FromPercent(30d),
            second.EnergyRemaining,
            3);
        Assert.Equal(
            first.EnergyPerGameSecond,
            second.EnergyPerGameSecond,
            6);
        Assert.Equal(
            0.5d,
            second.VolumeFraction,
            6);
    }

    [Fact]
    public void StomachRejectsPortionThatDoesNotFit()
    {
        // Объём желудка — 1 литр. Две порции по 0,75 литра не могут уместиться
        // вдвоём: вторая принимается лишь на четверть и занимает литр.
        var first = Begin(10d, 0d, portionFraction: 0.75d);

        Assert.Equal(0.75d, first.VolumeFraction, 6);

        var second = CharacterDigestion.Begin(
            first,
            10d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.75d);

        Assert.Equal(1d, second.VolumeFraction, 6);

        // Принята четверть второй порции — треть её номинала (0,25 из 0,75):
        // 10% + 10% / 3 ≈ 13,33% шкалы.
        Assert.Equal(
            PlayerConditionScale.UnitsPerPercent * 40d / 3d,
            second.EnergyRemaining,
            1);

        // Полный желудок больше не принимает ничего: лишнее не съедается.
        var third = CharacterDigestion.Begin(
            second,
            10d,
            0d,
            PlayerConditionScale.Maximum,
            PlayerConditionScale.Maximum,
            elevatedMetabolism: false,
            reducedMetabolism: false,
            portionFraction: 0.75d);

        Assert.Equal(
            second.EnergyRemaining,
            third.EnergyRemaining,
            6);
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

        // Бутылка 500 мл = 3,33% шкалы.
        Assert.Equal(
            PlayerConditionScale.UnitsPerPercent * 500d / 150d,
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
            PlayerConditionScale.UnitsPerPercent * 337.5d / 150d,
            absorbed,
            2);

        // Шкала за это время выросла ОТНОСИТЕЛЬНО исходной: приток 337,5 мл в час
        // перекрывает расход шкалы (111,1 единиц в час).
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
}
