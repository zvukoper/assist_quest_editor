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

    public DarkFlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        BackColor = Color.FromArgb(23, 24, 25);
        ForeColor = Color.FromArgb(220, 228, 236);
        FlatAppearance.BorderColor = Color.FromArgb(60, 66, 74);
    }

    /// <summary>
    /// Цвет текста выключенной кнопки. Значение по умолчанию даёт контраст
    /// 5,2:1 на фоне 23,24,25 — с запасом выше минимума 4,5:1, но заметно
    /// приглушённее рабочего состояния, чтобы «сейчас нельзя» было видно.
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
            DisabledTextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
