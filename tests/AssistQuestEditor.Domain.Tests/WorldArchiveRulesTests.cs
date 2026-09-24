using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Правила архива `.aqezip`.
///
/// Безопасность путей проверяется домен-тестами, а не «руками»: архив приходит
/// ИЗВНЕ, и запись `../../что-угодно` при распаковке ушла бы за пределы папки
/// мира. Такую ошибку нельзя заметить по интерфейсу — она проявляется уже
/// записанным файлом в чужом месте.
/// </summary>
public sealed class WorldArchiveRulesTests
{
    // --- Безопасные пути ---

    [Theory]
    [InlineData("world.aqworld")]
    [InlineData("campaigns/Common/campaign.aqcampaign")]
    [InlineData("campaigns/Common/quests/common_intro.aqquest")]
    [InlineData("Saves/session.aqsave")]
    [InlineData("world.png")]
    [InlineData("campaigns/Common/")]
    [InlineData(@"campaigns\Common\campaign.aqcampaign")]
    public void SafePathsAccepted(string path)
    {
        Assert.True(WorldArchiveRules.IsSafeEntryPath(path), "Должен быть принят: " + path);
    }

    // --- Выход за пределы папки ---

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("campaigns/../../secret.txt")]
    [InlineData("campaigns/Common/../../../Windows/System32/drivers/etc/hosts")]
    [InlineData("..")]
    [InlineData("campaigns/..")]
    [InlineData("./world.aqworld")]
    [InlineData("campaigns/./campaign.aqcampaign")]
    public void TraversalPathsRejected(string path)
    {
        Assert.False(WorldArchiveRules.IsSafeEntryPath(path), "Должен быть отвергнут: " + path);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\System32\hosts")]
    [InlineData("C:/Windows/hosts")]
    [InlineData("c:world.aqworld")]
    public void AbsolutePathsRejected(string path)
    {
        Assert.False(WorldArchiveRules.IsSafeEntryPath(path), "Должен быть отвергнут: " + path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("campaigns//campaign.aqcampaign")]
    [InlineData("world<>.aqworld")]
    [InlineData("world|name.aqworld")]
    public void MalformedPathsRejected(string? path)
    {
        Assert.False(WorldArchiveRules.IsSafeEntryPath(path), "Должен быть отвергнут: " + path);
    }

    // --- Формат ---

    [Fact]
    public void ArchiveFormatIsDistinctFromResourceFormats()
    {
        // Загрузчик различает ресурсы по паре «schemaVersion + format», поэтому
        // архив не должен называться так же, как мир или кампания.
        Assert.Equal("aqezip", WorldArchiveRules.FormatName);
        Assert.NotEqual(WorldDefinitionRules.FormatName, WorldArchiveRules.FormatName);
        Assert.NotEqual("aqcampaign", WorldArchiveRules.FormatName);
        Assert.Equal(".aqezip", WorldArchiveRules.Extension);
    }

    [Fact]
    public void DemoWorldFileNameIsDerivedFromExtension()
    {
        // Имя демо-мира собирается из расширения: если поменять расширение,
        // константа не должна «отстать» и указывать на старый файл.
        Assert.Equal("DemoWorld" + WorldArchiveRules.Extension, WorldArchiveRules.DemoWorldFileName);
        Assert.EndsWith(".aqezip", WorldArchiveRules.DemoWorldFileName);
    }

    [Fact]
    public void ManifestFileNameIsNotAResourceFile()
    {
        // Манифест — служебная запись архива. Он не должен выглядеть как ресурс:
        // иначе загрузчики квестов и сцен попытаются его прочитать.
        Assert.Equal("archive.json", WorldArchiveRules.ManifestFileName);
        Assert.DoesNotContain(".aq", WorldArchiveRules.ManifestFileName);
    }

    [Fact]
    public void ManifestRoundTripsThroughCanonicalFormat()
    {
        // Манифест пишется тем же каноническим форматом, что остальные ресурсы:
        // он должен читаться человеком (архив можно распаковать любым
        // архиватором) и не терять поля при обратном чтении.
        var document = new WorldArchiveManifestDocument(1, WorldArchiveRules.FormatName,
            new WorldArchiveManifest(
                WorldArchiveKinds.World, "demo", "Demo World",
                FullName: "Демо Мир",
                Description: "Пустой ресурс, в разработке",
                Version: 3,
                IncludesDependencies: true,
                Entries: [new WorldArchiveEntry("world.aqworld", 512)],
                Metadata: new ResourceMetadata().WithCreated("Ruslan",
                    new DateTimeOffset(2026, 9, 24, 11, 37, 5, TimeSpan.Zero))));

        var json = ResourceJsonFormat.Serialize(document);
        var restored = ResourceJsonFormat.Deserialize<WorldArchiveManifestDocument>(json);

        Assert.NotNull(restored);
        Assert.Equal(1, restored!.SchemaVersion);
        Assert.Equal(WorldArchiveRules.FormatName, restored.Format);
        Assert.Equal(WorldArchiveKinds.World, restored.Definition.Kind);
        Assert.Equal("Демо Мир", restored.Definition.FullName);
        Assert.Equal(3, restored.Definition.Version);
        Assert.True(restored.Definition.IncludesDependencies);
        Assert.Equal("world.aqworld", restored.Definition.Entries![0].Path);
        Assert.Equal(512, restored.Definition.Entries![0].Bytes);
        Assert.Equal("Ruslan", restored.Definition.Metadata!.CreatedBy);
        // Кириллица обязана остаться литералами: архив распаковывают и читают
        // глазами, когда что-то пошло не так.
        Assert.Contains("Демо Мир", json);
    }

    [Fact]
    public void ManifestKindValuesAreStable()
    {
        // Значения попадают в файл архива, поэтому изменение сломало бы
        // совместимость с уже выгруженными архивами.
        Assert.Equal("world", WorldArchiveKinds.World);
        Assert.Equal("campaign", WorldArchiveKinds.Campaign);
        Assert.Equal("quest", WorldArchiveKinds.Quest);
    }

    // --- Процент сжатия ---

    [Fact]
    public void CompressionPercentIsReadableByHumans()
    {
        Assert.Equal(25, WorldArchiveRules.CompressionPercent(1000, 250));
        Assert.Equal(100, WorldArchiveRules.CompressionPercent(1000, 1000));
        // Уже сжатые данные (картинки) могут дать архив БОЛЬШЕ папки: честный
        // ответ важнее красивого, иначе «сжатие 104%» выглядело бы ошибкой.
        Assert.Equal(104, WorldArchiveRules.CompressionPercent(1000, 1040));
    }

    [Fact]
    public void CompressionPercentDoesNotDivideByZero()
    {
        // Пустая папка — нормальный случай (мир без файлов), и деление на ноль
        // дало бы исключение вместо честного нуля.
        Assert.Equal(0, WorldArchiveRules.CompressionPercent(0, 100));
        Assert.Equal(0, WorldArchiveRules.CompressionPercent(-5, 100));
    }
}
