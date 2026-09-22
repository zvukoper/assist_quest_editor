namespace AssistQuestEditor.App;

public sealed class SettingsForm : Form
{
    private readonly Button _registerExtensionsButton;
    private readonly Button _resetWindowsButton;

    public SettingsForm()
    {
        Text = "Assist Quest Editor — Настройки";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(650, 470);
        MinimumSize = new Size(580, 420);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        WindowGeometryStore.Attach(this, "settings");

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(18, 16, 18, 6),
            Text = "Настройки приложения",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold)
        };

        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(18, 0, 18, 10),
            Text =
                "Окна приложения автоматически сохраняют координаты, размер и состояние.\r\n" +
                "Сохранённые данные хранятся глобально для всех окон Assist Quest Editor.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 9f)
        };

        var windowsHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 122),
            Size = new Size(600, 28),
            Text = "Windows и файлы проекта",
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var windowsDescription = new Label
        {
            AutoSize = false,
            Location = new Point(18, 150),
            Size = new Size(600, 54),
            Text =
                "Зарегистрирует текущие форматы .aqquest и .aqscene в Windows, " +
                "добавит их в «Открыть с помощью» и установит иконки Assist Quest. " +
                "Если для расширения уже выбран другой редактор по умолчанию, Windows сохранит этот выбор.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.8f)
        };

        _registerExtensionsButton = new Button
        {
            AutoSize = false,
            Location = new Point(18, 212),
            Size = new Size(285, 42),
            Text = "Зарегистрировать расширения",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(28, 28, 28),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _registerExtensionsButton.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _registerExtensionsButton.Click += RegisterExtensionsButton_Click;

        _resetWindowsButton = new Button
        {
            AutoSize = false,
            Location = new Point(18, 282),
            Size = new Size(260, 42),
            Text = "Сброс настроек окон",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(28, 28, 28),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _resetWindowsButton.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _resetWindowsButton.Click += (_, _) => ResetWindowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var hint = new Label
        {
            AutoSize = false,
            Location = new Point(18, 336),
            Size = new Size(600, 58),
            Text =
                "Сброс настроек окон удаляет сохранённые координаты и размеры. " +
                "Используйте его, если окно оказалось за пределами экранов или запомнился некорректный размер.",
            ForeColor = Color.FromArgb(125, 135, 148),
            Font = new Font("Segoe UI", 8.5f)
        };

        var closeButton = new Button
        {
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Size = new Size(96, 30),
            Location = new Point(532, 394),
            Text = "Закрыть",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(32, 36, 42),
            ForeColor = Color.FromArgb(220, 228, 236)
        };
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 84);
        closeButton.Click += (_, _) => Close();

        Controls.Add(closeButton);
        Controls.Add(hint);
        Controls.Add(_resetWindowsButton);
        Controls.Add(_registerExtensionsButton);
        Controls.Add(windowsDescription);
        Controls.Add(windowsHeader);
        Controls.Add(description);
        Controls.Add(title);
    }

    public event EventHandler? ResetWindowSettingsRequested;

    private void RegisterExtensionsButton_Click(object? sender, EventArgs e)
    {
        _registerExtensionsButton.Enabled = false;
        try
        {
            var result = FileAssociationRegistry.Register();

            MessageBox.Show(
                this,
                result.Message,
                result.Success ? "Расширения Assist Quest" : "Ошибка регистрации расширений",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        finally
        {
            _registerExtensionsButton.Enabled = true;
        }
    }
}
