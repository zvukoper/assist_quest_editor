using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed record SimulatorJournalEntry(
    string EventType,
    DateTimeOffset Timestamp,
    string Source,
    string Message,
    WorldCoordinate? Coordinate = null,
    bool Compact = false,
    string? TextColor = null,
    int FontWeight = 400);

/// <summary>
/// Что открыть по клику на подсвеченный фрагмент строки журнала.
///
/// Координата раньше была ЕДИНСТВЕННЫМ кликабельным фрагментом, и её тип был
/// зашит в список ссылок. Теперь кликабельны ещё и показатели, перки и предметы
/// (требование автора: «названия шкал, перков и предметов должны быть
/// кликабельными»), поэтому вид ссылки стал данными, а не отдельным списком на
/// каждый случай.
///
/// Сам ВИД (<see cref="JournalLinkKind"/>) и разбор разметки живут в домене
/// (<see cref="JournalLinkMarkup"/>): разметку пишет домен, и вторая её половина
/// в окне означала бы, что правка формата на одной стороне не видна другой.
/// </summary>
public sealed class JournalForm : Form
{
    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RichTextBox _log;
    private readonly Button _returnButton;

    /// <summary>
    /// Кликабельные фрагменты строки журнала вместе с тем, что по ним открывать.
    ///
    /// Раньше список хранил только координаты, и других ссылок быть не могло.
    /// Теперь элементы разметки заданы данными (<c>[[metric:energy]]</c> и т.п.),
    /// поэтому вид ссылки хранится рядом с её позицией, а обработка клика — одна.
    ///
    /// Координата лежит в самой записи, а не выводится из позиции: у ссылки на
    /// показатель координаты нет, а у координатной — нет значения, и попытка
    /// восстановить одно из другого по индексу связала бы два независимых поля.
    /// </summary>
    private readonly List<JournalLink> _links = new();

    private sealed record JournalLink(
        int Start,
        int Length,
        JournalLinkKind Kind,
        string Value,
        WorldCoordinate? Coordinate = null,
        JournalLinkMarkup? Markup = null);

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

    /// <summary>Клик по координате события.</summary>
    public event Action<WorldCoordinate>? CoordinateClicked;

    /// <summary>
    /// Клик по показателю: открыть монитор показателей и выделить его блок.
    ///
    /// Метрика — это ключ шкалы («energy», «stress», …), который знает и монитор:
    /// подсветка ищется по тому же ключу, поэтому новый вид ссылки не требует
    /// второй таблицы соответствий.
    /// </summary>
    public event Action<string>? MetricClicked;

    /// <summary>
    /// Клик по перку, баффу или дебаффу: открыть окно «Перки, баффы, скиллы»
    /// с подсветкой пункта.
    /// </summary>
    public event Action<string, string>? PerkClicked;

    /// <summary>Клик по предмету: открыть окно «Предметы» с подсветкой пункта.</summary>
    public event Action<string>? ItemClicked;


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
        _links.Clear();

        foreach (var entry in entries)
        {
            var time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var formattedMessage = FormatMessage(entry.Message);

            if (entry.Compact)
            {
                _log.SelectionStart = _log.TextLength;
                _log.SelectionLength = 0;
                _log.SelectionColor = ParseTextColor(entry.TextColor);
                using var compactFont = JournalFont(entry.FontWeight);
                _log.SelectionFont = compactFont;
                _log.AppendText(time + "  " + formattedMessage);
                _log.SelectionFont = _log.Font;
                _log.AppendText(Environment.NewLine);
                continue;
            }

            var source = string.IsNullOrWhiteSpace(entry.Source) ? "Источник" : entry.Source;

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
            _log.AppendText("  [" + source + "]");

            // Текст сообщения печатается ЧАСТЯМИ, а не одним AppendText: внутри
            // него встречается разметка ссылок, и каждая ссылка обязана быть
            // подчёркнута и кликабельна в своей позиции. Позиция берётся из
            // _log.TextLength, то есть из фактически набранного текста, поэтому
            // она не разъедется с разметкой.
            if (!string.IsNullOrWhiteSpace(formattedMessage))
            {
                _log.AppendText("  ");
                AppendRichText(formattedMessage);
            }

            if (entry.Coordinate is { } coordinate)
            {
                _log.AppendText("  · ");
                var start = _log.TextLength;
                _log.SelectionStart = start;
                _log.SelectionLength = 0;
                _log.SelectionFont = new Font(_log.Font, FontStyle.Underline);

                var coordinateText = FormatCoordinate(coordinate);
                _log.AppendText(coordinateText);

                _links.Add(new JournalLink(
                    start,
                    coordinateText.Length,
                    JournalLinkKind.Coordinate,
                    string.Empty,
                    coordinate));
                _log.SelectionFont = _log.Font;
            }

            _log.AppendText(Environment.NewLine);
        }

        _log.SelectionStart = 0;
        _log.SelectionLength = 0;
        _log.ResumeLayout();
    }

    /// <summary>
    /// Печатает текст с разметкой ссылок.
    ///
    /// Разметка — <c>[[metric:energy:Энергия]]</c>,
    /// <c>[[perk:burnout:debuff:Выгорание]]</c>, <c>[[item:water.bottle:Вода]]</c>.
    /// Выбрана квадратными скобками, потому что текст журнала — русская проза, и
    /// любой «обычный» разделитель (двоеточие, слэш) встречается в ней сам по себе.
    ///
    /// Последнее поле — ПОДПИСЬ, то, что читает игрок. Раньше её не было, и в
    /// журнале печаталось значение: «[[perk:rested:buff]]» показывал «buff», а
    /// «[[item:water.bottle]]» — «water.bottle».
    ///
    /// Нераспознанная разметка печатается КАК ЕСТЬ: молча съесть текст значило бы
    /// потерять часть сообщения, и игрок не понял бы, почему строка обрывается.
    /// </summary>
    private void AppendRichText(string text)
    {
        var cursor = 0;

        while (cursor < text.Length)
        {
            var open = text.IndexOf("[[", cursor, StringComparison.Ordinal);

            if (open < 0)
            {
                _log.SelectionFont = _log.Font;
                _log.SelectionColor = Color.FromArgb(205, 215, 225);
                _log.AppendText(text[cursor..]);
                return;
            }

            var close = text.IndexOf("]]", open + 2, StringComparison.Ordinal);

            if (close < 0)
            {
                _log.SelectionFont = _log.Font;
                _log.SelectionColor = Color.FromArgb(205, 215, 225);
                _log.AppendText(text[cursor..]);
                return;
            }

            // Текст перед разметкой — обычный.
            if (open > cursor)
            {
                _log.SelectionFont = _log.Font;
                _log.SelectionColor = Color.FromArgb(205, 215, 225);
                _log.AppendText(text[cursor..open]);
            }

            var payload = text[(open + 2)..close];
            var link = ParseLink(payload);

            if (link is null)
            {
                _log.SelectionFont = _log.Font;
                _log.SelectionColor = Color.FromArgb(205, 215, 225);
                _log.AppendText(text[open..(close + 2)]);
            }
            else
            {
                var start = _log.TextLength;

                _log.SelectionStart = start;
                _log.SelectionLength = 0;
                _log.SelectionColor = LinkColor(link.Kind);
                _log.SelectionFont = new Font(_log.Font, FontStyle.Underline);
                _log.AppendText(link.Label);

                _links.Add(new JournalLink(
                    start,
                    link.Label.Length,
                    link.Kind,
                    link.Value,
                    Coordinate: null,
                    Markup: link));
                _log.SelectionFont = _log.Font;
            }

            cursor = close + 2;
        }
    }

    /// <summary>
    /// Разбирает полезную нагрузку ссылки.
    ///
    /// Разбор вынесен в домен (<see cref="JournalLinkMarkup.Parse"/>): разметку
    /// ПИШЕТ домен, и держать её половину здесь значило бы, что правка формата на
    /// одной стороне не видна другой. Домен же покрыт тестами — у окна своей
    /// сборки нет, и проверить формат через форму было бы нельзя.
    /// </summary>
    private static JournalLinkMarkup? ParseLink(string payload) =>
        JournalLinkMarkup.Parse(payload);


    private static Color LinkColor(JournalLinkKind kind) => kind switch
    {
        // Координаты были синими; показатели, перки и предметы получают свой
        // оттенок, чтобы игрок по цвету понимал, куда ведёт ссылка, ещё до клика.
        JournalLinkKind.Metric => Color.FromArgb(120, 230, 170),
        JournalLinkKind.Perk => Color.FromArgb(250, 200, 90),
        JournalLinkKind.Item => Color.FromArgb(190, 160, 255),
        _ => Color.FromArgb(110, 170, 255)
    };

    private static Font JournalFont(int fontWeight) =>
        fontWeight >= 600
            ? new Font("Consolas", 9f, FontStyle.Bold)
            : new Font("Consolas", 9f, FontStyle.Regular);

    private static Color ParseTextColor(string? color) =>
        string.Equals(color, "lime", StringComparison.OrdinalIgnoreCase)
            ? Color.Lime
            : Color.FromArgb(205, 215, 225);

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
        _log.Cursor = LinkAt(e.Location) is null
            ? Cursors.Default
            : Cursors.Hand;
    }

    private void JournalLog_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        var link = LinkAt(e.Location);
        if (link is null)
            return;

        switch (link.Kind)
        {
            case JournalLinkKind.Coordinate when link.Coordinate is { } coordinate:
                CoordinateClicked?.Invoke(coordinate);
                break;

            case JournalLinkKind.Metric:
                MetricClicked?.Invoke(link.Value);
                break;

            case JournalLinkKind.Perk:
                // Идентификатор и вид перка берутся из разобранной разметки:
                // значение составное только у перка, и разбирать его второй раз
                // здесь значило бы описать один формат в двух местах.
                PerkClicked?.Invoke(
                    link.Markup?.PerkId ?? link.Value,
                    link.Markup?.PerkKind ?? string.Empty);
                break;

            case JournalLinkKind.Item:
                ItemClicked?.Invoke(link.Value);
                break;
        }
    }

    /// <summary>
    /// Ссылка под указателем, если она там есть.
    ///
    /// Позиция берётся из <see cref="RichTextBox.GetCharIndexFromPosition"/> —
    /// того же индекса, по которому ссылки и записывались, поэтому попадание не
    /// зависит от переносов строк и прокрутки.
    /// </summary>
    private JournalLink? LinkAt(Point location)
    {
        var index = _log.GetCharIndexFromPosition(location);

        foreach (var link in _links)
        {
            if (index >= link.Start && index < link.Start + link.Length)
                return link;
        }

        return null;
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
