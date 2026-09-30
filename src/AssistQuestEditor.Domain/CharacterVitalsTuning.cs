namespace AssistQuestEditor.Domain;

/// <summary>
/// ┌───────────────────────────────────────────────────────────────────────────┐
/// │ ЕДИНЫЙ ФАЙЛ КАЛИБРОВКИ СОСТОЯНИЯ ПЕРСОНАЖА                                │
/// │                                                                           │
/// │ Здесь собраны ВСЕ числа, проценты, скорости, пороги и множители формул    │
/// │ шкал: здоровье, стресс, энергия, жидкость, усталость, устойчивость,       │
/// │ метаболизм. Правишь ОДИН этот файл — меняешь поведение всех шкал.         │
/// │                                                                           │
/// │ Как пользоваться.                                                        │
/// │   1. Найди нужную ГРУППУ констант по заголовку секции.                    │
/// │   2. У каждой строки написано, ЧТО именно она меняет и в какую сторону.   │
/// │   3. Меняй ТОЛЬКО значение справа от `=`, не трогая имя: движок и         │
/// │      подсказки интерфейса читают эти имена.                               │
/// │   4. После правки: `dotnet test ...` и `ci\run_local.ps1 -Suite fast`.     │
/// │                                                                           │
/// │ Почему отдельный класс, а не правка CharacterVitalsEngine напрямую.       │
/// │ Движок — это ЛОГИКА (когда применить штраф, как посчитать шаг), а здесь —  │
/// │ только ЧИСЛА. Когда они перемешаны, любая правка баланса требует чтения   │
/// │ алгоритма, а любая правка алгоритма — перечитывания всех чисел.           │
/// │                                                                           │
/// │ CharacterVitalsEngine и CharacterDigestion ОСТАВЛЕНЫ как псевдонимы на     │
/// │ эти константы: существующий код и тесты продолжают компилироваться, а     │
/// │ «где менять число» — ровно одно место.                                    │
/// └───────────────────────────────────────────────────────────────────────────┘
///
/// Единицы измерения, принятые во ВСЕХ формулах ниже:
///   • проценты шкалы    — 0..100. «1%» — сотая часть полной шкалы;
///   • процент/минуту    — скорость изменения шкалы за ИГРОВУЮ минуту;
///   • процент/час       — то же за игровой час (1 игровой час = 60 игровых минут);
///   • секунды           — ИГРОВЫЕ секунды (1:1 с реальными, но при ускорении
///                         времени движок получает больше игровых секунд за тик).
/// Внутри движок хранит шкалы в единицах 0..10000 («единицы хранения»):
/// 100 единиц = 1%. Числа этого файла — в процентах, как задано автором.
/// </summary>
public static class CharacterVitalsTuning
{
    // ═══════════════════════════════════════════════════════════════════════
    // 1. РАЗМЕРЫ ШКАЛ (физический потолок)
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Шкала хранения всегда 0..10000 единиц. Эти два числа задают, СКОЛЬКО
    // ФИЗИЧЕСКИХ величин помещается в 100% шкалы — именно они показываются
    // игроку: «2750 / 5000 ккал», «2100 / 3000 мл».

    /// <summary>
    /// Полная шкала ЭНЕРГИИ в килокалориях. Меняя это число, помни, что от него
    /// считаются ВСЕ скорости энергии (см. секцию 3): 100% шкалы = столько ккал.
    /// Увеличение числа делает шкалу «длиннее» — расход в процентах падает.
    /// </summary>
    public const double EnergyScaleKilocalories = 5000d;

    /// <summary>
    /// Полная шкала ЖИДКОСТИ в миллилитрах. Тот же смысл, что у энергии: 100%
    /// шкалы = столько мл. Осторожно: пороги жажды/обезвоживания (секция 5)
    /// заданы в МИЛЛИЛИТРАХ и пересчитываются в проценты ОТ ЭТОГО числа, поэтому
    /// уменьшение шкалы приближает дебаффы (при 3000 мл жажда начинается на 50%,
    /// а не на 10%, как было при 15 000 мл).
    /// </summary>
    public const double HydrationScaleMilliliters = 3000d;

    // ═══════════════════════════════════════════════════════════════════════
    // 2. ПИЩЕВАРЕНИЕ: объём, пропускная способность, метаболизм
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Модель описана в `DigestionRefactoring.md` и живёт в CharacterDigestion.
    // Коротко: у пищеварения есть ДВА независимых канала вывода — «сухой»
    // (белки, жиры, углеводы) и «жидкий» (вода, напитки). Объём, который
    // покидает пищеварение за минуту, задан ПРОПУСКНОЙ СПОСОБНОСТЬЮ, а не
    // таймером порции: смесь разных продуктов в пищеварении только переделяет
    // этот лимит между ними, отчего каждый усваивается дольше.
    //
    // Как считается (единицы — миллилитры порции, для воды миллилитр = грамм):
    //   • у каждой порции известно, сколько в ней ВОДЫ (доля 0..1) и сколько
    //     калорий на миллилитр;
    //   • пропускная способность каждого канала ВЫВОДИТСЯ из физических норм
    //     автора ниже: 2000 мл канала за 2 часа — 16,67 мл/мин у сухого канала
    //     и 1350 мл/ч — 22,5 мл/мин у жидкого;
    //   • сухая часть каждой порции уходит со СВОЕЙ ставкой (по массе её сухого
    //     остатка), жидкая — со своей, поэтому вода обгоняет плотную еду;
    //   • если суммарный сухой остаток меньше пропускной способности канала,
    //     освободившаяся пропускная способность уходит воде (её много, и она
    //     не должна ждать).

    /// <summary>
    /// Полный объём ПИЩЕВАРЕНИЯ в литрах. Было 1 (желудок), стало 2: по
    /// документу это «средний комфортный объём желудка + начального отдела
    /// кишечника». Ограничивает, сколько еды можно «загрузить» заранее: порция
    /// больше свободного места целиком не влезает. На СКОРОСТЬ усвоения влияет
    /// только через пропускную способность (см. ниже) — прежняя модель считала
    /// время прямо от размера порции.
    /// </summary>
    public const double StomachVolumeLiters = 2d;

    /// <summary>
    /// Пропускная способность СУХОГО канала в миллилитрах за игровой час.
    /// Выведена из заданного автором «объём пищеварения за 2 игровых часа»:
    /// 2000 мл / 2 ч = 1000 мл/ч ≈ 16,67 мл/мин.
    ///
    /// Именно она, а не таймер порции, задаёт, сколько еды покидает пищеварение
    /// за минуту. Меньше — еда усваивается дольше и восстанавливает шкалы
    /// медленнее; больше — быстрее.
    /// </summary>
    public const double DryThroughputLitersPerHour =
        StomachVolumeLiters / FoodStomachEmptyHours;

    /// <summary>
    /// За сколько игровых часов пищеварение пропускает свой полный объём СУХОЙ
    /// пищи. Это исходная норма автора («2000 мл за 2 часа»), от которой
    /// выведена пропускная способность выше. Менять норму нужно ЗДЕСЬ.
    /// </summary>
    public const double FoodStomachEmptyHours = 2d;

    /// <summary>
    /// Пропускная способность ЖИДКОГО канала в миллилитрах за игровой час.
    /// Задана ФИЗИОЛОГИЧЕСКИ («организм усваивает 1,35 литра воды в час») и от
    /// размера шкалы жидкости не зависит: литр воды остаётся литром.
    /// </summary>
    public const double HydrationAbsorptionLitersPerHour = 1.35d;

    /// <summary>
    /// За сколько игровых часов усваивается один литр жидкости. ВЫВЕДЕНО из
    /// скорости усвоения, чтобы «литр за N часов» в подсказке не разошёлся с
    /// начислением. Менять нужно скорость выше, а не это число.
    /// </summary>
    public const double HoursPerLitreOfWater =
        1d / HydrationAbsorptionLitersPerHour;

    /// <summary>
    /// Эталонные объёмы порций, к которым привязаны каталоги предметов: «порция
    /// еды» = 500 г, «порция питья» = 500 мл. Нужны, чтобы пересчитать питательную
    /// ценность предмета в проценты шкалы.
    /// </summary>
    public const double ReferenceFoodGrams = 500d;
    public const double ReferenceDrinkMilliliters = 500d;

    /// <summary>
    /// ПОВЫШЕННЫЙ метаболизм ускоряет пищеварение в 1 / ЭТО_ЧИСЛО раз
    /// (задано «в 2,5 раза быстрее» → 1 / 2,5 = 0,4).
    /// </summary>
    public const double ElevatedMetabolismSpeedFactor = 1d / 2.5d;

    /// <summary>
    /// ПОНИЖЕННЫЙ метаболизм замедляет пищеварение в ЭТО раз (задано «в 1,5 раза
    /// медленнее» → 1,5). Слишком большое число сделает еду бесполезной: приток
    /// перестанет перекрывать расход.
    /// </summary>
    public const double ReducedMetabolismSpeedFactor = 1.5d;

    /// <summary>
    /// Бонус и штраф ОБЪЁМА восстановления при отклонении метаболизма, как доля
    /// шкалы (0,005 = 0,5% шкалы). Повышенный даёт +0,5% объёма, пониженный
    /// отнимает столько же.
    /// </summary>
    public const double ElevatedMetabolismVolumeBonus = 0.005d;
    public const double ReducedMetabolismVolumePenalty = 0.005d;

    // ═══════════════════════════════════════════════════════════════════════
    // 3. РАСХОД ЭНЕРГИИ И ЖИДКОСТИ (зависит от нагрузки)
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Коэффициенты — ГЛАВНЫЕ ручки расхода: ими задано, во сколько раз норма
    // отличается от эталона. Эталон — 2500 ккал за 3 часа и 300 мл за час под
    // нагрузкой, 2500 ккал за 8 часов и 120 мл за час в покое. Менять расход
    // удобно ЗДЕСЬ, коэффициентами:
    //   • EnergyLoadCoefficient   (1,5 → 3750 ккал/ч = 75%/ч шкалы 5000);
    //   • EnergyRestCoefficient   (1   → 312,5 ккал/ч = 6,25%/ч);
    //   • HydrationLoadCoefficient(2   → 600 мл/ч = 20%/ч шкалы 3000);
    //   • HydrationRestCoefficient(1   → 120 мл/ч = 4%/ч).
    //
    // Формула: взять норму ниже → поделить на размер шкалы → получить процент
    // шкалы за час. Пример: (3750 / 5000) × 100 = 75% шкалы за игровой час.

    /// <summary>Расход ЭНЕРГИИ под нагрузкой относительно эталона (см. блок выше).</summary>
    public const double EnergyLoadCoefficient = 1.5d;

    /// <summary>Расход ЭНЕРГИИ в покое относительно эталона.</summary>
    public const double EnergyRestCoefficient = 1d;

    /// <summary>Расход ЖИДКОСТИ под нагрузкой относительно эталона.</summary>
    public const double HydrationLoadCoefficient = 2d;

    /// <summary>Расход ЖИДКОСТИ в покое относительно эталона.</summary>
    public const double HydrationRestCoefficient = 1d;

    /// <summary>Норма расхода ЭНЕРГИИ при НАГРУЗКЕ (движение по маршруту), ккал/час.
    /// Эталон автора — 2500 ккал за 3 игровых часа — множится на коэффициент нагрузки.</summary>
    public const double EnergyKilocaloriesMovingPerHour =
        2500d * EnergyLoadCoefficient / 3d;

    /// <summary>Норма расхода ЭНЕРГИИ В ПОКОЕ, ккал/час.
    /// Эталон автора — 2500 ккал за 8 игровых часов — множится на коэффициент покоя.</summary>
    public const double EnergyKilocaloriesRestingPerHour =
        2500d * EnergyRestCoefficient / 8d;

    /// <summary>Норма расхода ЖИДКОСТИ при НАГРУЗКЕ, мл/час.
    /// Эталон автора — 150 мл за 30 игровых минут (то есть 300 мл/час) — множится на коэффициент нагрузки.</summary>
    public const double HydrationMillilitersMovingPerHour =
        300d * HydrationLoadCoefficient;

    /// <summary>Норма расхода ЖИДКОСТИ В ПОКОЕ, мл/час.
    /// Эталон автора — 60 мл за 30 игровых минут (то есть 120 мл/час) — множится на коэффициент покоя.</summary>
    public const double HydrationMillilitersRestingPerHour =
        120d * HydrationRestCoefficient;

    /// <summary>
    /// Во сколько раз расход ПАДАЕТ во сне (1/3 — втрое меньше). Общий для
    /// энергии и жидкости.
    /// </summary>
    public const double SleepConsumptionMultiplier = 1d / 3d;

    // ═══════════════════════════════════════════════════════════════════════
    // 4. УСТАЛОСТЬ И СТРЕСС (накопление, пороги, восстановление)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>С какого процента усталости начинается критическая зона (истощение).</summary>
    public const double FatigueCriticalPercent = 80d;

    /// <summary>С какого процента усталости идёт истощение усталости.</summary>
    public const double FatigueExhaustionPercent = 90d;

    /// <summary>За сколько игровых ЧАСОВ движения усталость набирается до 100%.
    /// Больше — усталость растёт медленнее.</summary>
    public const double FatigueBuildHours = 18d;

    /// <summary>За сколько игровых часов В ПОКОЕ усталость спадает со 100% до 0.
    /// Больше — отдых медленнее.</summary>
    public const double FatigueRestHours = 9d;

    /// <summary>За сколько КИЛОМЕТРОВ пути усталость набирается до 100%.
    /// Больше — дальние перегоны даются легче.</summary>
    public const double FatigueBuildKilometers = 900d;

    /// <summary>С какого процента стресса начинается критическая зона (риск «Выгорания»).</summary>
    public const double StressCriticalPercent = 50d;

    /// <summary>
    /// Базовый множитель накопления КУМУЛЯТИВНОЙ усталости (истощения усталости).
    /// Больше — «лавина» истощения наступает быстрее.
    /// </summary>
    public const double CumulativeFatigueAccrualBaseMultiplier = 3d;

    /// <summary>За каждые столько процентов кумулятивной усталости множитель растёт.</summary>
    public const double CumulativeFatigueAccrualStepPercent = 5d;

    /// <summary>Насколько растёт множитель за каждые накопленные 5% (см. выше).</summary>
    public const double CumulativeFatigueAccrualStepBonus = 0.5d;

    /// <summary>Сколько КУМУЛЯТИВНОЙ усталости добавляет каждый час, проведённый
    /// с усталостью выше критической (в процентах шкалы истощения, 1 — «1% в час»).</summary>
    public const double CumulativeFatiguePerCriticalHour = 1d;

    /// <summary>Сколько КУМУЛЯТИВНОГО стресса добавляет каждый час, проведённый
    /// со стрессом выше критического.</summary>
    public const double CumulativeStressPerCriticalHour = 1d;

    // ═══════════════════════════════════════════════════════════════════════
    // 5. ПИТЬЁ: «ЖАЖДА» И «ОБЕЗВОЖИВАНИЕ»
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Пороги заданы в МИЛЛИЛИТРАХ (так их называл автор), а движок работает в
    // процентах — поэтому ниже они ПЕРЕСЧИТЫВАЮТСЯ от размера шкалы. Меняя
    // размер шкалы (секция 1), помни: порог в процентах поедет вместе с ним.

    /// <summary>
    /// Порог «ЖАЖДЫ» в миллилитрах: ниже него копится истощение жидкости, растёт
    /// стресс и падает метаболизм. Больше — жажда начинается раньше.
    /// </summary>
    public const double ThirstThresholdMilliliters = 1500d;

    /// <summary>
    /// Порог «ОБЕЗВОЖИВАНИЯ» в миллилитрах: ниже него метаболизм теряется вдвое
    /// быстрее, стресс растёт на 25% быстрее, падает устойчивость. Больше —
    /// обезвоживание начинается раньше.
    /// </summary>
    public const double DehydrationThresholdMilliliters = 500d;

    /// <summary>
    /// Порог критической нехватки жидкости в ПРОЦЕНТАХ шкалы: ниже него удваивается
    /// истощение, отнимается здоровье и копится истощение стресса. Задано автором
    /// как «при показателе шкалы меньше 1».
    /// </summary>
    public const double CriticalHydrationPercent = 1d;

    /// <summary>«ЖАЖДА»: прибавка к скорости накопления стресса, %/минуту.
    /// АДДИТИВНАЯ (прибавляется к текущей скорости, не умножает её).</summary>
    public const double ThirstStressPercentPerMinute = 0.5d;

    /// <summary>«ЖАЖДА»: потеря метаболизма, % за 10 игровых минут.</summary>
    public const double ThirstMetabolismPercentPerTenMinutes = 1d;

    /// <summary>«ОБЕЗВОЖИВАНИЕ»: МНОЖИТЕЛЬ скорости накопления стресса (×1,25).
    /// Единственный множитель среди штрафов: остальные аддитивны.</summary>
    public const double DehydrationStressMultiplier = 1.25d;

    /// <summary>«ОБЕЗВОЖИВАНИЕ»: потеря метаболизма, % за 10 игровых минут
    /// (вдвое быстрее жажды).</summary>
    public const double DehydrationMetabolismPercentPerTenMinutes = 2d;

    /// <summary>Критическая нехватка жидкости: потеря здоровья, % шкалы за игровой час.</summary>
    public const double CriticalHydrationHealthLossPercentPerHour = 100d;

    /// <summary>Критическая нехватка жидкости: во сколько раз быстрее копится
    /// истощение жидкости.</summary>
    public const double CriticalHydrationExhaustionDoubleFactor = 2d;

    /// <summary>Критическая нехватка жидкости: скорость накопления истощения
    /// СТРЕССА, % за игровой час.</summary>
    public const double CriticalHydrationStressExhaustionPercentPerHour = 1d;

    // ═══════════════════════════════════════════════════════════════════════
    // 6. СОН, ГИГИЕНА, ВОССТАНОВЛЕНИЕ ЗДОРОВЬЯ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>За сколько часов ПОЛНОЦЕННОГО сна уходит весь обычный стресс.</summary>
    public const double FullSleepStressClearHours = 6d;

    /// <summary>Во сколько раз ОБЫЧНЫЙ сон снимает стресс медленнее полноценного.</summary>
    public const double RegularSleepStressClearPenalty = 2.5d;

    /// <summary>Цена восстановления здоровья: сколько единиц энергии тратит
    /// одна единица здоровья (1:1).</summary>
    public const double HealthRegenEnergyPerHealthUnit = 1d;

    /// <summary>Цена восстановления здоровья: сколько единиц жидкости тратит
    /// одна единица здоровья (1:2).</summary>
    public const double HealthRegenHydrationPerHealthUnit = 2d;

    /// <summary>Базовое восстановление здоровья: 1% за столько игровых минут.
    /// Больше — здоровье восстанавливается медленнее.</summary>
    public const double HealthRegenGameMinutesPerPercent = 10d;

    /// <summary>Надбавка к восстановлению здоровья за каждое ПОВЫШЕННОЕ свойство
    /// (метаболизм выше 60%, устойчивость выше 60%), % за те же 10 минут.</summary>
    public const double HealthRegenElevatedBonusPercent = 1d;

    /// <summary>Надбавка от баффа «Бык», % за те же 10 игровых минут.</summary>
    public const double HealthRegenBullBonusPercent = 2d;

    /// <summary>Во сколько раз усталость набирается быстрее, пока здоровье
    /// восстанавливается.</summary>
    public const double HealthRegenFatigueMultiplier = 2d;

    /// <summary>За сколько игровых часов гигиена падает со 100% до 0.</summary>
    public const double HygieneDecayHours = 72d;

    // ═══════════════════════════════════════════════════════════════════════
    // 7. УСТОЙЧИВОСТЬ И МЕТАБОЛИЗМ
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Номинал УСТОЙЧИВОСТИ, к которому шкала возвращается сама (%).</summary>
    public const double DefaultResiliencePercent = 60d;

    /// <summary>Номинал МЕТАБОЛИЗМА, к которому шкала возвращается сама (%).</summary>
    public const double DefaultMetabolismPercent = 60d;

    /// <summary>Границы нормального метаболизма: НИЖЕ этого расход замедлен.</summary>
    public const double ReducedMetabolismPercent = 45d;

    /// <summary>
    /// ВЕРХНЯЯ ГРАНИЦА НОМИНАЛЬНОГО КОРИДОРА метаболизма: от неё расход ускорен.
    ///
    /// ВНИМАНИЕ, ДВА РАЗНЫХ СМЫСЛА СЛОВА «ПОВЫШЕННЫЙ» — это не дублирование, а
    /// разделение понятий, которое вводится нарочно.
    ///
    ///   • «ВЫШЕ НОМИНАЛА» (> <see cref="DefaultMetabolismPercent"/>) — свойство
    ///     работает ЛУЧШЕ обычного: ускоряется восстановление здоровья. Мера
    ///     авторская: «повышенный метаболизм даёт +1% за 10 игровых минут».
    ///   • «ПОВЫШЕННЫЙ» (≥ этого числа) — организм ВЫШЕЛ ЗА ГРАНИЦУ коридора:
    ///     растёт расход, ускоряется пищеварение и заживление, растёт объём
    ///     восстановления. Это уже не «лучше обычного», а другое состояние.
    ///
    /// До правки оба смысла обозначались одним порогом и одним словом: автор
    /// поднял метаболизм до 61% и увидел строку «Повышенный метаболизм», тогда
    /// как расход, пищеварение и объём включались лишь с 75%. Теперь подписи в
    /// интерфейсе обязаны называть эти состояния РАЗНЫМИ словами.
    /// </summary>
    public const double ElevatedMetabolismPercent = 75d;

    /// <summary>
    /// Процент, на котором пропорциональные надбавки к восстановлению здоровья
    /// (за метаболизм и за устойчивость) выходят на ПОЛНЫЙ размер.
    ///
    /// Задано автором: надбавка растёт ЛИНЕЙНО от номинала 60% до 75% и дальше
    /// не увеличивается. Прежняя модель включала надбавку флагом «выше 60%» —
    /// 61% и 100% давали ровно одно и то же, хотя автор просил «процент шкалы
    /// влияет на ВЕЛИЧИНУ восстановления».
    /// </summary>
    public const double ElevatedBonusFullPercent = 75d;

    /// <summary>
    /// Во сколько раз устойчивость делится, чтобы дать ШАНС пропустить порцию
    /// истощения: «R/2 процентов шанса не получить очередную порцию». При
    /// номинале 60% это 30%.
    ///
    /// Число вынесено из кода нарочно: раньше делитель был литералом <c>2d</c> в
    /// шести местах, и монитор показателей не мог назвать игроку величину этого
    /// шанса, хотя он самый сильный эффект устойчивости в игре.
    /// </summary>
    public const double ExhaustionResilienceDivisor = 2d;

    /// <summary>Скорость возврата метаболизма к номиналу, % за игровой час.</summary>
    public const double MetabolismRecoveryPercentPerHour = 1d;

    /// <summary>Штраф метаболизма за КАЖДУЮ накопленную шкалу истощения,
    /// % за 15 игровых минут (то есть ×4 в час).</summary>
    public const double MetabolismExhaustionPenaltyPerQuarterHour = 2d;

    /// <summary>Сколько стресса даёт КАЖДЫЙ процент кумулятивной усталости
    /// за игровой час (0.5 — «за 2% усталости 1% стресса в час»).</summary>
    public const double StressPerCumulativeFatiguePercentPerHour = 0.5d;

    /// <summary>Насколько устойчивость гасит прирост стресса. Формула:
    /// 1 − устойчивость / 400. То есть 400 = «полная устойчивость обнуляет
    /// стресс», 100 = «максимум −25%».</summary>
    public const double StressResilienceDivisor = 400d;

    /// <summary>Возврат УСТОЙЧИВОСТИ к номиналу, % за 15 игровых минут, когда
    /// дебаффа «Бомж» нет.</summary>
    public const double ResilienceReturnPercentPerQuarterHour = 0.5d;

    /// <summary>Возврат УСТОЙЧИВОСТИ, когда висит дебафф «Бомж», % за 15 минут
    /// (вдвое быстрее обычного — дебафф быстро «тянет» шкалу к своему потолку).</summary>
    public const double ResilienceBumReturnPercentPerQuarterHour = 1d;

    /// <summary>Потолок устойчивости под дебаффом «Бомж» (%).</summary>
    public const double BumResilienceCapPercent = 50d;

    // ═══════════════════════════════════════════════════════════════════════
    // 8. МЕТАБОЛИЗМ: влияние на расход и на пищеварение
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Множитель расхода ресурсов при ПОНИЖЕННОМ метаболизме
    /// (ниже <see cref="ReducedMetabolismPercent"/>). Меньше 1 — организм
    /// тратит меньше.</summary>
    public const double ReducedMetabolismConsumptionFactor = 0.90d;

    /// <summary>Множитель расхода ресурсов при ПОВЫШЕННОМ метаболизме
    /// (от <see cref="ElevatedMetabolismPercent"/>). Больше 1 — тратит больше.</summary>
    public const double ElevatedMetabolismConsumptionFactor = 1.25d;

    /// <summary>Во сколько раз бафф «Бык» снижает расход ресурсов (×0,5).</summary>
    public const double BullMetabolismConsumptionFactor = 0.50d;

    // ═══════════════════════════════════════════════════════════════════════
    // 9. УСТАЛОСТЬ И СТРЕСС: множители условий
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Множитель набора усталости вечером (18:00–22:00). Больше —
    /// вечером устаёшь быстрее.</summary>
    public const double NightFatigueMultiplier = 1.5d;

    /// <summary>Множитель набора усталости НОЧЬЮ (22:00–06:00).</summary>
    public const double DeepNightFatigueMultiplier = 2.5d;

    /// <summary>Граница «дня» по игровому часу: с этого часа начинается вечер.</summary>
    public const double NightFatigueStartHour = 18d;

    /// <summary>Граница «вечера» по игровому часу: с этого часа начинается ночь.</summary>
    public const double DeepNightFatigueStartHour = 22d;

    /// <summary>Граница «ночи» по игровому часу: с этого часа начинается день.</summary>
    public const double DayFatigueStartHour = 6d;

    // ═══════════════════════════════════════════════════════════════════════
    // 10. ГИГИЕНА: пороги дебаффов
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Ниже этой гигиены выдаётся дебафф «Неопрятный»
    /// (стресс копится ×1,5, обаяние −1).</summary>
    public const double UnkemptHygienePercent = 50d;

    /// <summary>Ниже этой гигиены выдаётся дебафф «Бомж»
    /// (стресс ×2,5, обаяние −3, потолок устойчивости 50%).</summary>
    public const double BumHygienePercent = 20d;

    /// <summary>Множитель накопления стресса под дебаффом «Неопрятный».</summary>
    public const double UnkemptStressMultiplier = 1.5d;

    /// <summary>Множитель накопления стресса под дебаффом «Бомж».</summary>
    public const double BumStressMultiplier = 2.5d;

    /// <summary>Сколько игровых часов держатся проблемы с кожей, полученные
    /// при появлении дебаффа «Бомж».</summary>
    public const double SkinIssuesGameHours = 5d;

    /// <summary>Стресс, который сразу начисляется при появлении проблем с кожей
    /// (вместе с дебаффом «Бомж»), % шкалы.</summary>
    public const double SkinIssuesStressPercent = 90d;

    /// <summary>Здоровье, которое сразу отнимается при появлении проблем
    /// с кожей, % шкалы.</summary>
    public const double SkinIssuesHealthLossPercent = 25d;

    /// <summary>Сколько гигиены восстанавливает мыло, % шкалы.</summary>
    public const double SoapHygienePercent = 35d;

    /// <summary>До скольки игровых часов мазь сокращает проблемы с кожей.</summary>
    public const double SkinOintmentRemainingGameHours = 2d;

    /// <summary>Во сколько раз ПРОБЛЕМЫ С КОЖЕЙ уходят быстрее/медленнее
    /// при ПОНИЖЕННОМ метаболизме (0,75 — заживает медленнее).</summary>
    public const double SkinIssuesReducedMetabolismSpeedFactor = 0.75d;

    /// <summary>Во сколько раз проблемы с кожей уходят быстрее при ПОВЫШЕННОМ
    /// метаболизме (1,5 — заживает быстрее).</summary>
    public const double SkinIssuesElevatedMetabolismSpeedFactor = 1.5d;

    // ═══════════════════════════════════════════════════════════════════════
    // 11. ИСТОЩЕНИЕ: пороги и скорость накопления
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>При каком проценте усталости начинают копиться проблемы —
    /// порог «критической» зоны уже посчитан, здесь про долю ПЕРЕрасхода,
    /// которая становится истощением.</summary>
    public const double FatigueExhaustionShare = 1d;

    /// <summary>Доля перерасхода, превращающаяся в истощение, когда усталость
    /// ещё НЕ дошла до 100% (0,25 — четверть). При 100% берётся 1,0.</summary>
    public const double FatigueExhaustionBelowFullShare = 0.25d;

    /// <summary>Истощение ЭНЕРГИИ: множитель перерасхода (1,0 — как есть).</summary>
    public const double EnergyExhaustionMultiplier = 1d;

    /// <summary>Истощение ЖИДКОСТИ: базовый множитель (2,0 — жидкость изнашивает
    /// организм вдвое быстрее энергии). При критической нехватке умножается ещё
    /// на <see cref="CriticalHydrationExhaustionDoubleFactor"/>.</summary>
    public const double HydrationExhaustionMultiplier = 2d;

    /// <summary>Штраф здоровья от истощения за 15 игровых минут считается как
    /// суммарное истощение энергии и жидкости, делённое на ЭТО число
    /// (6 — «за 15 минут 1/6 от процента истощения»).</summary>
    public const double ExhaustionHealthLossDivisor = 6d;

    // ═══════════════════════════════════════════════════════════════════════
    // 12. ВОССТАНОВЛЕНИЕ ЗДОРОВЬЯ: множители стресса
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Во сколько раз ЛЮБОЙ стресс замедляет восстановление здоровья
    /// (0,75 — минус 25%).</summary>
    public const double HealthRegenAnyStressMultiplier = 0.75d;

    /// <summary>Дополнительный множитель восстановления здоровья при наличии
    /// КУМУЛЯТИВНОГО стресса (0,5 — минус ещё половина).</summary>
    public const double HealthRegenCumulativeStressMultiplier = 0.5d;

    // ═══════════════════════════════════════════════════════════════════════
    // 13. ЗАПРЕТ СНА И БАФФ «ПРИЛИВ СИЛ»
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Сон невозможен, если здоровье, энергия или жидкость НЕ ВЫШЕ
    /// этого процента (задано автором: «должны быть выше 5%»).</summary>
    public const double SleepBlockedBelowPercent = 5d;

    /// <summary>Длительность ПОЛНОЦЕННОГО сна (кнопка «Спать»), игровые часы.</summary>
    public const int FullSleepHours = 4;

    /// <summary>Длительность ПОЛЕВОГО сна (короткий отдых в дороге), игровые часы.</summary>
    public const int FieldSleepHours = 6;

    /// <summary>Длительность ночлега в гостинице, игровые часы.</summary>
    public const int HotelSleepHours = 7;

    /// <summary>Сколько обычного стресса снимает ПОЛНОЦЕННЫЙ сон за ночь,
    /// % шкалы (100 — очищает полностью).</summary>
    public const double FullSleepStressClearPercent = 100d;

    /// <summary>Сколько КУМУЛЯТИВНОЙ усталости снимает полноценный сон
    /// (0 — снимает всю: полноценный сон обнуляет кумулятивную усталость).</summary>
    public const double FullSleepCumulativeFatigueClearPercent = 0d;

    /// <summary>Сколько КУМУЛЯТИВНОЙ усталости снимает ПОЛЕВОЙ сон за ночь,
    /// % шкалы (6 — за ночь уходит 6%).</summary>
    public const double FieldSleepCumulativeFatigueClearPercent = 6d;

    /// <summary>Минимум, до которого полевой сон опускает кумулятивную усталость,
    /// % (ниже него не снимает — 10%).</summary>
    public const double FieldSleepCumulativeFatigueFloorPercent = 10d;

    /// <summary>«Прилив сил» включается, только если шкала заполнена почти
    /// полностью: не ниже этого процента (99,9 — «полная»).</summary>
    public const double PowerSurgeFullScalePercent = 99.9d;

    /// <summary>«Прилив сил» требует усталости НЕ ВЫШЕ этого процента.</summary>
    public const double PowerSurgeMaxFatiguePercent = 10d;

    /// <summary>«Прилив сил» требует стресса НЕ ВЫШЕ этого процента.</summary>
    public const double PowerSurgeMaxStressPercent = 20d;

    /// <summary>«Прилив сил» требует гигиены НЕ НИЖЕ этого процента.</summary>
    public const double PowerSurgeMinHygienePercent = 50d;

    /// <summary>«Прилив сил» требует устойчивости ВЫШЕ этого процента.</summary>
    public const double PowerSurgeMinResiliencePercent = 75d;

    /// <summary>«Прилив сил» требует метаболизма ВЫШЕ этого процента.</summary>
    public const double PowerSurgeMinMetabolismPercent = 75d;

    /// <summary>Сколько здоровья/энергии/жидкости забирает ВЫГОРАНИЕ в момент
    /// выдачи (по 5% с каждой шкалы).</summary>
    public const double BurnoutInstantPenaltyPercent = 5d;
    // ═══════════════════════════════════════════════════════════════════════
    // 14. КОФЕИН
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>За сколько игровых часов выводится ПОЛОВИНА кофеина
    /// (период полувыведения).</summary>
    public const double CaffeineHalfLifeGameHours = 5d;

    /// <summary>Максимальная доля «привыкания», которая гасит дозу (0,75 — до 75%).
    /// Считается от <c>CaffeineDependence</c> (0..100).</summary>
    public const double CaffeineMaxAdaptation = 0.75d;

    /// <summary>Какую долю дозы съедает максимальное привыкание (0,35 — 35%).</summary>
    public const double CaffeineAdaptationDosePenalty = 0.35d;

    /// <summary>Сколько единиц «форсажа» энергии даёт 1 мг кофеина
    /// (доза делится на это число).</summary>
    public const double CaffeineEnergyOverchargeDivisor = 12d;

    /// <summary>Минимальный форсаж энергии от кофеина, единицы.</summary>
    public const double CaffeineMinEnergyOvercharge = 1d;

    /// <summary>Базовое снижение усталости от кофеина, % шкалы.</summary>
    public const double CaffeineBaseFatigueReliefPercent = 1.5d;

    /// <summary>Добавка к снижению усталости на каждую «единицу» дозы
    /// (доза делится на это число).</summary>
    public const double CaffeineFatigueReliefDoseDivisor = 90d;

    /// <summary>Максимум снижения усталости от одной дозы кофеина, % шкалы.</summary>
    public const double CaffeineMaxFatigueReliefPercent = 5d;

    /// <summary>Сколько «привыкания» (0..100) добавляет доза: мг делим на это
    /// число и умножаем на <c>CaffeineDependenceGainFactor</c>.</summary>
    public const double CaffeineDependenceDivisor = 100d;

    /// <summary>Множитель в накоплении привыкания (см. выше).</summary>
    public const double CaffeineDependenceGainFactor = 0.8d;

    /// <summary>Сколько «привыкания» уходит за игровой час.</summary>
    public const double CaffeineDependenceDecayPerHour = 0.15d;

    /// <summary>Суточная доза, при превышении которой начисляется стресс. Первая
    /// ступень + вторая ступень с дебаффом «перенапряжение».</summary>
    public const double CaffeineOveruseDailyMg = 200d;

    /// <summary>Суточная доза, при превышении которой дебафф «перенапряжение».</summary>
    public const double CaffeineStressDailyMg = 400d;

    /// <summary>Суточная доза, при превышении которой дебафф «избыток».</summary>
    public const double CaffeineExcessDailyMg = 600d;

    /// <summary>Стресс за переход через первую ступень, % шкалы.</summary>
    public const double CaffeineOveruseStressPercent = 1d;

    /// <summary>Стресс за переход через вторую ступень.</summary>
    public const double CaffeineStressPercent = 2d;

    /// <summary>Стресс за переход через третью ступень.</summary>
    public const double CaffeineExcessStressPercent = 4d;

    /// <summary>Доза одной порции, с которой начинается «возбудимость».</summary>
    public const double CaffeineJitterDoseMg = 200d;

    /// <summary>Стресс от «возбудимости».</summary>
    public const double CaffeineJitterStressPercent = 1d;

    /// <summary>Длительность дебаффа «возбудимость» в РЕАЛЬНЫХ секундах.</summary>
    public const double CaffeineJitterDurationRealSeconds = 90d * 60d;

    /// <summary>Час, с которого кофеин считается «поздним» (и до утра).</summary>
    public const double LateCaffeineStartHour = 20d;

    /// <summary>Час, до которого кофеин считается «поздним».</summary>
    public const double LateCaffeineEndHour = 6d;

    /// <summary>Стресс от позднего кофеина.</summary>
    public const double LateCaffeineStressPercent = 0.5d;

    /// <summary>Длительность дебаффа «поздний кофеин», игровые секунды.</summary>
    public const double LateCaffeineDurationGameSeconds = 3d * 3600d;

    /// <summary>Длительность дебаффов «перенапряжение» и «избыток», игровые секунды.</summary>
    public const double CaffeineOveruseDurationGameSeconds = 2d * 3600d;

    /// <summary>Порог «привыкания», с которого возможен кофеиновый откат.</summary>
    public const double CaffeineWithdrawalDependencePercent = 20d;

    /// <summary>Уровень кофеина в организме ДО, при котором откат ещё возможен.</summary>
    public const double CaffeineWithdrawalLoadBeforeMg = 20d;

    /// <summary>Уровень кофеина, НИЖЕ которого наступает откат.</summary>
    public const double CaffeineWithdrawalLoadAfterMg = 8d;

    /// <summary>Длительность дебаффа «кофеиновый откат», игровые секунды.</summary>
    public const double CaffeineWithdrawalDurationGameSeconds = 2d * 3600d;

    /// <summary>Усталость, которую добавляет кофеиновый откат, если игрок БОДРСТВУЕТ.</summary>
    public const double CaffeineWithdrawalAwakeFatiguePercent = 3d;

    /// <summary>Усталость, которую добавляет кофеиновый откат, если игрок СПИТ.</summary>
    public const double CaffeineWithdrawalSleepFatiguePercent = 1d;

    /// <summary>Стресс от кофеинового отката наяву.</summary>
    public const double CaffeineWithdrawalAwakeStressPercent = 2d;

    /// <summary>Стресс от кофеинового отката во сне.</summary>
    public const double CaffeineWithdrawalSleepStressPercent = 1d;

    /// <summary>Сколько игровых часов в «кофеиновых сутках» (сброс суточной дозы).</summary>
    public const double CaffeineDayGameHours = 24d;

    // ═══════════════════════════════════════════════════════════════════════
    // 15. ИТОГОВЫЕ ЗАВИСИМОСТИ И ЗАВИСИМОСТИ ОТ ВРЕМЕНИ СУТОК
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Скорость набора усталости в процентах за игровой час (величина,
    /// на которую делится 100% при известном числе часов). ВЫВЕДЕНО — менять
    /// нужно <see cref="FatigueBuildHours"/>.</summary>
    public const double FatigueBuildPercentPerHour = 100d / FatigueBuildHours;

    /// <summary>Скорость снятия усталости в покое, % за игровой час. ВЫВЕДЕНО.</summary>
    public const double FatigueRestPercentPerHour = 100d / FatigueRestHours;

    /// <summary>Скорость набора усталости на километр пути, %/км. ВЫВЕДЕНО.</summary>
    public const double FatigueBuildPercentPerKilometer =
        100d / FatigueBuildKilometers;

    // ═══════════════════════════════════════════════════════════════════════
    // 16. ГОСТИНИЦА, БАФФЫ И ДЕБАФФЫ (длительности и эффекты)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Цена ночлега в гостинице (деньги).</summary>
    public const double HotelPrice = 5000d;

    /// <summary>Бафф «Отпуск»: множитель получаемого опыта.</summary>
    public const double RelaxationExperienceMultiplier = 1.15d;

    /// <summary>Бафф «Отпуск»: на сколько процентов замедляется стресс.</summary>
    public const double RelaxationStressSlowdownPercent = 15d;

    /// <summary>Бафф «Отпуск»: длительность в РЕАЛЬНЫХ секундах.</summary>
    public const double RelaxationDurationRealSeconds = 15d * 60d;

    /// <summary>Бафф «Отдохнувший»: множитель получаемого опыта.</summary>
    public const double RestedExperienceMultiplier = 1.25d;

    /// <summary>Бафф «Отдохнувший»: на сколько процентов замедляется стресс.</summary>
    public const double RestedStressSlowdownPercent = 25d;

    /// <summary>Бафф «Отдохнувший»: длительность в РЕАЛЬНЫХ секундах.</summary>
    public const double RestedDurationRealSeconds = 25d * 60d;

    /// <summary>Дебафф «Выгорание»: во сколько раз быстрее растёт усталость.</summary>
    public const double BurnoutFatigueBuildMultiplier = 1.10d;

    /// <summary>Дебафф «Выгорание»: длительность в реальных секундах.</summary>
    public const double BurnoutDurationRealSeconds = 15d * 60d;

    /// <summary>Бафф «Прилив сил»: длительность в реальных секундах.</summary>
    public const double PowerSurgeDurationRealSeconds = 15d * 60d;

    // ═══════════════════════════════════════════════════════════════════════
    // 17. ПРОИЗВОДНЫЕ ПЕРЕСЧЁТЫ (менять не нужно — считаются из чисел выше)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Порог «ЖАЖДЫ» в процентах шкалы. ВЫВЕДЕН из миллилитров и размера шкалы:
    /// 1500 из 3000 → 50%. Не задавай его вторым числом — разъедется.
    /// </summary>
    public const double ThirstThresholdPercent =
        ThirstThresholdMilliliters /
        HydrationScaleMilliliters *
        100d;

    /// <summary>Порог «ОБЕЗВОЖИВАНИЯ» в процентах шкалы (500 из 3000 → 16,67%).</summary>
    public const double DehydrationThresholdPercent =
        DehydrationThresholdMilliliters /
        HydrationScaleMilliliters *
        100d;
}
