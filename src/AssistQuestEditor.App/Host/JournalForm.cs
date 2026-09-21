using System.Text.Json;

namespace AssistQuestEditor.App;

public sealed record SimulatorJournalEntry(
    string EventType,
    DateTimeOffset Timestamp,
    string Source,
    string Message);

public sealed class JournalForm : Form
{
    private readonly RichTextBox _log;
    private readonly Button _returnButton;

    public JournalForm()
    {
        Text = "Assist Quest Editor — Журнал событий";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 500);
        MinimumSize = new Size(420, 260);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(6, 5, 6, 5),
            BackColor = Color.FromArgb(23, 24, 25)
        };

        var title = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Left,
            Text = "Журнал событий",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _returnButton = new Button
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            Text = "Вернуть в сайдбар",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 38, 38),
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 8f),
            Padding = new Padding(5, 2, 5, 2)
        };
        _returnButton.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
        _returnButton.Click += (_, _) => ReturnToSidebarRequested?.Invoke(this, EventArgs.Empty);

        toolbar.Controls.Add(_returnButton);
        toolbar.Controls.Add(title);

        _log = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(13, 16, 20),
            ForeColor = Color.FromArgb(218, 226, 236),
            Font = new Font("Consolas", 9f),
            DetectUrls = false,
            HideSelection = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Padding = new Padding(6)
        };

        Controls.Add(_log);
        Controls.Add(toolbar);
    }

    public event EventHandler? ReturnToSidebarRequested;

    public void SetEntries(IEnumerable<SimulatorJournalEntry> entries)
    {
        var items = entries.ToArray();
        var lines = items.Select(entry =>
        {
            var time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var source = string.IsNullOrWhiteSpace(entry.Source) ? "Источник" : entry.Source;
            var detail = string.IsNullOrWhiteSpace(entry.Message) ? "" : "  " + entry.Message;
            return $"{time}  {entry.EventType}  [{source}]{detail}";
        });

        _log.Text = string.Join(Environment.NewLine, lines);
        _log.SelectionStart = 0;
        _log.SelectionLength = 0;
    }
}
