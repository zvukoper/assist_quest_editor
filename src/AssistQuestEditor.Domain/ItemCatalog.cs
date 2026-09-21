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
            "#b85c4a")
    ];
}