namespace AssistQuestEditor.App;

public sealed class SplashForm : Form
{
    private readonly Image? _image;

    public SplashForm(string imagePath)
    {
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(800, 450);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(10, 12, 16);
        DoubleBuffered = true;

        if (File.Exists(imagePath))
        {
            using var source = Image.FromFile(imagePath);
            _image = new Bitmap(source);
            BackgroundImage = _image;
            BackgroundImageLayout = ImageLayout.Stretch;
        }
        else
        {
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

        FormClosed += (_, _) => _image?.Dispose();
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
