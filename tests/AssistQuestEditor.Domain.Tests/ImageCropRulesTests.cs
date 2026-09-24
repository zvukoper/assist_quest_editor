using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Приведение тематического изображения мира/кампании к квадрату.
///
/// Правило живёт в домене, потому что его читают два места: окно свойств
/// (предпросмотр и подпись «что будет обрезано») и сама запись в папку ресурса.
/// Разойдись они — автор видел бы в предпросмотре одно, а на диске оказывалось
/// бы другое, и заметить это можно только сравнением.
/// </summary>
public sealed class ImageCropRulesTests
{
    // --- Квадрат уже квадрат: обрезать нечего ---

    [Fact]
    public void SquareImageNeedsNoCrop()
    {
        var crop = ImageCropRules.CenteredSquare(512, 512);

        Assert.Equal(512, crop.Side);
        Assert.Equal(0, crop.X);
        Assert.Equal(0, crop.Y);
        // IsNoOp важен не для красоты: он избавляет от перекодирования, которое
        // только потеряло бы качество, ничего не улучшив.
        Assert.True(crop.IsNoOp);
    }

    // --- Вытянутое вбок: обрезается по бокам ---

    [Fact]
    public void WideImageCropsSidesCentered()
    {
        var crop = ImageCropRules.CenteredSquare(1200, 400);

        // Сторона — меньшая из сторон: вписанный квадрат без растяжения.
        Assert.Equal(400, crop.Side);
        Assert.Equal(0, crop.Y);
        // По центру: 1200 - 400 = 800, пополам — 400 слева и столько же справа.
        Assert.Equal(400, crop.X);
        Assert.False(crop.IsNoOp);
    }

    // --- Вытянутое вверх: обрезается сверху и снизу ---

    [Fact]
    public void TallImageCropsTopAndBottomCentered()
    {
        var crop = ImageCropRules.CenteredSquare(300, 900);

        Assert.Equal(300, crop.Side);
        Assert.Equal(0, crop.X);
        // 900 - 300 = 600, пополам — 300 сверху.
        Assert.Equal(300, crop.Y);
        Assert.False(crop.IsNoOp);
    }

    // --- Квадрат НИКОГДА не выходит за границы изображения ---

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 5000)]
    [InlineData(5000, 1)]
    [InlineData(37, 41)]
    [InlineData(41, 37)]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    [InlineData(9999, 3)]
    public void CropAlwaysFitsInsideTheImage(int width, int height)
    {
        var crop = ImageCropRules.CenteredSquare(width, height);

        Assert.True(crop.Side > 0, "Сторона квадрата должна быть положительной.");
        Assert.True(crop.Side <= width, "Квадрат шире изображения — вылезет за границы.");
        Assert.True(crop.Side <= height, "Квадрат выше изображения — вылезет за границы.");
        Assert.True(crop.X >= 0 && crop.X + crop.Side <= width,
            $"Смещение по горизонтали выводит за границы: x={crop.X}, side={crop.Side}, width={width}.");
        Assert.True(crop.Y >= 0 && crop.Y + crop.Side <= height,
            $"Смещение по вертикали выводит за границы: y={crop.Y}, side={crop.Side}, height={height}.");
    }

    // --- Сторона равна меньшей стороне изображения ---

    [Fact]
    public void SideIsTheSmallerDimension()
    {
        Assert.Equal(400, ImageCropRules.CenteredSquare(1200, 400).Side);
        Assert.Equal(400, ImageCropRules.CenteredSquare(400, 1200).Side);
        Assert.Equal(9, ImageCropRules.CenteredSquare(16, 9).Side);
    }

    // --- Исходные размеры сохраняются в результате ---

    [Fact]
    public void CropKeepsSourceDimensions()
    {
        var crop = ImageCropRules.CenteredSquare(1024, 768);

        // Нужны подписи «квадрат 768×768 из 1024×768»: без исходных размеров
        // сообщить автору, что именно обрезано, было бы нечем.
        Assert.Equal(1024, crop.SourceWidth);
        Assert.Equal(768, crop.SourceHeight);
        Assert.Equal(768, crop.Side);
    }

    // --- Повреждённое изображение отвергается, а не даёт пустую картинку ---

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(0, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void NonPositiveDimensionsAreRejected(int width, int height)
    {
        // Молча вернуть пустой квадрат означало бы записать в ресурс пустую
        // картинку — и автор узнал бы об этом, лишь открыв свойства снова.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ImageCropRules.CenteredSquare(width, height));
    }

    // --- Центрирование проверяется арифметикой, а не «примерно» ---

    [Fact]
    public void CropIsSymmetricWhenTheOverflowIsEven()
    {
        var crop = ImageCropRules.CenteredSquare(1000, 600);

        // 1000 - 600 = 400 → по 200 с каждой стороны.
        Assert.Equal(200, crop.X);
        Assert.Equal(crop.X, crop.SourceWidth - crop.X - crop.Side);
    }

    [Fact]
    public void CropStaysInsideWhenTheOverflowIsOdd()
    {
        // Нечётная разница (99 -> 49 и 50) не должна сдвигать квадрат за край.
        var crop = ImageCropRules.CenteredSquare(100, 199);

        Assert.Equal(100, crop.Side);
        Assert.Equal(49, crop.Y);
        Assert.Equal(0, crop.X);
        Assert.True(crop.Y + crop.Side <= 199);
    }

    // --- Сторона не берётся по ширине: вертикальный снимок не растягивается ---

    [Fact]
    public void TallImageIsNotScaledDownByWidth()
    {
        // Если бы сторона бралась по ширине, изображение 300x900 превратилось бы
        // в квадрат 300x300, но со СЖАТОЙ по вертикали картинкой. Проверяем, что
        // сторона равна меньшей стороне — то есть обрезке, а не сжатию.
        var crop = ImageCropRules.CenteredSquare(300, 900);

        Assert.Equal(300, crop.Side);
        Assert.Equal(900, crop.SourceHeight);
    }
}
