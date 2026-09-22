using System.Text.Json;
using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Свойства мира в файле кампании.
///
/// Проверки появились после реального случая порчи контента: кнопка «сохранить в
/// кампанию» записала гео-координату 0,0 (пустое поле читалось как 0), а в файл
/// утекло вычисляемое свойство isValid. Обе ошибки тихие: файл валиден, приложение
/// работает, но мир потерял своё место на Земле.
/// </summary>
public sealed class CampaignWorldSettingsTests
{
    private static CampaignDefinitionDocument Document(GeoCoordinate? geo) =>
        new(1, "aqcampaign", new CampaignDefinition(
            "sibir_map", "SibirMap", 1, true,
            Array.Empty<CampaignQuestEntry>(), Array.Empty<string>(),
            geo,
            GameCalendar.DefaultStartDate,
            new WorldStartConditions("Ясно", 0, 5000)));

    [Fact]
    public void SerializationDoesNotLeakComputedIsValid()
    {
        var json = ResourceJsonFormat.Serialize(Document(new GeoCoordinate(55.1644, 61.4368)));

        // Вычисляемое свойство не должно попадать в файл: это разметка, а не данные.
        Assert.DoesNotContain("isValid", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("55.1644", json);
        Assert.Contains("61.4368", json);
    }

    [Fact]
    public void RoundTripKeepsGeoAndStartConditions()
    {
        var json = ResourceJsonFormat.Serialize(Document(new GeoCoordinate(55.1644, 61.4368)));

        var restored = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(json);

        Assert.NotNull(restored);
        Assert.Equal(55.1644, restored!.Definition.Geo!.Latitude, 4);
        Assert.Equal(61.4368, restored.Definition.Geo.Longitude, 4);
        Assert.Equal("Ясно", restored.Definition.StartConditions!.Weather);
        Assert.Equal(5000, restored.Definition.StartConditions.VisibilityMeters);
        Assert.Equal(GameCalendar.DefaultStartDate, restored.Definition.StartDate);
    }

    [Fact]
    public void NullGeoStaysNullInsteadOfBecomingZero()
    {
        // Отсутствие координаты — это «не задано», а не «нулевая широта».
        var json = ResourceJsonFormat.Serialize(Document(null));

        var restored = ResourceJsonFormat.Deserialize<CampaignDefinitionDocument>(json);

        Assert.Null(restored!.Definition.Geo);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(55.1644, 61.4368, true)]
    [InlineData(-90, -180, true)]
    [InlineData(90, 180, true)]
    [InlineData(91, 0, false)]
    [InlineData(0, 181, false)]
    [InlineData(-91, 0, false)]
    public void IsValidAcceptsOnlyRealCoordinates(double latitude, double longitude, bool expected)
    {
        // 0,0 формально валидна (нулевой меридиан и экватор), поэтому проверка
        // допустимости НЕ защищает от «пустое поле стало нулём» — это делает
        // разбор ввода. Здесь фиксируется сама граница диапазона.
        Assert.Equal(expected, new GeoCoordinate(latitude, longitude).IsValid);
    }

    [Fact]
    public void DefaultGeoIsRealPlaceNotZero()
    {
        var geo = GeoCoordinate.CreateDefault();

        Assert.True(geo.IsValid);
        Assert.NotEqual(0, geo.Latitude);
        Assert.NotEqual(0, geo.Longitude);
    }

    [Fact]
    public void JsonIgnoreIsPresentOnComputedProperty()
    {
        // Прямая проверка атрибута: если кто-то добавит в GeoCoordinate ещё одно
        // вычисляемое свойство, тест выше поймает утечку, а этот объясняет почему.
        var property = typeof(GeoCoordinate).GetProperty(nameof(GeoCoordinate.IsValid));

        Assert.NotNull(property);
        Assert.NotNull(property!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)
            .FirstOrDefault());
    }
}
