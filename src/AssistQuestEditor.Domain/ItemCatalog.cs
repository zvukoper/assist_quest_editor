namespace AssistQuestEditor.Domain;

public static class ItemCatalogFactory
{
    public static IReadOnlyList<ItemDefinition> CreateStarter() =>
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
            "Колбаса домашнего приготовления от Гоши. Продаётся только тем, кому Гоша доверяет.",
            "Еда",
            "#c8874a"),
        new ItemDefinition(
            "note",
            "Записка",
            "Короткая записка, найденная в динамическом тайнике.",
            "Квестовый предмет",
            "#e7d58a")
    ];
}