namespace AssistQuestEditor.Domain;

/// <summary>
/// Раздел окна «Перки, баффы, скиллы».
///
/// Четыре раздела — не украшение, а разные ЖИЗНЕННЫЕ ЦИКЛЫ: перк постоянен,
/// умение растёт очками, бафф и дебафф истекают по времени. Свалить их в один
/// список значило бы дать игроку один набор действий над пунктами с разной
/// природой, и вопрос «почему у баффа нет кнопки убрать» остался бы без ответа.
/// </summary>
public enum CharacterPerkCategory
{
    Perk,
    Skill,
    Buff,
    Debuff
}

/// <summary>
/// Пункт окна «Перки, баффы, скиллы».
///
/// <paramref name="Description"/> отвечает на вопрос «как это работает»,
/// <paramref name="Affects"/> — «на какие показатели влияет». Автор просил
/// описание у КАЖДОГО пункта, и это ровно два разных вопроса: первый про условие
/// получения и срок, второй про числа, которые игрок затем ищет в мониторе.
///
/// <paramref name="Implemented"/> — подключён ли пункт к симулятору. Выключенный
/// показывается с пометкой «(В разработке)»: автор просил честно отделять
/// задуманное от работающего, иначе игрок ждал бы действия от пункта, который
/// движок ещё не знает. Это знание О ДОМЕНЕ, а не о внешнем виде, поэтому флаг
/// живёт здесь, рядом с самими правилами.
///
/// <paramref name="MaxLevel"/> больше нуля только у умений: у них уровень
/// набирается очками, и нулевой уровень означает «умения нет», поэтому в окне
/// такой пункт не показывается.
/// </summary>
public sealed record CharacterPerkDefinition(
    string Id,
    CharacterPerkCategory Category,
    string Name,
    string Description,
    string Affects,
    bool Implemented = true,
    double DurationRealSeconds = 0d,
    double DurationGameSeconds = 0d,
    int MaxLevel = 0)
{
    /// <summary>Умение с очками, а не разовое состояние.</summary>
    public bool IsLevelled => MaxLevel > 0;

    /// <summary>
    /// Длительность словами для подсказки. Пустая строка — у пункта нет срока
    /// (перк постоянен, бафф держится по условию).
    /// </summary>
    public string DurationLabel =>
        DurationRealSeconds > 0d
            ? FormatMinutes(DurationRealSeconds) + " реального времени"
            : DurationGameSeconds > 0d
                ? FormatHours(DurationGameSeconds) + " игрового времени"
                : "";

    private static string FormatMinutes(double seconds)
    {
        var minutes = seconds / 60d;

        return minutes >= 1d
            ? Math.Round(minutes) + " мин"
            : Math.Round(seconds) + " с";
    }

    private static string FormatHours(double seconds)
    {
        var hours = seconds / 3600d;

        return hours >= 1d
            ? Math.Round(hours, hours == Math.Floor(hours) ? 0 : 1) + " ч"
            : FormatMinutes(seconds);
    }
}

/// <summary>
/// Каталог перков, умений, баффов и дебаффов.
///
/// Зачем каталог, а не список в странице. Пункты, ДЕЙСТВУЮЩИЕ сейчас, известны
/// движку (<see cref="PlayerConditionState.Effects"/>), а вот те, которых у
/// игрока нет, не знает никто — их надо перечислить, чтобы окно вообще было о
/// чём. Перечень задуманного — это знание о мире, и держать его в JavaScript
/// значило бы завести второй источник правды о том, какие эффекты существуют:
/// добавили эффект в движок — забыли в окне, и игрок его не увидит.
///
/// Здесь же — описания. Они объясняют механику игроку и потому могли бы жить в
/// интерфейсе, но КЛЮЧИ (Id) обязаны совпадать с теми, что создаёт движок:
/// рассогласование ключа означало бы, что окно не подсветит действующий бафф.
/// Держать их рядом — единственный способ заметить рассогласование на тесте.
/// </summary>
public static class CharacterPerksCatalog
{
    /// <summary>Пометка для пунктов, ещё не подключённых к симулятору.</summary>
    public const string InProgressSuffix = "(В разработке)";

    /// <summary>Пометка для пунктов, действующих у игрока прямо сейчас.</summary>
    public const string ActiveSuffix = "(активен)";

    private static readonly CharacterPerkDefinition[] Catalog =
    [
        // ── Перки: постоянные последствия действий игрока ──────────────────
        new(
            "perk.dairy_habit",
            CharacterPerkCategory.Perk,
            "Молочная привычка",
            "Даётся за регулярное употребление молочных продуктов: организм привыкает к кальцию.",
            "Открывает бафф «Крепкие кости» — полное исключение риска переломов.",
            Implemented: false),
        new(
            "perk.smoker",
            CharacterPerkCategory.Perk,
            "Прокуренный",
            "Даётся за длительное курение: без сигареты появляется никотиновый откат.",
            "Скорость накопления стресса выше, устойчивость растёт медленнее.",
            Implemented: false),
        new(
            "perk.discharged",
            CharacterPerkCategory.Perk,
            "Уволен",
            "Даётся за провал рабочего поручения: работодатель закрывает доступ к заказам.",
            "Репутация у работодателей, доступные квесты, обаяние.",
            Implemented: false),
        new(
            "perk.breadwinner",
            CharacterPerkCategory.Perk,
            "Кормилец",
            "Даётся за регулярные поставки еды на базу: тебя считают надёжным снабженцем.",
            "Репутация у жителей, бонус к получаемому опыту.",
            Implemented: false),

        // ── Умения: уровень набирается очками ──────────────────────────────
        new(
            "skill.mechanics",
            CharacterPerkCategory.Skill,
            "Механик",
            "Практика полевого ремонта: очки вкладываются в разборку и сборку узлов.",
            "Скорость ремонта транспорта, шанс успеха при полевом восстановлении.",
            MaxLevel: 5),
        new(
            "skill.navigation",
            CharacterPerkCategory.Skill,
            "Штурман",
            "Работа с картой и компасом: очки вкладываются в прокладку маршрута.",
            "Точность маршрута, расход топлива на участке, время в пути.",
            MaxLevel: 5),
        new(
            "skill.medicine",
            CharacterPerkCategory.Skill,
            "Фельдшер",
            "Первая помощь: очки вкладываются в обработку ран и работу с препаратами.",
            "Эффективность лекарств, скорость восстановления здоровья.",
            MaxLevel: 5),

        // ── Баффы: временная выгода ────────────────────────────────────────
        new(
            "rested",
            CharacterPerkCategory.Buff,
            "Отдохнувший",
            "Выдаётся, когда усталость доходит до нуля. Заменяет «Расслабление», а не складывается с ним.",
            "Опыт +25%, накопление стресса замедлено на своё значение процента.",
            DurationRealSeconds: CharacterVitalsTuning.RestedDurationRealSeconds),
        new(
            "relaxation",
            CharacterPerkCategory.Buff,
            "Расслабление",
            "Выдаётся коротким отдыхом вне сна. Не складывается с «Отдохнувшим».",
            "Опыт +15%, накопление стресса замедлено на своё значение процента.",
            DurationRealSeconds: CharacterVitalsTuning.RelaxationDurationRealSeconds),
        new(
            "bull",
            CharacterPerkCategory.Buff,
            "Бык",
            "Даётся за дисциплину в спорте и питании. Пока действует, негатив не накапливается вовсе.",
            "Любое негативное накопление (истощение и кумулятивный стресс) прекращается; " +
            "метаболизм расходует ресурсы как при сниженном; восстановление здоровья +2% за 10 минут; " +
            "получаемый форсаж ×1,5.",
            DurationGameSeconds: CharacterItemTuning.BullFromSupplementGameSeconds),
        new(
            "strong_bones",
            CharacterPerkCategory.Buff,
            "Крепкие кости",
            "Даётся за регулярное употребление молочных продуктов.",
            "Полностью исключает риск переломов.",
            DurationGameSeconds: CharacterItemTuning.StrongBonesGameSeconds),
        new(
            "power_surge",
            CharacterPerkCategory.Buff,
            "Прилив сил",
            "Кратковременный подъём: шкалы замирают, ничто не тратится.",
            "Блокирует ЛЮБОЕ изменение шкал — значения замораживаются.",
            DurationRealSeconds: CharacterVitalsTuning.PowerSurgeDurationRealSeconds),
        new(
            "sorbent",
            CharacterPerkCategory.Buff,
            "Сорбент",
            "Даётся приёмом сорбента.",
            "Связывает токсины, ускоряя очищение организма (вывод кофеина и алкоголя).",
            DurationGameSeconds: CharacterItemTuning.SorbentGameSeconds),
        new(
            "analgesia",
            CharacterPerkCategory.Buff,
            "Обезболивание",
            "Даётся приёмом обезболивающего.",
            "Снимает боль, поддерживает здоровье.",
            DurationGameSeconds: CharacterItemTuning.AnalgesiaGameSeconds),

        // ── Дебаффы: временная расплата ────────────────────────────────────
        new(
            "burnout",
            CharacterPerkCategory.Debuff,
            "Выгорание",
            "Выдаётся разово при входе стресса в критическую зону (выше 50%).",
            "Усталость копится на 10% быстрее. В момент выдачи разом снимает 5% здоровья, " +
            "энергии и жидкости.",
            DurationRealSeconds: CharacterVitalsTuning.BurnoutDurationRealSeconds),
        new(
            CharacterVitalsEngine.ThirstEffectId,
            CharacterPerkCategory.Debuff,
            "Жажда",
            "Начинается, когда жидкости в шкале остаётся меньше 1500 мл. Снимается, как " +
            "только в пищеварении начинает усваиваться что-то с жидкостью.",
            "Копится истощение жидкости (снижает верхний предел шкалы), стресс растёт на " +
            "0,5% в минуту, метаболизм падает на 1% за 10 игровых минут.",
            DurationGameSeconds: 0d),
        new(
            CharacterVitalsEngine.DehydrationEffectId,
            CharacterPerkCategory.Debuff,
            "Обезвоживание",
            "Начинается, когда жидкости остаётся меньше 500 мл. Снимается, когда игрок " +
            "добивается положительной (зелёной) динамики жидкости.",
            "Метаболизм падает вдвое быстрее (2% за 10 игровых минут), стресс копится " +
            "на 25% быстрее, устойчивость падает вместе с расходом жидкости, действия " +
            "с физической нагрузкой недоступны (кроме движения).",
            DurationGameSeconds: 0d),
        new(
            "unkempt",
            CharacterPerkCategory.Debuff,
            "Неопрятный",
            "Начинается, когда гигиена падает ниже 50%. Снимается полноценным сном или в отпуске.",
            "Стресс копится в 1,5 раза быстрее, обаяние снижено на 1.",
            DurationGameSeconds: 0d),
        new(
            "bum",
            CharacterPerkCategory.Debuff,
            "Бомж",
            "Начинается, когда гигиена падает ниже 20%. Снимается полноценным сном или в отпуске.",
            "Стресс копится в 2,5 раза быстрее, обаяние снижено на 3, " +
            "устойчивость не поднимается выше 50%.",
            DurationGameSeconds: 0d),
        new(
            "analgesic_overuse",
            CharacterPerkCategory.Debuff,
            "Злоупотребление обезболивающими",
            "Начинается после 4-го приёма обезболивающего.",
            "Препарат перестаёт помогать и добавляет стресс.",
            DurationGameSeconds: CharacterItemTuning.AnalgesicOveruseGameSeconds),
        new(
            "drowsiness",
            CharacterPerkCategory.Debuff,
            "Сонливость",
            "Побочный эффект успокоительных и адаптогенов.",
            "Усталость копится быстрее, устойчивость снижена.",
            DurationGameSeconds: CharacterItemTuning.DrowsinessGameSeconds),
        new(
            "nicotine_rebound",
            CharacterPerkCategory.Debuff,
            "Никотиновый откат",
            "Расплата за выкуренную сигарету: облегчение краткое, откат дольше.",
            "Стресс резко возвращается после краткого облегчения.",
            DurationRealSeconds: CharacterItemTuning.NicotineReboundRealSeconds),
        new(
            "alcohol_aftereffect",
            CharacterPerkCategory.Debuff,
            "Последействие алкоголя",
            "Наступает после выпивки.",
            "Обезвоживание и разбитость: жидкость падает, устойчивость снижена.",
            DurationGameSeconds: CharacterItemTuning.BeerAftereffectGameSeconds),
        new(
            "caffeine_overuse",
            CharacterPerkCategory.Debuff,
            "Кофеин — перенапряжение",
            "Слишком много кофеина за сутки.",
            "Сердце и нервы на пределе: стресс, риск срыва сна.",
            DurationGameSeconds: 0d),
        new(
            "caffeine_excess",
            CharacterPerkCategory.Debuff,
            "Кофеин — избыток",
            "Превышена комфортная доза кофеина.",
            "Растёт накопление стресса, ухудшается качество сна.",
            DurationGameSeconds: 0d),
        new(
            "caffeine_jitter",
            CharacterPerkCategory.Debuff,
            "Кофеиновая возбудимость",
            "Передозировка кофеина.",
            "Тремор и тревожность: стресс растёт сам по себе.",
            DurationGameSeconds: 0d),
        new(
            "late_caffeine",
            CharacterPerkCategory.Debuff,
            "Поздний кофеин",
            "Кофеин выпит слишком поздно.",
            "Сон будет хуже: восстановление во сне слабее.",
            DurationGameSeconds: 0d),
        new(
            "caffeine_withdrawal",
            CharacterPerkCategory.Debuff,
            "Кофеиновый откат",
            "Организм требует привычную дозу кофеина.",
            "Стресс растёт, устойчивость падает, пока доза не восстановлена.",
            DurationGameSeconds: 0d)
    ];

    /// <summary>Все пункты каталога.</summary>
    public static IReadOnlyList<CharacterPerkDefinition> Entries => Catalog;

    public static CharacterPerkDefinition? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return Catalog.FirstOrDefault(
            entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<CharacterPerkDefinition> ByCategory(
        CharacterPerkCategory category) =>
        Catalog.Where(entry => entry.Category == category).ToArray();

    /// <summary>
    /// Название пункта в окне.
    ///
    /// Автор задал точные пометки — «(активен)» у действующего пункта и
    /// «(В разработке)» у неподключённого. Обе строятся ЗДЕСЬ, а не в разметке
    /// страницы: это правило показа, и проверяться оно должно тестом, а не
    /// глазами в вёрстке.
    ///
    /// Пометки складываются: пункт может быть и активным, и незаконченным —
    /// молчание об одном из двух скрыло бы от игрока вторую половину правды.
    /// </summary>
    public static string DisplayName(CharacterPerkDefinition entry, bool active)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var name = entry.Name;

        if (!entry.Implemented)
            name += " " + InProgressSuffix;

        if (active)
            name += " " + ActiveSuffix;

        return name;
    }
}
