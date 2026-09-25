using System.ComponentModel;

namespace AssistQuestEditor.App;

/// <summary>
/// Плоская кнопка для тёмных диалогов с ЧИТАЕМЫМ выключенным состоянием.
///
/// Зачем отдельный класс. У обычной кнопки WinForms выключенный текст рисуется
/// СИСТЕМНЫМ серым цветом независимо от заданного <c>ForeColor</c>: на светлой
/// теме это бледно-серый, на тёмном фоне диалога он превращается в тёмно-серый.
/// Замерено пробой на настоящем экране: яркость текста 65 на фоне 24, контраст
/// 2,1:1 — вдвое ниже минимума 4,5:1, то есть не читается вовсе. Именно так
/// выглядела панель «Выбрать изображение…» в окне сведений.
///
/// Цвет задать свойством нельзя: значение подставляет сама отрисовка, поэтому
/// выключенная кнопка рисуется здесь вручную — фон, рамка и текст muted-цветом,
/// который остаётся читаемым. Включённая кнопка рисуется базовым классом: там
/// поведение темы корректно, и подменять его незачем.
/// </summary>
public class DarkFlatButton : Button
{
    private Color _disabledTextColor = Color.FromArgb(170, 178, 190);

    /// <summary>
    /// Цвет выключенного текста на СВЕТНОМ фоне.
    ///
    /// Отдельная константа нужна потому, что приглушённый серый читается только
    /// на тёмной кнопке. Акцентная кнопка оранжевая (250,176,3), и тот же серый
    /// давал на ней контраст около 1:1 — «серый на жёлтом», тот самый нечитаемый
    /// вид, ради которого класс и появился. Тёмный текст на оранжевом даёт
    /// контраст выше 8:1.
    /// </summary>
    private static readonly Color DarkDisabledTextColor = Color.FromArgb(20, 20, 20);

    /// <summary>
    /// Порог яркости фона, выше которого текст считается «на светлом».
    ///
    /// 140 — между тёмной кнопкой диалога (яркость 23) и акцентом (178): запас в
    /// обе стороны почти двукратный, поэтому промежуточные оттенки тёмной темы
    /// (панели, поля ввода) не переключат цвет случайно.
    /// </summary>
    private const int LightBackgroundLuma = 140;

    public DarkFlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        BackColor = Color.FromArgb(23, 24, 25);
        ForeColor = Color.FromArgb(220, 228, 236);
        FlatAppearance.BorderColor = Color.FromArgb(60, 66, 74);
    }

    /// <summary>
    /// Цвет текста выключенной кнопки на ТЁМНОМ фоне. Значение по умолчанию даёт
    /// контраст 5,2:1 на фоне 23,24,25 — с запасом выше минимума 4,5:1, но
    /// заметно приглушённее рабочего состояния, чтобы «сейчас нельзя» было видно.
    ///
    /// Атрибут скрывает свойство от конструктора форм: без него анализатор
    /// WinForms выдаёт WFO1000 («свойство не настраивает сериализацию кода»), а
    /// проект собирается с TreatWarningsAsErrors.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledTextColor
    {
        get => _disabledTextColor;
        set
        {
            _disabledTextColor = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Цвет выключенного текста, подобранный ПО ФОНУ кнопки.
    ///
    /// Цвет задаётся не свойством, а этим выбором, потому что одну и ту же кнопку
    /// используют и с тёмным фоном диалога, и с оранжевым акцентом. Один
    /// приглушённый серый на обоих не читается: на тёмном он верен, на оранжевом
    /// превращается в «серый на жёлтом».
    /// </summary>
    internal Color DisabledTextFor(Color background) =>
        Luma(background) > LightBackgroundLuma ? DarkDisabledTextColor : DisabledTextColor;

    /// <summary>Яркость по BT.601 — та же формула, что у пробы контраста.</summary>
    private static int Luma(Color color) =>
        (299 * color.R + 587 * color.G + 114 * color.B) / 1000;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled)
        {
            base.OnPaint(e);
            return;
        }

        // Полная отрисовка вместо базовой: базовая затирает текст системным
        // серым, и перекрыть это извне нечем.
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var background = new SolidBrush(BackColor))
            e.Graphics.FillRectangle(background, ClientRectangle);

        // Плоские кнопки в диалогах — прямоугольные, скругление рисует только
        // дерево кампаний своим помощником.
        if (FlatAppearance.BorderSize > 0 || FlatAppearance.BorderColor.A > 0)
        {
            using var border = new Pen(FlatAppearance.BorderColor);
            e.Graphics.DrawRectangle(border, bounds);
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            DisabledTextFor(BackColor),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
