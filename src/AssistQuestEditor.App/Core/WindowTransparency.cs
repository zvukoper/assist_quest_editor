using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AssistQuestEditor.App;

/// <summary>
/// Делает безрамочное окно прозрачным ПО АЛЬФА-КАНАЛУ картинки.
///
/// Зачем это нужно. WinForms-форма умеет принять `BackgroundImage` с альфой, но
/// само окно остаётся НЕПРОЗРАЧНЫМ: под пикселями с alpha = 0 виден цвет формы
/// (`BackColor`), а не рабочий стол. Именно поэтому заставка выглядела как
/// «логотип на чёрном квадрате» — PNG прозрачный, а окно рисовало под ним
/// тёмный прямоугольник 800×450.
///
/// Почему не `TransparencyKey`. Он выбивает РОВНО один цвет, поэтому:
/// - края рвутся на сглаженных пикселях и мягкой тени — у логотипа и то и другое;
/// - любой похожий цвет ВНУТРИ картинки стал бы дырой насквозь.
///
/// Работает через `UpdateLayeredWindow` со стилем `WS_EX_LAYERED`: окно получает
/// ПОПИКСЕЛЬНУЮ альфу из готового битмапа. Побочная выгода — полностью
/// прозрачные пиксели становятся «сквозными» и для мыши, поэтому большая пустая
/// область вокруг логотипа ничего не перекрывает.
/// </summary>
internal static class WindowTransparency
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;

    /// <summary>
    /// Подменяет содержимое окна битмапом с альфой.
    ///
    /// Возвращает <c>false</c>, если применить не удалось: тогда вызывающий код
    /// обязан оставить обычную отрисовку, а не показать пустое окно. Молчаливая
    /// неудача здесь выглядела бы как «заставка исчезла».
    /// </summary>
    public static bool Apply(Form form, Bitmap surface)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(surface);

        if (!OperatingSystem.IsWindows() || form.IsDisposed || !form.IsHandleCreated)
            return false;

        // Системная рамка рисуется ПОВЕРХ layered-содержимого, поэтому окно
        // обязано быть безрамочным: иначе прозрачность «не работает» ровно на
        // рамке, а это выглядит как дефект самой прозрачности.
        if (form.FormBorderStyle != FormBorderStyle.None)
            return false;

        var screenDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var bitmapHandle = IntPtr.Zero;
        var previousHandle = IntPtr.Zero;
        var styleApplied = false;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memoryDc = CreateCompatibleDC(screenDc);
            if (screenDc == IntPtr.Zero || memoryDc == IntPtr.Zero)
                return false;

            bitmapHandle = surface.GetHbitmap(Color.FromArgb(0));
            previousHandle = SelectObject(memoryDc, bitmapHandle);

            var size = new Size(surface.Width, surface.Height);
            var source = new Point(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };

            // Стиль ставится ДО содержимого, как в документации Windows.
            var style = GetWindowLongPtr(form.Handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(form.Handle, GwlExStyle, new IntPtr(style | WsExLayered));
            styleApplied = true;

            // Позиция НЕ задаётся (null): окно уже стоит там, куда его поставил
            // StartPosition, и пересчитывать её здесь значило бы дублировать
            // раскладку WinForms.
            var updated = UpdateLayeredWindow(
                form.Handle,
                screenDc,
                IntPtr.Zero,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha);

            if (updated)
                return true;

            // Содержимое не приняли — снимаем стиль. Окно, помеченное layered с
            // пустым содержимым, не рисует НИЧЕГО, то есть заставка просто
            // пропала бы, и причина была бы не видна.
            SetWindowLongPtr(form.Handle, GwlExStyle, new IntPtr(style));
            styleApplied = false;

            AppLogger.Warn("Прозрачность заставки не применена.",
                "UpdateLayeredWindow вернул false; остаётся обычная отрисовка.");
            return false;
        }
        catch (Exception ex)
        {
            if (styleApplied)
            {
                // Откат стиля: иначе исключение оставило бы невидимое окно.
                var style = GetWindowLongPtr(form.Handle, GwlExStyle).ToInt64();
                SetWindowLongPtr(form.Handle, GwlExStyle, new IntPtr(style & ~(long)WsExLayered));
            }

            AppLogger.Warn("Прозрачность заставки не применена.", ex.Message);
            return false;
        }
        finally
        {
            if (previousHandle != IntPtr.Zero) SelectObject(memoryDc, previousHandle);
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Помечено ли окно как layered. Нужно пробе: по этому признаку видно, что
    /// прозрачность вообще применялась, — иначе непрозрачная заставка и не
    /// применённая прозрачность выглядят одинаково (тёмный прямоугольник).
    /// </summary>
    public static bool IsLayered(Form form)
    {
        if (form is null || form.IsDisposed || !form.IsHandleCreated || !OperatingSystem.IsWindows())
            return false;

        return (GetWindowLongPtr(form.Handle, GwlExStyle).ToInt64() & WsExLayered) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    // IntPtr-варианты: приложение собирается под x64, и 32-битные GetWindowLong
    // на этом окне формально работают только со стилями, а не с указателями.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd,
        IntPtr hDcDst,
        IntPtr destinationPoint,
        ref Size size,
        IntPtr hDcSrc,
        ref Point sourcePoint,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);
}
