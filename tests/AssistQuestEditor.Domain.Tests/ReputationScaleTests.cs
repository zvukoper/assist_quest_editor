using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Границы, цвета и семантика прогресса репутации — это требования ТЗ,
/// а не оформление: неверная граница молча искажает игровую шкалу.
/// </summary>
public sealed class ReputationScaleTests
{
    [Theory]
    [InlineData(-10000, "Враг")]
    [InlineData(-6001, "Враг")]
    [InlineData(-3001, "Враг")]
    [InlineData(-1001, "Нежелательный")]
    [InlineData(-501, "Чужак")]
    [InlineData(-301, "Подозрительный")]
    [InlineData(-300, "Нейтральный")]
    [InlineData(0, "Нейтральный")]
    [InlineData(299, "Нейтральный")]
    [InlineData(300, "Неопасный")]
    [InlineData(499, "Неопасный")]
    [InlineData(500, "Знакомый")]
    [InlineData(999, "Знакомый")]
    [InlineData(1000, "Приятель")]
    [InlineData(2999, "Приятель")]
    [InlineData(3000, "Друг")]
    [InlineData(5999, "Друг")]
    [InlineData(6000, "Свой")]
    [InlineData(10000, "Свой")]
    [InlineData(20000, "Свой")]
    public void ResolveReturnsRangePerSpecification(int value, string expected)
    {
        Assert.Equal(expected, ReputationScale.Resolve(value).Name);
    }

    [Fact]
    public void BoundaryValuesBelongToExactlyOneRange()
    {
        // Диапазоны полуоткрытые [Min, Max): нижняя граница входит, верхняя нет.
        // Это следует из ТЗ, где у последних диапазонов явно сказано
        // «Свой 6000 и более» — то есть 6000 уже принадлежит «Свой», значит
        // нижняя граница включительна.
        Assert.Equal("Неопасный", ReputationScale.Resolve(300).Name);
        Assert.Equal("Нейтральный", ReputationScale.Resolve(299).Name);
        Assert.Equal("Друг", ReputationScale.Resolve(3000).Name);
        Assert.Equal("Свой", ReputationScale.Resolve(6000).Name);

        // Отрицательная сторона подчиняется тому же правилу включительной нижней
        // границы, поэтому -300 ещё нейтрально. Из-за этого границы ±300
        // несимметричны по принадлежности — так записано в ТЗ («300 -300
        // нейтральный» как зона, «после 300» уже lime).
        Assert.Equal("Нейтральный", ReputationScale.Resolve(-300).Name);
        Assert.Equal("Подозрительный", ReputationScale.Resolve(-301).Name);

        // «Враг» по ТЗ тянется от -3000 и «-6000 и более», то есть до конца шкалы.
        Assert.Equal("Враг", ReputationScale.Resolve(-3001).Name);
        Assert.Equal("Враг", ReputationScale.Resolve(-6000).Name);
        Assert.Equal("Враг", ReputationScale.Resolve(-10000).Name);
    }

    [Fact]
    public void FillColourBoundariesFollowTheirOwnSpecWording()
    {
        // ТЗ описывает заливку отдельно от диапазонов: «серый между 300 и -300,
        // потом lime после 300 и красный после -300». Поэтому ровно 300 и -300
        // остаются серыми, хотя подпись при 300 уже «Неопасный».
        Assert.Equal(ReputationScale.NeutralFillColor, ReputationScale.FillColor(300));
        Assert.Equal(ReputationScale.NeutralFillColor, ReputationScale.FillColor(-300));
        Assert.Equal(ReputationScale.PositiveFillColor, ReputationScale.FillColor(301));
        Assert.Equal(ReputationScale.NegativeFillColor, ReputationScale.FillColor(-301));
    }

    [Fact]
    public void EveryRangeIsReachableAndCoverageHasNoHoles()
    {
        // Соседние диапазоны должны стыковаться без дыр: иначе значение на
        // границе не попадёт ни в один диапазон.
        var ordered = ReputationScale.Ranges
            .OrderBy(range => range.Min)
            .ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.Equal(ordered[i - 1].Max, ordered[i].Min);
        }

        Assert.Equal(ReputationScale.Ranges.Count, ordered.Length);
    }

    [Theory]
    [InlineData(-10000, "#d21f1f")]
    [InlineData(-301, "#d21f1f")]
    [InlineData(-300, "#8f9baa")]
    [InlineData(0, "#8f9baa")]
    [InlineData(300, "#8f9baa")]
    [InlineData(301, "#44ff00")]
    [InlineData(10000, "#44ff00")]
    public void FillColorIsNeutralInsideThresholdAndColouredOutside(int value, string expected)
    {
        Assert.Equal(expected, ReputationScale.FillColor(value));
    }

    [Fact]
    public void PositiveRangeColoursMatchSpecification()
    {
        var expected = new Dictionary<string, string>
        {
            ["Неопасный"] = "#00a7bd",
            ["Знакомый"] = "#00614a",
            ["Приятель"] = "#224f33",
            ["Друг"] = "#175e0d",
            ["Свой"] = "#44ff00"
        };

        foreach (var (name, color) in expected)
        {
            var range = ReputationScale.Ranges.Single(item => item.Name == name);
            Assert.Equal(color, range.TextColor);
        }
    }

    [Fact]
    public void NegativeRangesUseRedScale()
    {
        // Для отрицательных диапазонов заданы цвета красной шкалы.
        Assert.Equal("#5e234d", ReputationScale.Ranges.Single(r => r.Name == "Подозрительный").TextColor);
        Assert.Equal("#ada010", ReputationScale.Ranges.Single(r => r.Name == "Чужак").TextColor);
        Assert.Equal("#940000", ReputationScale.Ranges.Single(r => r.Name == "Враг").TextColor);

        // В ТЗ ровно четыре отрицательных диапазона; «Враждебный» не вводим.
        Assert.Equal(4, ReputationScale.Ranges.Count(range => range.Max <= 0));
        Assert.DoesNotContain(ReputationScale.Ranges, range => range.Name == "Враждебный");

        // «Враг» — крайний диапазон, он продолжается за -6000.
        var enemy = ReputationScale.Ranges.Single(range => range.Name == "Враг");
        Assert.Equal(ReputationScale.MinReputation, enemy.Min);
        Assert.True(enemy.IsLowest);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(300, 3)]
    [InlineData(1000, 10)]
    [InlineData(5000, 50)]
    [InlineData(10000, 100)]
    [InlineData(-10000, 100)]
    [InlineData(-5000, 50)]
    [InlineData(-350, 3.5)]
    public void ProgressUsesMagnitudeSoNegativeIsNotReversed(int value, double expectedPercent)
    {
        // Отрицательная репутация не разворачивает прогрессбар: -10000 это 100%.
        Assert.Equal(expectedPercent, ReputationScale.ProgressPercent(value), 3);
    }

    [Fact]
    public void ProgressIsClampedBeyondScale()
    {
        Assert.Equal(100, ReputationScale.ProgressPercent(50000));
        Assert.Equal(100, ReputationScale.ProgressPercent(-50000));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(350, "+350")]
    [InlineData(-350, "-350")]
    [InlineData(-10000, "-10000")]
    public void ValueLabelShowsNumberWithoutPercent(int value, string expected)
    {
        // В подписи — очки репутации, а не проценты.
        Assert.Equal(expected, ReputationScale.FormatValue(value));
        Assert.DoesNotContain("%", ReputationScale.FormatValue(value));
    }

    [Fact]
    public void DescribeCombinesEverythingConsistently()
    {
        var view = ReputationScale.Describe(350);

        Assert.Equal(350, view.Value);
        Assert.Equal("+350", view.ValueLabel);
        Assert.Equal("Неопасный", view.RangeName);
        Assert.Equal("#00a7bd", view.RangeColor);
        Assert.Equal("#44ff00", view.FillColor);
        Assert.Equal(3.5, view.ProgressPercent, 3);
        Assert.Contains("Неопасный", view.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeNegativeValueKeepsPositiveProgress()
    {
        var view = ReputationScale.Describe(-6000);

        Assert.Equal("-6000", view.ValueLabel);
        Assert.Equal("Враг", view.RangeName);
        Assert.Equal("#d21f1f", view.FillColor);
        Assert.Equal(60, view.ProgressPercent, 3);
    }
}
