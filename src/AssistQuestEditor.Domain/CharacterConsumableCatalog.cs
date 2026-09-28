namespace AssistQuestEditor.Domain;

/// <summary>
/// Расширение каталога предметов для Character Vitals.
/// Дозы кофеина — игровые справочные значения, а не медицинская инструкция:
/// они основаны на типичных величинах для соответствующих напитков/продуктов.
/// </summary>
public static class CharacterConsumableCatalog
{
    private static readonly IReadOnlyDictionary<string, double> CaffeineMgByItem =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["drink.coffee"] = 95d,
            ["drink.espresso"] = 80d,
            ["drink.black_tea"] = 50d,
            ["drink.green_tea"] = 35d,
            ["drink.cola"] = 40d,
            ["drink.energy"] = 80d,
            ["food.dark_chocolate"] = 25d,
            ["drink.decaf_coffee"] = 5d
        };

    public static double GetCaffeineMg(string itemId) =>
        CaffeineMgByItem.TryGetValue(
            itemId ?? string.Empty,
            out var value)
                ? value
                : 0d;

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
