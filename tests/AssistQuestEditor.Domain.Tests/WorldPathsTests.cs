using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Контракт раскладки папок мира.
///
/// Проверяется отдельно от файлового стора, потому что это ОБМЕННЫЙ формат:
/// автор копирует папку мира другому игроку, и структура обязана остаться
/// понятной без приложения. Изменение любого сегмента — ломающее изменение, и
/// заметить его нужно на тесте, а не на чужой машине.
/// </summary>
public sealed class WorldPathsTests
{
    private static readonly string Root = Path.Combine("C:", "Users", "Author", "Documents", "Assist Quest Editor");

    private static string Rel(string path) =>
        Path.GetRelativePath(Root, path).Replace('\\', '/');

    [Fact]
    public void WorldTreeKeepsTheDocumentedShape()
    {
        // Полная раскладка одним взглядом: если поменяется любой сегмент, тест
        // покажет новую форму целиком, а не «одно поле не совпало».
        var worldFolder = WorldPaths.WorldFolder(Root, "SibirMap");
        var campaignsRoot = WorldPaths.CampaignsRoot(worldFolder);
        var commonFolder = WorldPaths.CampaignFolder(worldFolder, "Common");

        Assert.Equal("worlds", Rel(WorldPaths.WorldsRoot(Root)));
        Assert.Equal("worlds/SibirMap", Rel(worldFolder));
        Assert.Equal("worlds/SibirMap/world.aqworld", Rel(WorldPaths.WorldFilePath(worldFolder)));
        Assert.Equal("worlds/SibirMap/world.png", Rel(Path.Combine(worldFolder, WorldPaths.WorldImageFileName)));
        Assert.Equal("worlds/SibirMap/Saves", Rel(WorldPaths.SavesFolderPath(worldFolder)));
        Assert.Equal("worlds/SibirMap/campaigns", Rel(campaignsRoot));
        Assert.Equal("worlds/SibirMap/campaigns/Common", Rel(commonFolder));
        Assert.Equal("worlds/SibirMap/campaigns/Common/campaign.aqcampaign", Rel(WorldPaths.CampaignFilePath(commonFolder)));
        Assert.Equal("worlds/SibirMap/campaigns/Common/quests", Rel(WorldPaths.QuestsFolderPath(commonFolder)));
        Assert.Equal("worlds/SibirMap/campaigns/Common/scenes", Rel(WorldPaths.ScenesFolderPath(commonFolder)));
    }

    [Fact]
    public void SavesLiveInsideTheirOwnWorld()
    {
        // Сохранения внутри мира, а не в общем каталоге: состояние прохождения
        // несовместимо между мирами, и общая папка позволила бы загрузить чужой
        // снимок в мир с другими квестами и точками.
        var first = WorldPaths.SavesFolderPath(WorldPaths.WorldFolder(Root, "WorldA"));
        var second = WorldPaths.SavesFolderPath(WorldPaths.WorldFolder(Root, "WorldB"));

        Assert.NotEqual(first, second);
        Assert.StartsWith(Rel(WorldPaths.WorldFolder(Root, "WorldA")), Rel(first));
        Assert.EndsWith("/Saves", Rel(first));
    }

    [Fact]
    public void CampaignFileNamesDifferBetweenWorldsButShareTheLayout()
    {
        var a = WorldPaths.CampaignFilePath(WorldPaths.CampaignFolder(WorldPaths.WorldFolder(Root, "A"), "Common"));
        var b = WorldPaths.CampaignFilePath(WorldPaths.CampaignFolder(WorldPaths.WorldFolder(Root, "B"), "Common"));

        // Одинаковая кампания с одним и тем же Id лежит в РАЗНЫХ папках, потому
        // что её адрес включает мир. Иначе два мира не смогли бы иметь по
        // кампании Common — а они обязаны.
        Assert.NotEqual(a, b);
        Assert.EndsWith("/Common/campaign.aqcampaign", a.Replace('\\', '/'));
        Assert.EndsWith("/Common/campaign.aqcampaign", b.Replace('\\', '/'));
    }

    [Fact]
    public void FormatNamesAreDistinctAndStable()
    {
        // Форматы различаются по строке в файле: один и тот же загрузчик не
        // должен принимать мир за кампанию.
        Assert.Equal("aqworld", WorldDefinitionRules.FormatName);
        Assert.NotEqual(WorldDefinitionRules.FormatName, "aqcampaign");
        Assert.NotEqual(WorldPaths.WorldFileName, WorldPaths.CampaignFileName);
        Assert.NotEqual(WorldPaths.WorldFileName, WorldPaths.WorldImageFileName);
    }

    [Fact]
    public void ExportFoldersAreUniquePerMoment()
    {
        // Метка времени в имени папки: две выгрузки подряд не должны
        // перемешиваться — вложение содержит СТРУКТУРУ мира.
        var first = WorldPaths.ExportFolder(Root, new DateTimeOffset(2026, 9, 24, 11, 37, 5, TimeSpan.Zero));
        var second = WorldPaths.ExportFolder(Root, new DateTimeOffset(2026, 9, 24, 11, 37, 6, TimeSpan.Zero));

        Assert.NotEqual(first, second);
        Assert.StartsWith("Exported/", Rel(first));
        Assert.Matches(@"Exported/\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", Rel(first));
    }

    [Fact]
    public void FolderNameNeverKeepsPathSeparators()
    {
        // Имя мира становится именем ПАПКИ. Символы пути обязаны быть заменены,
        // иначе мир «A/B» создал бы вложенную папку и сломал раскладку.
        var folderName = ResourceNaming.ToFolderName("A/B:C*D?E");

        Assert.DoesNotContain('/', folderName);
        Assert.DoesNotContain('\\', folderName);
        Assert.DoesNotContain(':', folderName);
        Assert.DoesNotContain('*', folderName);
        Assert.DoesNotContain('?', folderName);
        Assert.True(ResourceNaming.IsValidName(folderName), "Имя папки обязано быть допустимым: " + folderName);
    }

    [Fact]
    public void NamingRejectsTrailingSpacesAndPathSeparators()
    {
        Assert.True(ResourceNaming.IsValidName("Sibir Map"));
        Assert.False(ResourceNaming.IsValidName(" SibirMap"));
        Assert.False(ResourceNaming.IsValidName("SibirMap "));
        Assert.False(ResourceNaming.IsValidName("Sibir/Map"));
        Assert.False(ResourceNaming.IsValidName(""));
        Assert.False(ResourceNaming.IsValidName("   "));
        Assert.False(ResourceNaming.IsValidName(null));
    }
}
