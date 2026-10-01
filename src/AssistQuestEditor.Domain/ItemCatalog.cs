namespace AssistQuestEditor.Domain;

/// <summary>
/// Стартовый каталог предметов Симулятора и редактора: квестовые предметы
/// (мясо, колбаса, записка) и расходники механик (вода, паёк, кофе, медицина,
/// гигиена). Предметы Character Vitals приходят из
/// <see cref="CharacterConsumableCatalog"/>, где их описания лежат рядом с
/// дозировками кофеина: оба списка обязаны совпадать по Id.
/// </summary>
public static class ItemCatalogFactory
{
    public static IReadOnlyList<ItemDefinition> CreateStarter() => Items;

    /// <summary>
    /// Категории, которые НИКОГДА не продаются: их выдают квесты и события, и
    /// цена у них означала бы покупку, которой в мире нет.
    ///
    /// Признак задан КАТЕГОРИЕЙ, а не списком Id: автор сформулировал правило как
    /// «кроме квестовых, которые никогда не продаются», и новая квестовая вещь
    /// должна попадать под него сама, без правки второй таблицы.
    /// </summary>
    public static bool IsQuestOnly(string? category) =>
        string.Equals(
            category,
            QuestItemCategory,
            StringComparison.OrdinalIgnoreCase);

    public const string QuestItemCategory = "Квестовый предмет";

    /// <summary>Определение по Id. null — такого предмета в каталоге нет.</summary>
    public static ItemDefinition? Find(string? itemId) =>
        string.IsNullOrWhiteSpace(itemId)
            ? null
            : Items.FirstOrDefault(item =>
                item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Цена предмета в рублях с учётом правила «квестовое не продаётся».
    ///
    /// Проверка стоит ЗДЕСЬ, а не в таблице цен: одна забытая строка в таблице
    /// вернула бы цену квестовой вещи, и игрок увидел бы у неё ценник. Отказ
    /// должен зависеть от КАТЕГОРИИ, то есть от того, что автор задал словами.
    /// </summary>
    public static int PriceRubles(ItemDefinition? item)
    {
        if (item is null || IsQuestOnly(item.Category))
            return 0;

        return ItemData.PriceRubles(item.Id);
    }

    public static IReadOnlyList<ItemDefinition> Items =>
    [
        new ItemDefinition(
            "ruslan.raw_meat",
            "Мясо",
            "Особое свежее мясо, которое Руслан попросил принести для приготовления блюда.",
            "Квестовый предмет",
            "#b85c4a"),
        new ItemDefinition(
            "gosha.homemade_sausage",
            "Домашняя колбаса",
            "Колбаса домашнего приготовления от Гоши. Еда: восстанавливает немного энергии.",
            "Еда",
            "#c8874a"),
        new ItemDefinition(
            "ruslan.legendary_shashlik",
            "Легендарный шашлык Руслана",
            "Особый шашлык из спецмаринада. Единственный способ получить форсаж без препаратов: +25% к шкалам энергии и стресса сверх нормы.",
            QuestItemCategory,
            "#d9762f"),
        new ItemDefinition(
            "note",
            "Записка",
            "Короткая записка, найденная в динамическом тайнике.",
            "Квестовый предмет",
            "#e7d58a"),

        new ItemDefinition(
            "water.bottle",
            "Вода",
            "Бутылка воды. Восполняет жидкость.",
            "Напиток",
            "#4b8fe8"),
        new ItemDefinition(
            "food.meal",
            "Паёк",
            "Нормальная еда: энергия, немного жидкости и уменьшение усталости.",
            "Еда",
            "#b98a55"),
        new ItemDefinition(
            "food.milk",
            "Молоко",
            "Пища: энергия, жидкость и небольшое снижение стресса. После нескольких употреблений появляется «Крепкие кости».",
            "Еда",
            "#e8e0ca"),
        new ItemDefinition(
            "drink.lemon_tea",
            "Чай с лимоном",
            "Тёплый напиток: жидкость, снижение стресса, небольшой плюс к метаболизму и устойчивости.",
            "Напиток",
            "#d8b64c"),
        new ItemDefinition(
            "tea.herbal",
            "Травяной чай",
            "Успокаивает и немного повышает устойчивость.",
            "Напиток",
            "#6f9b5d"),
        new ItemDefinition(
            "drink.coffee",
            "Кофе",
            "Даёт краткий энергетический форсаж и уменьшает усталость. Злоупотребление повышает стресс.",
            "Напиток",
            "#76533d"),
        new ItemDefinition(
            "drink.energy",
            "Энергетик",
            "Сильный краткий подъём энергии с форсажем. Повторное злоупотребление даёт стресс и дебафф.",
            "Напиток",
            "#8b6bd8"),
        new ItemDefinition(
            "vitamin.c",
            "Витамин C",
            "Повышает устойчивость и немного метаболизм. После регулярного применения даёт «Бык».",
            "Медицина",
            "#f0c94b"),
        new ItemDefinition(
            "adaptogen.tincture",
            "Адаптоген",
            "Снимает стресс и временно повышает устойчивость и метаболизм.",
            "Медицина",
            "#8aa06b"),
        new ItemDefinition(
            "painkiller",
            "Обезболивающее",
            "Небольшое восстановление здоровья и стресса. Злоупотребление даёт дебафф.",
            "Медицина",
            "#c9c9cf"),
        new ItemDefinition(
            "soap",
            "Мыло",
            "Повышает скрытую гигиену.",
            "Гигиена",
            "#88b9d8"),
        new ItemDefinition(
            "skin.ointment",
            "Мазь",
            "Ускоряет очистку эффекта «Проблемы с кожей».",
            "Медицина",
            "#b48c77"),

        ..CharacterConsumableCatalog.Items
    ];
}
