namespace AssistQuestEditor.App;

public sealed class SplashForm : Form
{
    private readonly Image? _image;
    private readonly Size _originalSize;
    private Bitmap? _surface;
    private bool _transparencyApplied;

    public SplashForm(string imagePath)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        ShowInTaskbar = false;
        TopMost = true;
        // BackColor под прозрачностью не виден, но он остаётся фоном обычной
        // отрисовки: если попиксельная альфа не применится, заставка обязана
        // выглядеть как прежняя тёмная карточка, а не как белый прямоугольник.
        BackColor = Color.FromArgb(10, 12, 16);
        DoubleBuffered = true;

        if (File.Exists(imagePath))
        {
            using var source = Image.FromFile(imagePath);
            _image = new Bitmap(source);
            _originalSize = _image.Size;

            // Размер здесь только задаётся ЧЕРНОВО, чтобы окно не мелькнуло чужим
            // размером. Окончательно оно выставляется в OnHandleCreated: до
            // создания окна WinForms откладывает установку размера, и показанное
            // окно «съедает» несуществующую рамку — пропорции ломались.
            ClientSize = ScaledSize(_originalSize);
        }
        else
        {
            ClientSize = new Size(800, 450);

            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Assist Quest Editor",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = BackColor,
                Font = new Font("Segoe UI", 20f, FontStyle.Regular)
            };

            Controls.Add(label);
        }

        FormClosed += (_, _) =>
        {
            _surface?.Dispose();
            _image?.Dispose();
        };
    }

    /// <summary>
    /// Применена ли попиксельная прозрачность. Нужно пробе: без этого признака
    /// «прозрачно» и «прозрачность не применилась» неразличимы снаружи.
    /// </summary>
    public bool TransparencyApplied => _transparencyApplied;

    /// <summary>Размер окна до масштабирования — для проверки соотношения сторон.</summary>
    public Size OriginalImageSize => _originalSize;

    /// <summary>
    /// Размер окна по картинке.
    ///
    /// Картинка 800×450 на экране 1920×1080 занимает заметную часть, но по высоте
    /// она теряется на больших мониторах. Пропорции при этом сохраняются ВСЕГДА:
    /// иначе мягкая тень логотипа растянулась бы по одной оси.
    /// </summary>
    private static Size ScaledSize(Size image)
    {
        if (image.Width <= 0 || image.Height <= 0)
            return new Size(800, 450);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var scale = Math.Min(1.35, Math.Min(
            area.Width * 0.55 / image.Width,
            area.Height * 0.85 / image.Height));

        // Меньше единицы не масштабируем: уменьшать готовую картинку значило бы
        // показывать логотип мельче, чем он нарисован.
        scale = Math.Max(1.0, scale);

        return new Size(
            (int)Math.Round(image.Width * scale),
            (int)Math.Round(image.Height * scale));
    }

    /// <summary>
    /// Готовит битмап с альфой ровно под размер клиентской области.
    ///
    /// Размер берётся из ПЕРЕДАННОГО размера, а не из `ClientSize`: у битмапа и
    /// у окна один источник истины, поэтому «битмап отстал от окна» невозможно.
    ///
    /// Масштабирование идёт из ИСХОДНОГО изображения в целевой прямоугольник, а
    /// не из уже растянутого: промежуточный `BackgroundImage` с
    /// `ImageLayout.Stretch` теряет альфу по краям при двойном проходе.
    /// </summary>
    private Bitmap RenderSurface(Size size)
    {
        var surface = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));

        using var graphics = Graphics.FromImage(surface);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        // Прозрачность сохраняется только при очистке в прозрачный цвет: без неё
        // новый Bitmap заполнен нулями, но любое сглаживание примешало бы чёрный.
        graphics.Clear(Color.Transparent);

        graphics.DrawImage(_image!, new Rectangle(Point.Empty, surface.Size));

        return surface;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_image is null)
            return;

        // Размер ставится именно здесь: до создания окна WinForms откладывает его,
        // и показанное окно «съедает» несуществующую рамку — пропорции ломались
        // (замерено: 1040x555 вместо 1056x594, то есть 1,874 против 1,778).
        // Битмап строится из ТОГО ЖЕ размера, поэтому отстать от окна не может.
        var size = ScaledSize(_originalSize);
        ClientSize = size;

        _surface?.Dispose();
        _surface = RenderSurface(ClientSize);

        _transparencyApplied = WindowTransparency.Apply(this, _surface);
        AppLogger.Info("Splash: прозрачность.",
            $"applied={_transparencyApplied}; size={ClientSize.Width}x{ClientSize.Height}; " +
            $"image={_originalSize.Width}x{_originalSize.Height}");
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080;
            return parameters;
        }
    }
}
