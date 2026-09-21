namespace AssistQuestEditor.App;

public sealed class SettingsForm : Form
{
    private readonly Button _resetWindowsButton;

    public SettingsForm()
    {
        Text = "Assist Quest Editor — Настройки";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 330);
        MinimumSize = new Size(520, 280);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        WindowGeometryStore.Attach(this, "settings");

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(18, 16, 18, 6),
            Text = "Настройки интерфейса",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold)
        };

        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(18, 0, 18, 10),
            Text = "Окна приложения автоматически сохраняют координаты, размер и состояние.\r\nСохранённые данные хранятся глобально для всех окон Assist Quest Editor.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 9f)
        };

        _resetWindowsButton = new Button
        {
            AutoSize = false,
            Location = new Point(18, 140),
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
            Location = new Point(18, 194),
            Size = new Size(560, 70),
            Text = "Используйте эту кнопку, если окно оказалось за пределами экранов или запомнился некорректный размер. После повторного открытия окна получат стандартное расположение.",
            ForeColor = Color.FromArgb(125, 135, 148),
            Font = new Font("Segoe UI", 8.5f)
        };

        var closeButton = new Button
        {
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Size = new Size(96, 30),
            Location = new Point(500, 276),
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
        Controls.Add(description);
        Controls.Add(title);
    }

    public event EventHandler? ResetWindowSettingsRequested;
}
