using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Правила переноса подготовленной папки на место.
///
/// Проверяются домен-тестами, потому что речь о ПУТЯХ и порядке файлов, а не о
/// диске. Дефект, ради которого правило появилось, на машине разработчика
/// воспроизвести нельзя: он зависел от того, лежат ли временная папка и каталог
/// миров на разных томах. Именно поэтому проверка «сравнить корни» должна быть
/// тестируемой отдельно от файловой системы.
/// </summary>
public sealed class StagedFolderRulesTests
{
    // --- Переименование возможно только внутри одного тома ---

    [Fact]
    public void SameVolumeCanRename()
    {
        var result = StagedFolderRules.CanRenameIntoPlace(
            @"C:\Users\author\AppData\Local\Temp\aq-import-1",
            @"C:\Users\author\Documents\Assist Quest Editor\worlds\Демо Мир");

        Assert.True(result, "Внутри одного тома перенос обязан делаться переименованием.");
    }

    [Fact]
    public void DifferentVolumesCannotRename()
    {
        // Именно этот случай давал «Move will not work across volumes»: временный
        // каталог на системном диске, пользовательские данные — в Документах на
        // другом диске.
        var result = StagedFolderRules.CanRenameIntoPlace(
            @"C:\Users\author\AppData\Local\Temp\aq-import-1",
            @"E:\Users\Docs\Assist Quest Editor\worlds\Демо Мир");

        Assert.False(result, "Между томами переименование невозможно: нужен путь копированием.");
    }

    [Fact]
    public void VolumeComparisonIsCaseInsensitive()
    {
        // «c:» и «C:» — один и тот же том; различие регистра не должно заставлять
        // приложение копировать файлы там, где достаточно переименования.
        var result = StagedFolderRules.CanRenameIntoPlace(
            @"c:\temp\aq-import-1",
            @"C:\worlds\Demo");

        Assert.True(result, "Сравнение корней не должно зависеть от регистра буквы диска.");
    }

    [Fact]
    public void UncPathsOnSameShareCanRename()
    {
        Assert.True(StagedFolderRules.CanRenameIntoPlace(
            @"\\server\share\temp\aq-import-1",
            @"\\server\share\worlds\Demo"));
    }

    [Fact]
    public void UncAndLocalPathCannotRename()
    {
        Assert.False(StagedFolderRules.CanRenameIntoPlace(
            @"\\server\share\temp\aq-import-1",
            @"C:\worlds\Demo"));
    }

    [Fact]
    public void RelativePathsAreComparedByResolvedRoot()
    {
        // Относительные пути домен не обязан понимать, но и падать на них не
        // должен: решение принимается по корню, а «отступить к копированию» —
        // безопасный ответ на любой непонятный случай.
        var result = StagedFolderRules.CanRenameIntoPlace("aq-import-1", "worlds\\Demo");

        Assert.True(result, "Относительные пути разрешаются от текущего каталога и попадают на один том.");
    }

    // --- Непонятные входы не должны ронять проверку ---

    [Theory]
    [InlineData(null, @"C:\worlds\Demo")]
    [InlineData("", @"C:\worlds\Demo")]
    [InlineData("   ", @"C:\worlds\Demo")]
    [InlineData(@"C:\temp\aq-import-1", null)]
    [InlineData(@"C:\temp\aq-import-1", "")]
    public void MissingPathsCannotRename(string? source, string? target)
    {
        // Пути нет — значит переименовывать нечего, и решение обязано быть
        // «нельзя», а не исключением: вызывающий код должен отступить, а не упасть.
        Assert.False(StagedFolderRules.CanRenameIntoPlace(source!, target!));
    }

    // --- Порядок копирования: определитель ресурса последним ---

    [Fact]
    public void DefiningFileGoesLast()
    {
        var ordered = StagedFolderRules.OrderForCopy(
            new[]
            {
                "world.aqworld",
                "campaigns/training/campaign.aqcampaign",
                "locations/training_dynamic_cache_location.aqlocation",
                @"events\test_dynamic_cache.aqevent"
            },
            "world.aqworld");

        Assert.Equal("world.aqworld", ordered[^1]);
        Assert.Equal(4, ordered.Count);
    }

    [Fact]
    public void NestedDefiningFilesGoLastToo()
    {
        // Определитель может лежать во ВЛОЖЕННОЙ папке: архивы выгружаются как
        // «папка мира + содержимое». Показать мир раньше его содержимого нельзя и
        // в этом случае.
        var ordered = StagedFolderRules.OrderForCopy(
            new[] { @"Демо Мир\world.aqworld", @"Демо Мир\campaigns\common\campaign.aqcampaign" },
            "world.aqworld");

        Assert.Equal(@"Демо Мир\world.aqworld", ordered[^1]);
    }

    [Fact]
    public void OrderForCopyKeepsEveryFile()
    {
        // Порядок не имеет права ТЕРЯТЬ файлы: пропущенный при копировании файл
        // сделал бы импорт неполным, а заметить это можно только по содержимому.
        var input = new[] { "a.aqquest", "campaign.aqcampaign", "nested/b.aqquest", "campaign.aqcampaign" };

        var ordered = StagedFolderRules.OrderForCopy(input, "campaign.aqcampaign");

        Assert.Equal(input.Length, ordered.Count);
        Assert.Equal("campaign.aqcampaign", ordered[^1]);
    }

    [Fact]
    public void DefiningFileLookupIsCaseInsensitive()
    {
        // Имя может прийти из архива, собранного на системе с другим регистром.
        var ordered = StagedFolderRules.OrderForCopy(
            new[] { "World.AQWORLD", "campaign.aqcampaign" },
            "world.aqworld");

        Assert.Equal("World.AQWORLD", ordered[^1]);
    }

    [Fact]
    public void MissingDefiningFileDoesNotThrow()
    {
        // Отсутствие определителя проверяет импорт: у него своя ошибка — «в архиве
        // нет файла мира». Правило порядка не должно подменять её своим падением.
        var ordered = StagedFolderRules.OrderForCopy(new[] { "campaign.aqcampaign" }, "world.aqworld");

        Assert.Single(ordered);
    }

    [Fact]
    public void EmptyInputIsAllowed()
    {
        Assert.Empty(StagedFolderRules.OrderForCopy(Array.Empty<string>(), "world.aqworld"));
    }
}
