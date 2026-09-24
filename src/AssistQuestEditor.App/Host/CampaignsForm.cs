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
    // Строка квеста идёт от отступа слева до правого края плашки кампании:
    // отступ показывает вложенность, правый край образует общую линию.
    private const int QuestRowIndent = 30;
    private const int CampaignHeaderHeight = 48;
    // Раздел мира выше строки кампании: имя мира — заголовок дерева, а не
    // равноправный пункт списка.
    private const int WorldHeaderHeight = 54;
    private const string WorldCollapseKey = "::world::";
    private static readonly Color WorldHeaderColor = Color.FromArgb(38, 32, 16);
    private static readonly Color WorldRowBackColor = Color.FromArgb(28, 25, 18);
    // Минимальная высота строки: заголовок + строка деталей. Реальная высота
    // считается по переносу текста в MeasureRowHeight.
    private const int QuestRowMinHeight = 46;
    // Высота подписи автора/даты. Константа, потому что подпись — одна строка
    // без переноса, и её высота должна быть видна формуле начальной высоты
    // плашки: иначе состав кампании уезжает под нижний край окна.
    private const int SignatureRowHeight = 18;

    private readonly FlowLayoutPanel _list;
    private readonly Label _title;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstalledCampaignView> _catalog = Array.Empty<InstalledCampaignView>();
    private WorldRecord? _world;
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
        // Заголовок собирается из имени мира: окно принадлежит ОДНОМУ миру, и без
        // имени нельзя понять, чей это каталог, — особенно когда открыто два
        // мира подряд и состав кампаний похож.
        _title = new Label { Dock = DockStyle.Left, AutoSize = true, Text = "Кампании и квесты", ForeColor = AccentColor, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var hint = new Label { Dock = DockStyle.Right, AutoSize = true, Text = "ЛКМ по кампании — свернуть", ForeColor = Color.FromArgb(125, 135, 148), Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleRight };
        toolbar.Controls.Add(hint); toolbar.Controls.Add(_title);
        _list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8), BackColor = Color.FromArgb(13, 16, 20), BorderStyle = BorderStyle.None };
        _list.Resize += (_, _) => ResizeBlocks();
        Controls.Add(_list); Controls.Add(toolbar);
    }
    public event EventHandler<CampaignActiveChangedEventArgs>? CampaignActiveChanged;
    public event EventHandler<QuestEnabledChangedEventArgs>? QuestEnabledChanged;
    public event EventHandler<QuestOpenRequestedEventArgs>? QuestOpenRequested;
    public event EventHandler<CampaignFolderOpenRequestedEventArgs>? CampaignFolderOpenRequested;
    public event EventHandler<QuestSelectedEventArgs>? QuestSelected;
    public event EventHandler? WorldFolderOpenRequested;

    /// <summary>
    /// Текущее выделение квеста. Нужно Simulator: он подсвечивает на карте тот
    /// же квест, что выбран в этом окне, и наоборот.
    /// </summary>
    public (string CampaignId, string QuestId) SelectedQuest =>
        (_selectedCampaignId, _selectedQuestId);

    /// <summary>
    /// Мир, которому принадлежит окно: имя для заголовка и папка для кнопки.
    ///
    /// Ставится ДО первого показа списка: пустой мир рисует только раздел мира,
    /// а без него автор видел бы «установленных кампаний нет» вместо объяснения,
    /// в каком мире он находится.
    /// </summary>
    public void SetWorld(WorldRecord? world)
    {
        _world = world;
        _title.Text = world is null
            ? "Кампании и квесты"
            : "Кампании и квесты — " + world.DisplayName;
        Text = _title.Text;
        Rebuild();
    }

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

            // Раздел мира идёт ПЕРВЫМ и всегда: мир — корень дерева, и без него
            // список кампаний висит в воздухе. Он же даёт доступ к папке мира,
            // тогда как у кампаний своя кнопка «Папка».
            if (_world is not null)
                _list.Controls.Add(CreateWorldBlock(_world));

            if (_catalog.Count == 0)
            {
                _list.Controls.Add(CreateNotice(_world is null
                    ? "Установленных кампаний нет."
                    : "В мире «" + _world.DisplayName + "» кампаний нет: создайте её в разделе мира."));
                return;
            }

            foreach (var campaign in _catalog) _list.Controls.Add(CreateCampaignBlock(campaign));
            ResizeBlocks();
        }
        finally { _list.ResumeLayout(true); }
    }

    /// <summary>
    /// Раздел мира: имя КРУПНЫМ шрифтом и кнопка «ПАПКА».
    ///
    /// Именно ПАПКА (капитальными), а не «Папка»: это переход к файлам проекта,
    /// а не действие над кампанией, и смешивать их вид нельзя. Размер шрифта
    /// отличает раздел от кампаний: без него мир выглядел бы ещё одной
    /// кампанией в общем списке.
    /// </summary>
    private Control CreateWorldBlock(WorldRecord world)
    {
        var collapsed = _collapsed.Contains(WorldCollapseKey);
        var block = new Panel
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = WorldRowBackColor,
            Margin = new Padding(0, 0, 0, 10)
        };

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = WorldHeaderHeight,
            ColumnCount = 3,
            Padding = new Padding(10, 6, 8, 6),
            BackColor = WorldHeaderColor,
            Cursor = Cursors.Hand
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Cursor = Cursors.Hand,
            Text = (collapsed ? "▸ " : "▾ ") + world.DisplayName,
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            Margin = new Padding(0, 2, 6, 2)
        };

        var folder = CreateMicroButton("ПАПКА");
        folder.Click += (_, _) => WorldFolderOpenRequested?.Invoke(this, EventArgs.Empty);

        header.Controls.Add(text, 0, 0);
        header.Controls.Add(folder, 2, 0);
        header.Click += (_, _) => ToggleCollapsed(WorldCollapseKey);
        text.Click += (_, _) => ToggleCollapsed(WorldCollapseKey);

        block.Controls.Add(header);

        var rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = WorldRowBackColor,
            Padding = new Padding(0)
        };

        if (!collapsed)
        {
            // Подписи мелким курсивом: автора и дату читают лишь когда возникает
            // вопрос «чья это правка», и в основном списке они не должны
            // соревноваться с названиями кампаний.
            var signature = WorldDisplayRules.Describe(world.Definition.Metadata);
            var created = WorldDisplayRules.Describe(world.Definition.Metadata, modified: false);
            AddSignature(rows, created);
            AddSignature(rows, signature);

            if (!string.IsNullOrWhiteSpace(world.Definition.Description))
                rows.Controls.Add(CreateNotice(world.Definition.Description!));
        }

        block.Controls.Add(rows);
        rows.BringToFront();
        block.Height = collapsed ? WorldHeaderHeight : WorldHeaderHeight + (collapsed ? 0 : 74);
        block.Width = Math.Max(200, _list.ClientSize.Width - 24);
        return block;
    }

    /// <summary>
    /// Добавляет подпись, если подписывать есть чем.
    ///
    /// Обязательный шаг, а не удобство: <see cref="CreateSignatureRow"/> возвращает
    /// null для файлов без авторства (созданных до появления подписи), а передача
    /// null в <c>Controls.Add</c> бросает ArgumentNullException — окно падало бы на
    /// первом же старом ресурсе.
    /// </summary>
    private void AddSignature(FlowLayoutPanel rows, string? text)
    {
        var control = CreateSignatureRow(text);
        if (control is not null)
            rows.Controls.Add(control);
    }

    /// <summary>Подпись автора/даты. null означает «подписывать нечем» — строку не рисуем.</summary>
    private Control? CreateSignatureRow(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        return new Label
        {
            AutoSize = false,
            Height = SignatureRowHeight,
            Width = Math.Max(200, _list.ClientSize.Width - 40),
            Margin = new Padding(12, 0, 8, 0),
            Text = text,
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };
    }

    private Control CreateCampaignBlock(InstalledCampaignView campaign)
    {
        var collapsed = _collapsed.Contains(campaign.Id);
        var activeCount = campaign.Quests.Count(quest => quest.Status == CampaignQuestStatus.Enabled);
        var block = new Panel { BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(23, 24, 25), Margin = new Padding(0, 0, 0, 8) };
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = CampaignHeaderHeight, ColumnCount = 4, Padding = new Padding(8, 4, 7, 4), BackColor = Color.FromArgb(20, 32, 38), Cursor = Cursors.Hand };
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
        // Порядок докинга в WinForms — обратный z-order: контролы укладываются
        // от последнего в коллекции к первому. Добавляем сначала заголовок, а
        // rows поднимаем наверх, поэтому список не уезжает под плашку кампании.
        block.Controls.Add(header);

        // Предупреждение о чужом родителе — ОРАНЖЕВЫМ и ДО списка квестов.
        //
        // Не запрет: папку кампании мог перенести в чужой мир сам автор, и
        // осознанный перенос — его право. Но без предупреждения квесты кампании
        // выглядят пропавшими, а причина не видна вовсе. Цвет берётся тот же,
        // что у акцента выделения: пользователь уже знает, что это «обрати
        // внимание», а не «ошибка».
        var foreignWarning = ResourceParentRules.Describe(
            campaign.ParentWorldId, _world?.Definition.Id);

        if (foreignWarning is not null)
        {
            var warning = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                AutoSize = false,
                Padding = new Padding(12, 4, 8, 4),
                // Перенос по словам, а не обрезка: текст объясняет причину, и
                // многоточие в середине сделало бы его бесполезным.
                Text = "⚠ " + foreignWarning,
                ForeColor = AccentColor,
                Font = new Font("Segoe UI", 8.2f)
            };

            block.Controls.Add(warning);
            warning.BringToFront();
        }

        // Список квестов живёт в отдельном контейнере с прокруткой, а не в
        // общем FlowLayoutPanel окна. Высота блока при этом фиксированная: она
        // складывается из заголовка и строк, поэтому список не «наезжает» на
        // заголовок следующей кампании и не вылезает под её плашку.
        var rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(23, 24, 25),
            // Padding пустой: высота блока считается точно под строки, и лишние
            // пиксели заставляли бы список показывать полосу прокрутки всегда.
            Padding = new Padding(0)
        };
        if (!collapsed)
        {
            // Зависимости включаются ВСЕГДА: подписи необязательны (у файлов,
            // созданных до появления авторства, их нет), и добавление null в
            // коллекцию контролов бросило бы ArgumentNullException — то есть
            // окно кампаний падало бы на первом же «старом» ресурсе.
            AddSignature(rows, WorldDisplayRules.Describe(campaign.Metadata));
            AddSignature(rows, WorldDisplayRules.Describe(campaign.Metadata, modified: false));

            foreach (var quest in campaign.Quests)
                rows.Controls.Add(CreateQuestRow(campaign, quest));
            if (campaign.Quests.Count == 0)
                rows.Controls.Add(CreateNotice("В кампании нет доступных файлов Quest."));
        }
        block.Controls.Add(rows);
        rows.BringToFront();
        // Начальная высота: заголовок + строки минимальной высоты. Точную
        // высоту выставит ResizeBlocks после раскладки, когда будет известна
        // ширина колонки и фактический перенос названий.
        block.Height = collapsed
            ? CampaignHeaderHeight
            : CampaignHeaderHeight + SignatureRowHeight * 2
                + Math.Max(1, campaign.Quests.Count) * QuestRowMinHeight + 7;
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
        var row = new TableLayoutPanel { Height = QuestRowMinHeight - 3, ColumnCount = 4, CellBorderStyle = TableLayoutPanelCellBorderStyle.None, Padding = new Padding(4, 2, 4, 2), BackColor = selected ? RowSelectedBackColor : RowBackColor, Margin = new Padding(0, 0, 0, 3), Cursor = Cursors.Hand, AutoSize = false };
        // Выделенный квест подсвечивается оранжевой рамкой: раньше выделение
        // хранилось только на карте, и связать список с картой было нечем.
        row.Paint += (_, e) =>
        {
            if (!selected) return;
            using var pen = new Pen(AccentColor, 2f);
            var bounds = row.ClientRectangle;
            e.Graphics.DrawRectangle(pen, bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
        };
        // Колонки: галочка, название (растягивается и переносится по словам),
        // статус, кнопка. Раньше название стояло в растягивающейся колонке, но
        // AutoEllipsis резал длинный текст на одной строке — теперь название
        // занимает две строки (заголовок + детали) и переносит слова по ширине.
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var check = new CheckBox { AutoSize = true, Checked = enabled, Margin = new Padding(0, 8, 5, 0) };
        check.CheckedChanged += (_, _) => QuestEnabledChanged?.Invoke(this, new QuestEnabledChangedEventArgs(campaign.Id, quest.QuestId, check.Checked));
        var activation = quest.Activation;
        var details = activation is null
            ? $"#{quest.Order} · v{quest.Version}"
            : activation.Mode == QuestStartMode.Proximity
                ? $"#{quest.Order} · v{quest.Version} · Радиус {activation.Radius:0} м"
                : $"#{quest.Order} · v{quest.Version} · {activation.Mode}";
        // Отключённый квест гасится курсивом и приглушённо-белым цветом: он
        // остаётся читаемым, но сразу видно, что в игре его нет.
        var nameFont = new Font("Segoe UI", 8.5f, enabled ? FontStyle.Regular : FontStyle.Italic);
        var nameColor = selected
            ? AccentColor
            : enabled ? Color.FromArgb(231, 237, 244) : Color.FromArgb(178, 184, 192);
        var name = new Label
        {
            Dock = DockStyle.Fill,
            // AutoSize обязан быть false: по умолчанию Label сам подстраивает
            // ширину под текст, Dock при этом игнорируется, и перенос по словам
            // не работает — заголовок просто вылезал бы за строку.
            AutoSize = false,
            // Без AutoEllipsis: он обрезает длинный заголовок многоточием вместо
            // переноса. Текст разбит на две строки, поэтому перенос идёт по
            // ширине колонки, а не отбрасывается.
            AutoEllipsis = false,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = quest.Title + Environment.NewLine + details,
            ForeColor = nameColor,
            Font = nameFont,
            Margin = new Padding(0, 2, 5, 2),
            UseMnemonic = false
        };
        var status = new Label { AutoSize = true, Text = enabled ? "Включён" : "Отключён", TextAlign = ContentAlignment.MiddleRight, ForeColor = enabled ? Color.FromArgb(139, 216, 255) : Color.FromArgb(165, 172, 182), Font = new Font("Segoe UI", 8f, enabled ? FontStyle.Regular : FontStyle.Italic), Margin = new Padding(0, 14, 7, 0) };
        var edit = CreateMicroButton("Ред.");
        edit.Margin = new Padding(0, 11, 0, 0);
        edit.Click += (_, _) => QuestOpenRequested?.Invoke(this, new QuestOpenRequestedEventArgs(quest.FullPath));
        // Правое выравнивание задаётся TextAlign, а не RightToLeft: RightToLeft.Yes
        // переставляет знаки в строках вида «#1 · v1» и ломает детали квеста.
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
            // Поиск строго по типу, а не по индексу Controls[^1]: индекс — это
            // z-order, и любой BringToFront или смена порядка добавления ломала
            // бы выравнивание строк молча.
            if (block is not Panel panel)
                continue;

            var rows = panel.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            var header = panel.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (rows is null)
                continue;

            // Строка квеста начинается с отступа QuestRowIndent и доходит до
            // правого края плашки кампании: отступ показывает вложенность,
            // правый край образует общую линию с заголовком.
            var rowWidth = Math.Max(280, width - QuestRowIndent - 4);
            var totalRows = 0;
            foreach (Control row in rows.Controls)
            {
                row.Width = rowWidth;
                // Отступ вложенности и замер высоты относятся ТОЛЬКО к строкам
                // квестов. Подписи автора/дат и уведомления — не строки квеста:
                // им задавали бы отступ QuestRowIndent и высоту QuestRowMinHeight,
                // то есть подпись в 18 пикселей занимала бы 26 и сдвигала состав.
                if (row is not TableLayoutPanel)
                {
                    totalRows += row.Height + row.Margin.Vertical;
                    continue;
                }

                row.Margin = new Padding(QuestRowIndent, 0, 0, 3);
                // Высота задаётся измеренной, а не константой: заголовок
                // переносится по словам, и фиксированная высота срезала
                // длинные названия квестов.
                row.Height = MeasureRowHeight(row);
                totalRows += row.Height + row.Margin.Vertical;
            }

            // Высота плашки считается по фактическим высотам строк, а не по
            // формуле «строк × константа»: заголовок переносится по словам, и
            // фиксированная высота срезала длинные названия квестов.
            if (header is not null)
            {
                panel.Height = Math.Max(
                    CampaignHeaderHeight + 4,
                    header.Height + totalRows + 4);
            }
        }
    }

    /// <summary>
    /// Высота строки квеста по фактическому переносу названия.
    ///
    /// Ширина колонки берётся у самого TableLayoutPanel после раскладки: считать
    /// её «на глаз» нельзя, потому что рядом стоят галочка, статус и кнопка с
    /// собственными размерами. Без этого замера длинное название обрезалось.
    /// </summary>
    private static int MeasureRowHeight(Control row)
    {
        // Название берётся ПО ПОЗИЦИИ в таблице (колонка 1), а не по индексу в
        // коллекции Controls: индекс — это z-order, и после любого
        // BringToFront/AddControl имя перестало бы находиться. В строке есть и
        // другие Label (статус), поэтому «первый Label» тоже не годится.
        if (row is not TableLayoutPanel table ||
            table.GetControlFromPosition(1, 0) is not Label name)
        {
            return QuestRowMinHeight;
        }

        table.PerformLayout();
        var widths = table.GetColumnWidths();
        var available = widths.Length > 1
            ? Math.Max(80, widths[1] - name.Margin.Horizontal)
            : Math.Max(80, table.Width - 220);

        var required = TextRenderer.MeasureText(
            name.Text,
            name.Font,
            new Size(available, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        return Math.Max(
            QuestRowMinHeight,
            required.Height + table.Padding.Vertical + name.Margin.Vertical);
    }
}