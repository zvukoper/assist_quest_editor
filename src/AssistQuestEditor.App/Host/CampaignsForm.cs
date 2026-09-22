using AssistQuestEditor.Domain;
namespace AssistQuestEditor.App;

// EventArgs — обычный класс, а запись наследуется только от записи или объекта,
// поэтому события окна описаны классами, как и остальные EventArgs в проекте.
public sealed class CampaignActiveChangedEventArgs : EventArgs
{
    public CampaignActiveChangedEventArgs(string campaignId, bool active)
    {
        CampaignId = campaignId;
        Active = active;
    }

    public string CampaignId { get; }
    public bool Active { get; }
}

public sealed class QuestEnabledChangedEventArgs : EventArgs
{
    public QuestEnabledChangedEventArgs(string campaignId, string questId, bool enabled)
    {
        CampaignId = campaignId;
        QuestId = questId;
        Enabled = enabled;
    }

    public string CampaignId { get; }
    public string QuestId { get; }
    public bool Enabled { get; }
}

public sealed class QuestOpenRequestedEventArgs : EventArgs
{
    public QuestOpenRequestedEventArgs(string path) => Path = path;

    public string Path { get; }
}

public sealed class QuestSelectedEventArgs : EventArgs
{
    public QuestSelectedEventArgs(string campaignId, string questId)
    {
        CampaignId = campaignId;
        QuestId = questId;
    }

    public string CampaignId { get; }
    public string QuestId { get; }
}

public sealed class CampaignFolderOpenRequestedEventArgs : EventArgs
{
    public CampaignFolderOpenRequestedEventArgs(string campaignId) => CampaignId = campaignId;

    public string CampaignId { get; }
}

/// <summary>Окно активации Campaign/Quest, отделённое от Simulator по тому же принципу, что Journal.</summary>
public sealed class CampaignsForm : Form
{
    // Акцент выделенного квеста. Один и тот же цвет используется и для рамки
    // строки в этом окне, и (через snapshot) для подсветки на карте Simulator.
    private static readonly Color AccentColor = Color.FromArgb(250, 176, 3);
    private static readonly Color RowBackColor = Color.FromArgb(27, 30, 34);
    private static readonly Color RowSelectedBackColor = Color.FromArgb(58, 44, 14);

    private readonly FlowLayoutPanel _list;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstalledCampaignView> _catalog = Array.Empty<InstalledCampaignView>();
    private string _selectedCampaignId = string.Empty;
    private string _selectedQuestId = string.Empty;

    public CampaignsForm()
    {
        Text = "Кампании и квесты";
        StartPosition = FormStartPosition.Manual;
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var width = Math.Min(620, Math.Max(420, workArea.Width / 3));
        Bounds = new Rectangle(workArea.Right - width, workArea.Top, width, workArea.Height);
        MinimumSize = new Size(420, 260);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);
        WindowGeometryStore.Attach(this, "campaigns");
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 6), BackColor = Color.FromArgb(23, 24, 25) };
        var title = new Label { Dock = DockStyle.Left, AutoSize = true, Text = "Кампании и квесты", ForeColor = AccentColor, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var hint = new Label { Dock = DockStyle.Right, AutoSize = true, Text = "ЛКМ по кампании — свернуть", ForeColor = Color.FromArgb(125, 135, 148), Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleRight };
        toolbar.Controls.Add(hint); toolbar.Controls.Add(title);
        _list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8), BackColor = Color.FromArgb(13, 16, 20), BorderStyle = BorderStyle.None };
        _list.Resize += (_, _) => ResizeBlocks();
        Controls.Add(_list); Controls.Add(toolbar);
    }
    public event EventHandler<CampaignActiveChangedEventArgs>? CampaignActiveChanged;
    public event EventHandler<QuestEnabledChangedEventArgs>? QuestEnabledChanged;
    public event EventHandler<QuestOpenRequestedEventArgs>? QuestOpenRequested;
    public event EventHandler<CampaignFolderOpenRequestedEventArgs>? CampaignFolderOpenRequested;
    public event EventHandler<QuestSelectedEventArgs>? QuestSelected;

    /// <summary>
    /// Текущее выделение квеста. Нужно Simulator: он подсвечивает на карте тот
    /// же квест, что выбран в этом окне, и наоборот.
    /// </summary>
    public (string CampaignId, string QuestId) SelectedQuest =>
        (_selectedCampaignId, _selectedQuestId);

    public void SetCatalog(IReadOnlyList<InstalledCampaignView> catalog)
    {
        _catalog = catalog ?? Array.Empty<InstalledCampaignView>();
        Rebuild();
    }

    /// <summary>
    /// Выделяет квест и раскрывает его кампанию. Используется, когда квест
    /// выбран кликом по карте Simulator: окно должно показать ту же строку.
    /// Повторный вызов с тем же квестом только перестраивает список.
    /// </summary>
    public void SelectQuest(string campaignId, string questId)
    {
        var campaign = _catalog.FirstOrDefault(item =>
            item.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
        var quest = campaign?.Quests.FirstOrDefault(item =>
            item.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase));

        if (campaign is null || quest is null)
            return;

        _selectedCampaignId = campaign.Id;
        _selectedQuestId = quest.QuestId;
        _collapsed.Remove(campaign.Id);
        Rebuild();
    }

    private void Rebuild()
    {
        _list.SuspendLayout();
        try
        {
            foreach (Control child in _list.Controls) child.Dispose();
            _list.Controls.Clear();
            if (_catalog.Count == 0) { _list.Controls.Add(CreateNotice("Установленных кампаний нет.")); return; }
            foreach (var campaign in _catalog) _list.Controls.Add(CreateCampaignBlock(campaign));
            ResizeBlocks();
        }
        finally { _list.ResumeLayout(true); }
    }

    private Control CreateCampaignBlock(InstalledCampaignView campaign)
    {
        var collapsed = _collapsed.Contains(campaign.Id);
        var activeCount = campaign.Quests.Count(quest => quest.Status == CampaignQuestStatus.Enabled);
        var block = new Panel { BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(23, 24, 25), Margin = new Padding(0, 0, 0, 8) };
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 4, Padding = new Padding(8, 4, 7, 4), BackColor = Color.FromArgb(20, 32, 38), Cursor = Cursors.Hand };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var active = new CheckBox { AutoSize = true, Checked = campaign.Active, Margin = new Padding(0, 6, 6, 0) };
        active.CheckedChanged += (_, _) => CampaignActiveChanged?.Invoke(this, new CampaignActiveChangedEventArgs(campaign.Id, active.Checked));
        var text = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Cursor = Cursors.Hand, Text = (collapsed ? "▸ " : "▾ ") + campaign.Name + Environment.NewLine +
            $"квесты: {activeCount}/{campaign.Quests.Count} · v{campaign.Version}", ForeColor = Color.FromArgb(238, 243, 248), Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 2, 6, 2) };
        var state = new Label { AutoSize = true, Text = campaign.Active ? "Активна" : "Отключена", ForeColor = campaign.Active ? Color.FromArgb(139, 216, 255) : Color.FromArgb(135, 145, 157), Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 7, 8, 0) };
        var folder = CreateMicroButton("Папка");
        folder.Click += (_, _) => CampaignFolderOpenRequested?.Invoke(this, new CampaignFolderOpenRequestedEventArgs(campaign.Id));
        header.Controls.Add(active, 0, 0); header.Controls.Add(text, 1, 0); header.Controls.Add(state, 2, 0); header.Controls.Add(folder, 3, 0);
        // ЛКМ по заголовку (кроме галочки, статуса и кнопки) сворачивает список
        // квестов кампании: это позволяет держать длинный каталог компактным.
        header.Click += (_, _) => ToggleCollapsed(campaign.Id);
        text.Click += (_, _) => ToggleCollapsed(campaign.Id);
        block.Controls.Add(header);

        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false, BackColor = Color.FromArgb(23, 24, 25), Padding = new Padding(8, 0, 8, 4) };
        if (!collapsed)
        {
            foreach (var quest in campaign.Quests)
                rows.Controls.Add(CreateQuestRow(campaign, quest));
            if (campaign.Quests.Count == 0)
                rows.Controls.Add(CreateNotice("В кампании нет доступных файлов Quest."));
        }
        block.Controls.Add(rows);
        block.Height = collapsed ? 48 : 48 + Math.Max(1, campaign.Quests.Count) * 44;
        return block;
    }

    private void ToggleCollapsed(string campaignId)
    {
        if (!_collapsed.Remove(campaignId))
            _collapsed.Add(campaignId);

        Rebuild();
    }

    private Control CreateQuestRow(InstalledCampaignView campaign, InstalledQuestView quest)
    {
        var selected = quest.QuestId.Equals(_selectedQuestId, StringComparison.OrdinalIgnoreCase) &&
            campaign.Id.Equals(_selectedCampaignId, StringComparison.OrdinalIgnoreCase);
        var enabled = quest.Status == CampaignQuestStatus.Enabled;
        var row = new TableLayoutPanel { Height = 40, Width = 520, ColumnCount = 4, Padding = new Padding(4, 2, 4, 2), BackColor = selected ? RowSelectedBackColor : RowBackColor, Margin = new Padding(0, 0, 0, 3), Cursor = Cursors.Hand };
        // Выделенный квест подсвечивается оранжевой рамкой: раньше выделение
        // хранилось только на карте, и связать список с картой было нечем.
        row.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
        row.Paint += (_, e) =>
        {
            if (!selected) return;
            using var pen = new Pen(AccentColor, 2f);
            var bounds = row.ClientRectangle;
            e.Graphics.DrawRectangle(pen, bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var check = new CheckBox { AutoSize = true, Checked = enabled, Margin = new Padding(0, 6, 5, 0) };
        check.CheckedChanged += (_, _) => QuestEnabledChanged?.Invoke(this, new QuestEnabledChangedEventArgs(campaign.Id, quest.QuestId, check.Checked));
        var activation = quest.Activation;
        var details = activation is null
            ? $"#{quest.Order} · v{quest.Version}"
            : activation.Mode == QuestStartMode.Proximity
                ? $"#{quest.Order} · v{quest.Version} · Радиус {activation.Radius:0} м"
                : $"#{quest.Order} · v{quest.Version} · {activation.Mode}";
        var name = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Cursor = Cursors.Hand, Text = quest.Title + Environment.NewLine + details, ForeColor = selected ? AccentColor : Color.FromArgb(231, 237, 244), Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 1, 5, 1) };
        var status = new Label { AutoSize = true, Text = enabled ? "Включён" : "Отключён", ForeColor = enabled ? Color.FromArgb(139, 216, 255) : Color.FromArgb(135, 145, 157), Font = new Font("Segoe UI", 8f), Margin = new Padding(0, 7, 7, 0) };
        var edit = CreateMicroButton("Ред.");
        edit.Click += (_, _) => QuestOpenRequested?.Invoke(this, new QuestOpenRequestedEventArgs(quest.FullPath));
        row.Controls.Add(check, 0, 0); row.Controls.Add(name, 1, 0); row.Controls.Add(status, 2, 0); row.Controls.Add(edit, 3, 0);
        // Выделение по ЛКМ на любом элементе строки, кроме галочки и кнопки:
        // клик по ним уже означает другое действие.
        foreach (Control element in new Control[] { row, name, status })
        {
            element.Click += (_, _) => QuestSelected?.Invoke(this, new QuestSelectedEventArgs(campaign.Id, quest.QuestId));
        }
        return row;
    }
    private static Button CreateMicroButton(string text)
    {
        var button = new Button { AutoSize = true, Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(38, 38, 38), ForeColor = Color.FromArgb(231, 237, 244), Font = new Font("Segoe UI", 8f), Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 4, 0, 4) };
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70); return button;
    }
    private static Label CreateNotice(string message) => new() { AutoSize = true, Text = message, ForeColor = Color.FromArgb(165, 175, 185), Font = new Font("Segoe UI", 8.5f), Padding = new Padding(4, 6, 4, 6), Margin = new Padding(0, 0, 0, 8) };
    private void ResizeBlocks()
    {
        var width = Math.Max(360, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
        foreach (Control block in _list.Controls)
        {
            block.Width = width;
            if (block is Panel panel && panel.Controls.Count > 0 && panel.Controls[^1] is FlowLayoutPanel rows)
            {
                var rowWidth = Math.Max(300, width - rows.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
                foreach (Control row in rows.Controls) row.Width = rowWidth;
            }
        }
    }
}