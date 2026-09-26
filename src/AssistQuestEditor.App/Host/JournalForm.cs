using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace AssistQuestEditor.App;

public sealed record SimulatorJournalEntry(
    string EventType,
    DateTimeOffset Timestamp,
    string Source,
    string Message,
    WorldCoordinate? Coordinate = null);

public sealed class JournalForm : Form
{
    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RichTextBox _log;
    private readonly Button _returnButton;
    private readonly List<(int Start, int Length, WorldCoordinate Coordinate)> _coordinateLinks = new();

    public JournalForm()
    {
        Text = "Журнал событий";
        StartPosition = FormStartPosition.Manual;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var defaultWidth = Math.Min(620, Math.Max(420, workArea.Width / 3));
        Bounds = new Rectangle(workArea.Right - defaultWidth, workArea.Top, defaultWidth, workArea.Height);
        MinimumSize = new Size(420, 260);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        WindowGeometryStore.Attach(this, "journal");

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
        _log.MouseMove += JournalLog_MouseMove;
        _log.MouseClick += JournalLog_MouseClick;

        Controls.Add(_log);
        Controls.Add(toolbar);
    }

    public event EventHandler? ReturnToSidebarRequested;
    public event Action<WorldCoordinate>? CoordinateClicked;

    /// <summary>
    /// Нажатие общей клавиши симулятора.
    ///
    /// Окно обычное, а не <see cref="WebViewForm"/>, поэтому повторяет его роль
    /// явно: без этого «I» и Escape в журнале не работали бы, хотя Симулятор на
    /// них подписан.
    /// </summary>
    public event EventHandler<WebViewHotKeyEventArgs>? GlobalHotKeyPressed;

    /// <inheritdoc cref="WebViewForm.ProcessCmdKey"/>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (SimulatorHotKey.TryHandle(this, keyData, GlobalHotKeyPressed))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SetEntries(IEnumerable<SimulatorJournalEntry> entries)
    {
        _log.SuspendLayout();
        _log.Clear();
        _coordinateLinks.Clear();

        foreach (var entry in entries)
        {
            var time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var source = string.IsNullOrWhiteSpace(entry.Source) ? "Источник" : entry.Source;
            var formattedMessage = FormatMessage(entry.Message);
            var detail = string.IsNullOrWhiteSpace(formattedMessage) ? string.Empty : "  " + formattedMessage;

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = Color.FromArgb(125, 135, 148);
            _log.AppendText(time + "  ");

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = EventColor(entry);
            _log.AppendText(entry.EventType);

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = Color.FromArgb(205, 215, 225);
            _log.AppendText("  [" + source + "]" + detail);

            if (entry.Coordinate is { } coordinate)
            {
                _log.AppendText("  · ");
                var start = _log.TextLength;
                _log.SelectionStart = start;
                _log.SelectionLength = 0;
                _log.SelectionColor = Color.FromArgb(110, 170, 255);
                _log.SelectionFont = new Font(_log.Font, FontStyle.Underline);
                var coordinateText = FormatCoordinate(coordinate);
                _log.AppendText(coordinateText);
                _coordinateLinks.Add((start, coordinateText.Length, coordinate));
                _log.SelectionFont = _log.Font;
            }

            _log.AppendText(Environment.NewLine);
        }

        _log.SelectionStart = 0;
        _log.SelectionLength = 0;
        _log.ResumeLayout();
    }

    private static Color EventColor(SimulatorJournalEntry entry)
    {
        if (entry.Source.Equals("Движение по маршруту", StringComparison.OrdinalIgnoreCase))
            return Color.FromArgb(110, 165, 245);

        return entry.EventType.ToLowerInvariant() switch
        {
            "runtimestarted" or "questcompleted" => Color.FromArgb(110, 230, 150),
            "runtimewaiting" => Color.FromArgb(250, 176, 3),
            "runtimeeventmatched" => Color.FromArgb(110, 220, 255),
            "nodetransition" or "nodeentered" => Color.FromArgb(140, 175, 255),
            "runtimefailed" => Color.FromArgb(255, 112, 112),
            "channelchanged" => Color.FromArgb(150, 155, 165),
            "hornpressed" => Color.FromArgb(255, 145, 190),
            _ => Color.FromArgb(205, 215, 225)
        };
    }

    private void JournalLog_MouseMove(object? sender, MouseEventArgs e)
    {
        var index = _log.GetCharIndexFromPosition(e.Location);
        _log.Cursor = _coordinateLinks.Any(link =>
            index >= link.Start && index < link.Start + link.Length)
            ? Cursors.Hand
            : Cursors.Default;
    }

    private void JournalLog_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        var index = _log.GetCharIndexFromPosition(e.Location);
        var link = _coordinateLinks.FirstOrDefault(item =>
            index >= item.Start && index < item.Start + item.Length);

        if (link.Length > 0)
            CoordinateClicked?.Invoke(link.Coordinate);
    }

    private static string FormatCoordinate(WorldCoordinate coordinate) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"X {coordinate.X:0.###} · Y {coordinate.Y:0.###} · Z {coordinate.Z:0.###}");

    private static string FormatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(message);
            return JsonSerializer.Serialize(document.RootElement, MessageJsonOptions);
        }
        catch (JsonException)
        {
            return message;
        }
    }
}
