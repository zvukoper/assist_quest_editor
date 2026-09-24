using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Правила мира, кампании и подписей ресурсов.
///
/// Вынесены в домен именно потому, что их читают несколько мест (окно кампаний,
/// селектор [МИР][КАМПАНИЯ], диалог импорта). Разъехавшиеся правила дают не
/// ошибку, а разные ответы на один вопрос — «как называется мир» / «кто его
/// изменил», — и заметить это можно только сравнением.
/// </summary>
public sealed class WorldModelsTests
{
    private static WorldDefinition World(
        string name = "SibirMap",
        string? fullName = null,
        string? lastCampaignId = null,
        ResourceMetadata? metadata = null) =>
        new("sibirmap", name, fullName, null, null, 1, metadata, lastCampaignId);

    // --- Идентификатор общей кампании ---

    [Fact]
    public void CommonCampaignIdIsStableAcrossWorlds()
    {
        // Общая кампания — системная сущность: её адрес обязан быть
        // предсказуемым, иначе ссылки из импорта и поведение по умолчанию
        // зависели бы от мира.
        Assert.Equal("common", WorldDefinitionRules.CommonCampaignId("sibirmap"));
        Assert.Equal("common", WorldDefinitionRules.CommonCampaignId("another-world"));
        Assert.Equal(
            WorldDefinitionRules.CommonCampaignIdValue,
            WorldDefinitionRules.CommonCampaignId("sibirmap"));
    }

    // --- Подпись мира ---

    [Fact]
    public void FullNameWinsOverShortName()
    {
        Assert.Equal("Сибирь — карта", WorldDisplayRules.DisplayName(World(fullName: "Сибирь — карта")));
    }

    [Fact]
    public void ShortNameUsedWhenFullNameIsBlank()
    {
        // Пустая или пробельная строка — это отсутствие значения, а не значение
        // из пробелов: иначе в списке появлялась бы «пустая» строка.
        Assert.Equal("SibirMap", WorldDisplayRules.DisplayName(World(fullName: "   ")));
        Assert.Equal("SibirMap", WorldDisplayRules.DisplayName(World(fullName: "")));
        Assert.Equal("SibirMap", WorldDisplayRules.DisplayName(World(fullName: null)));
    }

    [Fact]
    public void UnnamedWorldGetsExplicitLabel()
    {
        // Пустая подпись читается как «элемент не загрузился». Явное
        // «(без имени)» говорит правду: имя не задано.
        Assert.Equal(WorldDisplayRules.UnnamedLabel, WorldDisplayRules.DisplayName(World(name: " ")));
        Assert.Equal(WorldDisplayRules.UnnamedLabel, WorldDisplayRules.DisplayName((WorldDefinition?)null));
    }

    [Fact]
    public void CampaignDisplayFollowsTheSameRuleAsWorld()
    {
        var campaign = new CampaignWorldDefinition("c1", "sibirmap", "Common", "Общая кампания", null, null, 1, null);

        Assert.Equal("Общая кампания", WorldDisplayRules.DisplayName(campaign));
        Assert.Equal(
            WorldDisplayRules.UnnamedLabel,
            WorldDisplayRules.DisplayName((CampaignWorldDefinition?)null));
    }

    // --- Родительские связи ---

    [Fact]
    public void CampaignBelongsToWorldIgnoresEmptyParent()
    {
        // Пустой ParentWorldId — это «связь ещё не проставлена» (файлы, созданные
        // до появления поля), и трактовать это как «чужой мир» значило бы
        // показывать предупреждение о несовпадении родителя на ровном месте.
        Assert.True(new CampaignWorldDefinition("c1", "w1", "C", null, null, null, 1, null).BelongsTo("w1"));
        Assert.True(new CampaignWorldDefinition("c1", "", "C", null, null, null, 1, null).BelongsTo("w1"));
        Assert.False(new CampaignWorldDefinition("c1", "w2", "C", null, null, null, 1, null).BelongsTo("w1"));
    }

    // --- Подписи created_by / modified_by ---

    [Fact]
    public void CreatedBySignatureCarriesAuthorAndDate()
    {
        var moment = new DateTimeOffset(2026, 9, 24, 11, 37, 5, TimeSpan.Zero);
        var metadata = new ResourceMetadata().WithCreated("Ruslan", moment);

        var text = WorldDisplayRules.Describe(metadata, modified: false);

        Assert.NotNull(text);
        Assert.Contains("Created by:", text);
        Assert.Contains("Ruslan", text);
        // Дата обязана быть в подписи: без неё нельзя сравнить две версии
        // ресурса при импорте — а именно для этого подпись и нужна.
        Assert.Contains("24.09.2026", text);
    }

    [Fact]
    public void ModifiedSignaturePrefersModifiedOverCreated()
    {
        var created = new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero);
        var modified = new DateTimeOffset(2026, 9, 25, 18, 30, 0, TimeSpan.Zero);
        var metadata = new ResourceMetadata()
            .WithCreated("Ruslan", created)
            .WithModified("Gosha", modified);

        var text = WorldDisplayRules.Describe(metadata);

        Assert.NotNull(text);
        Assert.Contains("Modified by:", text);
        Assert.Contains("Gosha", text);
        Assert.Contains("25.09.2026", text);
        // Подпись изменения не должна показывать автора создания: иначе список
        // сообщал бы, что Гоша создал ресурс.
        Assert.DoesNotContain("Ruslan", text);
    }

    [Fact]
    public void ModifiedFallsBackToCreatedMomentButKeepsModifiedLabel()
    {
        // Правок ещё не было: момент изменения берётся от создания (Effective),
        // но подпись остаётся «Modified by» — иначе список выглядел бы так,
        // будто ресурс вообще не менялся.
        var created = new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero);
        var metadata = new ResourceMetadata().WithCreated("Ruslan", created);

        var text = WorldDisplayRules.Describe(metadata);

        Assert.NotNull(text);
        Assert.Contains("Modified by:", text);
        Assert.Contains("24.09.2026", text);
    }

    [Fact]
    public void NoSignatureAtAllReturnsNullInsteadOfEmptyLine()
    {
        // Пустая строка списка читается как «данных нет», хотя правда в том, что
        // подписи нет (файлы, созданные до её появления). Форма обязана уметь
        // отличить эти случаи, поэтому «нечем подписать» = null.
        Assert.Null(WorldDisplayRules.Describe(null));
        Assert.Null(WorldDisplayRules.Describe(new ResourceMetadata()));
    }

    [Fact]
    public void AuthorWithoutDateStillProducesSignature()
    {
        // Дата может отсутствовать в старом файле, но автор — уже полезная
        // информация, и терять её из-за отсутствия даты нельзя. Проверяются оба
        // случая: подпись создания и подпись изменения читают РАЗНЫЕ поля, и
        // «автор есть, даты нет» должно работать в каждом.
        var created = WorldDisplayRules.Describe(new ResourceMetadata("Ruslan", null, null, null), modified: false);
        Assert.NotNull(created);
        Assert.Contains("Created by:", created);
        Assert.Contains("Ruslan", created);

        var modified = WorldDisplayRules.Describe(new ResourceMetadata(null, null, "Gosha", null));
        Assert.NotNull(modified);
        Assert.Contains("Modified by:", modified);
        Assert.Contains("Gosha", modified);
    }

    // --- Допустимость псевдонима автора ---

    [Theory]
    [InlineData("Ruslan")]
    [InlineData("Руслан")]
    [InlineData("User_2609241137")]
    [InlineData("Иван Петров")]
    [InlineData("_")]
    public void ValidAuthorNamesAccepted(string value)
    {
        Assert.True(ResourceMetadata.IsValidAuthor(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ruslan!")]
    [InlineData("Ruslan#1")]
    public void InvalidAuthorNamesRejected(string? value)
    {
        // Приложение не должно пускать к работе без псевдонима: подпись
        // ресурсов — не украшение, а то, по чему автор находит свои правки.
        Assert.False(ResourceMetadata.IsValidAuthor(value));
    }

    // --- Селектор [МИР][КАМПАНИЯ] ---

    [Fact]
    public void RememberedCampaignWins()
    {
        // Запомненная кампания важнее порядка: автор возвращается туда, где
        // работал, а не в «первую по алфавиту».
        Assert.Equal("story", ResourceSelectorRules.ResolveCampaignId(
            "story", new[] { "common", "story", "sandbox" }));
    }

    [Fact]
    public void RememberedCampaignIsMatchedCaseInsensitively()
    {
        // Id ресурсов в проекте регистронезависимы везде — требовать точного
        // регистра только здесь значило бы ломать уже работающий контент.
        Assert.Equal("story", ResourceSelectorRules.ResolveCampaignId(
            "STORY", new[] { "common", "story" }));
    }

    [Fact]
    public void MissingRememberedCampaignFallsBackToCommon()
    {
        // Кампанию могли удалить или переименовать. «Common» — системная
        // сущность каждого мира, поэтому именно она надёжная точка возврата.
        Assert.Equal("common", ResourceSelectorRules.ResolveCampaignId(
            "removed", new[] { "story", "common", "sandbox" }));
    }

    [Fact]
    public void WithoutCommonTheFirstCampaignIsUsed()
    {
        var available = new[] { "story", "sandbox" };
        Assert.Equal("story", ResourceSelectorRules.ResolveCampaignId(null, available));
        Assert.Equal("story", ResourceSelectorRules.ResolveCampaignId("  ", available));
        Assert.Equal("story", ResourceSelectorRules.ResolveCampaignId("removed", available));
    }

    [Fact]
    public void NoCampaignsMeansNoSelection()
    {
        // Пустой список — это «кампаний нет», а не «выбери любую»: выдуманный id
        // ушёл бы в Host и выглядел бы как отсутствующая кампания.
        Assert.Null(ResourceSelectorRules.ResolveCampaignId("story", Array.Empty<string>()));
        Assert.Null(ResourceSelectorRules.ResolveCampaignId(null, Array.Empty<string>()));
    }

    [Fact]
    public void CreateNewItemExistsInTheLists()
    {
        // «Создать…» идёт последним пунктом каждого списка, а не отдельной
        // кнопкой: создание — это тоже выбор, только нового элемента.
        Assert.False(string.IsNullOrWhiteSpace(ResourceSelectorRules.CreateNewLabel));
        Assert.Contains("Создать", ResourceSelectorRules.CreateNewLabel);
    }

    [Fact]
    public void ForeignParentIsReportedButNotBlocked()
    {
        // Перенос папки кампании в чужой мир — НЕ запрет: объект мог быть
        // перемещён осознанно. Поэтому расхождение сообщается текстом, а не
        // отказом работать.
        Assert.True(ResourceParentRules.IsForeignParent("sibirmap", "demo"));

        var warning = ResourceParentRules.Describe("sibirmap", "demo");
        Assert.NotNull(warning);
        // Названы ОБА идентификатора: «кампания из другого мира» не подсказывает,
        // куда именно её положить, и нужную папку искали бы наугад.
        Assert.Contains("sibirmap", warning);
        Assert.Contains("demo", warning);
    }

    [Fact]
    public void OwnParentProducesNoWarning()
    {
        // Ни одного предупреждения на нормальном случае: оранжевая плашка на
        // каждой кампании перестала бы что-либо значить.
        Assert.False(ResourceParentRules.IsForeignParent("demo", "demo"));
        Assert.Null(ResourceParentRules.Describe("demo", "demo"));

        // Регистр не важен: id ресурсов регистронезависимы везде в проекте.
        Assert.False(ResourceParentRules.IsForeignParent("Demo", "DEMO"));
    }

    [Fact]
    public void MissingParentIdIsNotAForeignParent()
    {
        // Пусто у кампаний, созданных до появления поля. Объявлять их «чужими»
        // значило бы разом пометить весь существующий контент.
        Assert.False(ResourceParentRules.IsForeignParent(null, "demo"));
        Assert.False(ResourceParentRules.IsForeignParent("", "demo"));
        Assert.False(ResourceParentRules.IsForeignParent("   ", "demo"));
        Assert.Null(ResourceParentRules.Describe(null, "demo"));

        // И наоборот: мир без id — это «мир неизвестен», а не расхождение.
        Assert.False(ResourceParentRules.IsForeignParent("sibirmap", null));
    }
}
