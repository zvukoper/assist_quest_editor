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

public sealed class CampaignExportRequestedEventArgs : EventArgs
{
    public CampaignExportRequestedEventArgs(string campaignId) => CampaignId = campaignId;
    public string CampaignId { get; }
}

public sealed class QuestExportRequestedEventArgs : EventArgs
{
    public QuestExportRequestedEventArgs(string campaignId, string questId, string path)
    {
        CampaignId = campaignId;
        QuestId = questId;
        Path = path;
    }

    public string CampaignId { get; }
    public string QuestId { get; }
    public string Path { get; }
}

/// <summary>
/// Просьба открыть окно свойств кампании из списка.
///
/// Отдельное событие, а не «открыть свойства текущей»: список показывает НЕСКОЛЬКО
/// кампаний, и кнопка ℹ️ есть у каждой. Иначе нажатие у второй кампании открывало
/// бы свойства первой — той, что выбрана в селекторе.
/// </summary>
public sealed class CampaignPropertiesRequestedEventArgs : EventArgs
{
    public CampaignPropertiesRequestedEventArgs(string campaignId) => CampaignId = campaignId;

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
    private const int CampaignHeaderHeight = 26;
    // Раздел мира выше строки кампании: имя мира — заголовок дерева, а не
    // равноправный пункт списка.
    //
    // Высота заголовка мира РАВНА высоте заголовка кампании: мир — такой же
    // сворачиваемый пункт дерева, просто уровнем выше. Собственная высота
    // выдавала бы его за посторонний блок.
    private const string WorldCollapseKey = "::world::";
    private static readonly Color WorldHeaderColor = Color.FromArgb(38, 32, 16);
    private static readonly Color WorldRowBackColor = Color.FromArgb(28, 25, 18);
    // Минимальная высота строки: заголовок + строка деталей. Реальная высота
    // считается по переносу текста в MeasureRowHeight.
    private const int QuestRowMinHeight = 24;
    // Высота подписи автора/даты. Константа, потому что подпись — одна строка
    // без переноса, и её высота должна быть видна формуле начальной высоты
    // плашки: иначе состав кампании уезжает под нижний край окна.
    private const int SignatureRowHeight = 15;

    /// <summary>
    /// Начальная высота блока вложенных кампаний до первой раскладки.
    ///
    /// Точную высоту считает ResizeBlocks по фактическим размерам вложенных
    /// плашек. Константа нужна только чтобы блок не был нулевой высоты до
    /// первого Resize — иначе вложенные кампании не отрисовались бы вообще.
    /// </summary>
    private const int CampaignStackHeight = 80;

    // --- Оформление в стиле основной формы редактора ---
    //
    // Кнопки повторяют кнопки веб-интерфейса: скругление 7 px, фон #262626,
    // рамка rgba(255,255,255,.10), шрифт 12 px. Прежде это были плоские
    // прямоугольники с собственным размером, и окно выглядело из другого
    // приложения, чем редактор.
    private const int ButtonRadius = 7;
    private static readonly Color ButtonBackColor = Color.FromArgb(38, 38, 38);
    private static readonly Color ButtonHoverColor = Color.FromArgb(24, 31, 35);
    private static readonly Color ButtonBorderColor = Color.FromArgb(70, 70, 70);

    /// <summary>
    /// Отступ ВЛОЖЕННОГО уровня, в пикселях на уровень.
    ///
    /// Требование автора: развёрнутый список сдвигается не только вниз, но и
    /// влево-вправо — иначе пропадает древовидность. Без этого отступа все
    /// строки выравнивались по левому краю и уровни становились неразличимы.
    /// </summary>
    private const int TreeLevelIndent = 18;

    /// <summary>Отступ строк квестов ВНУТРИ кампании.</summary>
    private const int QuestRowIndent = 18;

    private readonly FlowLayoutPanel _list;
    private readonly Label _title;
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstalledCampaignView> _catalog = Array.Empty<InstalledCampaignView>();
    private WorldRecord? _world;
    private string _selectedCampaignId = string.Empty;
    private string _selectedQuestId = string.Empty;

    /// <summary>
    /// Отпечаток последнего показанного каталога.
    ///
    /// Нужен, чтобы НЕ перестраивать дерево на каждом снимке. Симулятор
    /// присылает снимок несколько раз в секунду и каждый раз передаёт каталог:
    /// безусловная перестройка пересоздавала все контролы, и список МЕРЦАЛ —
    /// особенно заметно при выделении пунктов, потому что выделение сбрасывалось
    /// и возвращалось.
    /// </summary>
    private string _catalogFingerprint = string.Empty;

    public CampaignsForm()
    {
        Text = "Кампании и квесты";
        StartPosition = FormStartPosition.Manual;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var width = Math.Min(620, Math.Max(420, workArea.Width / 3));
        Bounds = new Rectangle(workArea.Right - width, workArea.Top, width, workArea.Height);
        MinimumSize = new Size(420, 260);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);
        WindowGeometryStore.Attach(this, "campaigns");
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(7, 4, 7, 4), BackColor = Color.FromArgb(23, 24, 25) };
        // Заголовок собирается из имени мира: окно принадлежит ОДНОМУ миру, и без
        // имени нельзя понять, чей это каталог, — особенно когда открыто два
        // мира подряд и состав кампаний похож.
        _title = new Label { Dock = DockStyle.Left, AutoSize = true, Text = "Кампании и квесты", ForeColor = AccentColor, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        var hint = new Label { Dock = DockStyle.Right, AutoSize = true, Text = "ЛКМ — свернуть / развернуть", ForeColor = Color.FromArgb(125, 135, 148), Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleRight };
        var import = CreateMicroButton("Импорт");
        import.Dock = DockStyle.Right;
        import.Margin = new Padding(0, 1, 8, 1);
        import.Click += (_, _) => ImportArchiveRequested?.Invoke(this, EventArgs.Empty);
        toolbar.Controls.Add(hint); toolbar.Controls.Add(import); toolbar.Controls.Add(_title);
        _list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(5), BackColor = Color.FromArgb(13, 16, 20), BorderStyle = BorderStyle.None };
        _list.Resize += (_, _) => ResizeBlocks();
        Controls.Add(_list); Controls.Add(toolbar);

        // Двойная буферизация окна и списка.
        //
        // Без неё перестройка дерева и смена выделения проходят через
        // перерисовку «по частям»: сначала стирается фон, затем рисуются контролы,
        // и глаз успевает увидеть промежуточные кадры — то самое МЕРЦАНИЕ при
        // выделении пунктов. Свойство защищено, поэтому включается отражением.
        EnableDoubleBuffering(this);
        EnableDoubleBuffering(_list);
    }

    /// <summary>
    /// Включает двойную буферизацию контрола.
    ///
    /// <c>DoubleBuffered</c> — защищённое свойство Control, и напрямую из другого
    /// класса его не выставить. Отражение здесь оправдано: без него остаётся
    /// либо свой наследник FlowLayoutPanel, либо мигание.
    /// </summary>
    private static void EnableDoubleBuffering(Control control)
    {
        try
        {
            var property = typeof(Control).GetProperty(
                "DoubleBuffered",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);

            property?.SetValue(control, true);
        }
        catch (Exception ex)
        {
            // Не критично: без буферизации окно работает, только мигает.
            AppLogger.Warn("CampaignsForm: двойная буферизация не включена.", ex.Message);
        }
    }
    public event EventHandler<CampaignActiveChangedEventArgs>? CampaignActiveChanged;
    public event EventHandler<QuestEnabledChangedEventArgs>? QuestEnabledChanged;
    public event EventHandler<QuestOpenRequestedEventArgs>? QuestOpenRequested;
    public event EventHandler<CampaignFolderOpenRequestedEventArgs>? CampaignFolderOpenRequested;
    public event EventHandler<CampaignExportRequestedEventArgs>? CampaignExportRequested;
    public event EventHandler<QuestExportRequestedEventArgs>? QuestExportRequested;
    public event EventHandler? ImportArchiveRequested;
    public event EventHandler? WorldExportRequested;
    public event EventHandler<QuestSelectedEventArgs>? QuestSelected;
    public event EventHandler? WorldFolderOpenRequested;
    public event EventHandler? WorldPropertiesRequested;
    public event EventHandler<CampaignPropertiesRequestedEventArgs>? CampaignPropertiesRequested;

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
        // Смена мира меняет всё содержимое: отпечаток сбрасывается явно, иначе
        // дерево старого мира осталось бы на экране.
        _catalogFingerprint = string.Empty;
        Rebuild();
    }

    /// <summary>
    /// Показывает каталог кампаний.
    ///
    /// Перестройка идёт ТОЛЬКО если каталог изменился.
    ///
    /// Симулятор присылает снимок несколько раз в секунду и каждый раз передаёт
    /// каталог заново. Безусловная перестройка пересоздавала все контролы по
    /// нескольку раз в секунду: выделение сбрасывалось и возвращалось, а список
    /// МЕРЦАЛ. Отпечаток сравнивается по значимым полям, а не по ссылке: снимок
    /// каждый раз новый объект, поэтому сравнение ссылок не дало бы ничего.
    /// </summary>
    public void SetCatalog(IReadOnlyList<InstalledCampaignView> catalog)
    {
        var next = catalog ?? Array.Empty<InstalledCampaignView>();
        var fingerprint = BuildFingerprint(next);

        if (fingerprint == _catalogFingerprint)
            return;

        _catalogFingerprint = fingerprint;
        _catalog = next;
        Rebuild();
    }

    /// <summary>
    /// Отпечаток каталога: всё, что видно в дереве.
    ///
    /// Входят имена, статусы, даты, состав квестов и родитель кампании — если
    /// пропустить хотя бы одно из них, изменение не отразилось бы в списке, и
    /// это выглядело бы как «правка не сохранилась».
    /// </summary>
    private static string BuildFingerprint(IReadOnlyList<InstalledCampaignView> catalog)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var campaign in catalog)
        {
            builder.Append(campaign.Id).Append('|')
                .Append(campaign.Name).Append('|')
                .Append(campaign.Active ? '1' : '0').Append('|')
                .Append(campaign.Version).Append('|')
                .Append(campaign.FullName).Append('|')
                .Append(campaign.ParentWorldId).Append('|')
                .Append(campaign.Metadata?.CreatedOn?.Ticks ?? 0).Append('|')
                .Append(campaign.Metadata?.EffectiveModifiedOn?.Ticks ?? 0).Append('|')
                .Append(campaign.FolderPath).Append('\n');

            foreach (var quest in campaign.Quests)
            {
                builder.Append(' ').Append(quest.QuestId).Append('|')
                    .Append(quest.Title).Append('|')
                    .Append(quest.Status).Append('|')
                    .Append(quest.Version).Append('|')
                    .Append(quest.Order).Append('|')
                    .Append(quest.FullPath).Append('\n');
            }
        }

        return builder.ToString();
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

            // Кампании больше НЕ добавляются сюда: они вложены в пункт мира
            // (см. CreateWorldBlock). Плоский список плюс отдельный раздел мира
            // давали две несвязанные части, и принадлежность кампании миру
            // приходилось додумывать.
            if (_world is not null)
            {
                _list.Controls.Add(CreateWorldBlock(_world));
            }
            else
            {
                // Мир не выбран: показывать кампании без родителя нельзя —
                // непонятно, чьи они.
                _list.Controls.Add(CreateNotice("Мир не выбран."));

                foreach (var campaign in _catalog)
                    _list.Controls.Add(CreateCampaignBlock(campaign));
            }

            ResizeBlocks();
        }
        finally { _list.ResumeLayout(true); }
    }

    /// <summary>
    /// Пункт мира: ТОТ ЖЕ вид, что у кампании, но уровнем выше в дереве.
    ///
    /// Различие только двумя признаками: оранжевый текст (мир — корень) и
    /// отступ: кампании идут внутри его пункта И сдвинуты вправо, поэтому
    /// принадлежность видна без подписи.
    ///
    /// Рамки уровня НЕТ. Прежде блок был обведён прямоугольником, и вместе с
    /// рамками кампаний окно превращалось в набор вложенных рамок — уровни
    /// читались хуже, чем без них, а требование автора было именно убрать рамки
    /// и показать уровень ОТСТУПОМ.
    /// </summary>
    private Control CreateWorldBlock(WorldRecord world)
    {
        var collapsed = _collapsed.Contains(WorldCollapseKey);

        var block = new Panel
        {
            BackColor = WorldRowBackColor,
            Margin = new Padding(0, 0, 0, 6)
        };

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = CampaignHeaderHeight,
            ColumnCount = 5,
            Padding = new Padding(5, 2, 5, 2),
            BackColor = WorldHeaderColor,
            Cursor = Cursors.Hand
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var marker = new Label
        {
            AutoSize = false,
            Width = 12,
            Height = RowContentHeight,
            Text = collapsed ? "▸" : "▾",
            ForeColor = AccentColor,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.Left,
            Cursor = Cursors.Hand
        };

        var text = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Text = world.DisplayName,
            ForeColor = AccentColor,
            Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(2, 0, 5, 0),
            Cursor = Cursors.Hand
        };

        var info = CreateInfoIcon("Свойства мира");
        info.Margin = new Padding(3, 0, 5, 0);
        info.Click += (_, _) => WorldPropertiesRequested?.Invoke(this, EventArgs.Empty);

        var folder = CreateMicroButton("ПАПКА");
        folder.Click += (_, _) => WorldFolderOpenRequested?.Invoke(this, EventArgs.Empty);

        var export = CreateMicroButton("Экспорт");
        export.Click += (_, _) => WorldExportRequested?.Invoke(this, EventArgs.Empty);

        header.Controls.Add(marker, 0, 0);
        header.Controls.Add(text, 1, 0);
        header.Controls.Add(info, 2, 0);
        header.Controls.Add(folder, 3, 0);
        header.Controls.Add(export, 4, 0);

        foreach (Control element in new Control[] { header, text, marker })
            element.Click += (_, _) => ToggleCollapsed(WorldCollapseKey);

        block.Controls.Add(header);

        var rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = WorldRowBackColor,
            Padding = new Padding(0)
        };

        if (!collapsed)
        {
            AddSignature(rows, WorldDisplayRules.DescribeStamp(world.Definition.Metadata, modified: false));
            AddSignature(rows, WorldDisplayRules.DescribeStamp(world.Definition.Metadata));

            if (!string.IsNullOrWhiteSpace(world.Definition.Description))
                rows.Controls.Add(CreateNotice(world.Definition.Description!));

            foreach (var campaign in _catalog)
                rows.Controls.Add(CreateCampaignBlock(campaign));

            if (_catalog.Count == 0)
                rows.Controls.Add(CreateNotice("В этом мире кампаний нет — создайте её в разделе мира."));
        }

        block.Controls.Add(rows);
        rows.BringToFront();
        block.Height = collapsed
            ? CampaignHeaderHeight
            : CampaignHeaderHeight + SignatureRowHeight * 2 + CampaignStackHeight;

        block.Width = Math.Max(200, _list.ClientSize.Width - 20);
        return block;
    }

    /// <summary>
    /// Сколько кампаний в мире — для строки деталей пункта мира.
    ///
    /// Тот же формат, что у кампании («квесты: N/M»): строка деталей дерева
    /// обязана читаться однотипно, иначе мир и кампания выглядят разными
    /// сущностями, хотя различаются только уровнем.
    /// </summary>
    private string DescribeCampaignsCount()
    {
        if (_catalog.Count == 0)
            return "кампаний нет";

        var quests = _catalog.Sum(item => item.Quests.Count);
        var active = _catalog.Count(item => item.Active);

        return $"кампаний: {active}/{_catalog.Count} · квестов: {quests}";
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

    /// <summary>
    /// Кнопка в стиле основной формы: скруглённые углы, серый фон, рамка.
    ///
    /// Скругление рисуется <c>OnPaint</c>, а не задаётся регионом: у Button нет
    /// свойства радиуса, и «обрезка» через Region давала рваные края при
    /// перерисовке. Кнопка перерисовывается сама при наведении и нажатии —
    /// обработчики меняют только состояние и вызывают Invalidate.
    /// </summary>
    private static Button CreateRoundedButton(string text, int width, int height, int fontSize = 12)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonBackColor,
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", fontSize / 1.15f),
            Size = new Size(width, height),
            UseVisualStyleBackColor = false
        };

        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ButtonHoverColor;
        button.FlatAppearance.MouseDownBackColor = ButtonHoverColor;

        // Скруглённая рамка рисуется поверх фона: у плоской кнопки своего
        // скругления нет, а прямоугольная рамка выдавала бы её за чужой элемент.
        button.Paint += (_, e) =>
        {
            var bounds = new Rectangle(0, 0, button.Width - 1, button.Height - 1);
            using var path = RoundedPath(bounds, ButtonRadius);
            using var pen = new Pen(ButtonBorderColor);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        };

        return button;
    }

    /// <summary>Микро-кнопка в стиле основной формы: маленькая и скруглённая.</summary>
    private static Button CreateMicroButton(string text)
    {
        var button = CreateRoundedButton(text, 0, 0, fontSize: 10);
        button.AutoSize = true;
        button.Padding = new Padding(5, 1, 5, 1);
        button.Font = new Font("Segoe UI", 8.1f);
        return button;
    }

    /// <summary>
    /// Маленькая СИНЯЯ иконка «i» вместо огромной кнопки.
    ///
    /// Требование автора: иконка информации должна быть иконкой, а не кнопкой во
    /// всю высоту строки. Синий — цвет справки в этом интерфейсе.
    ///
    /// Высота равна высоте ТЕКСТА строки (<see cref=«RowContentHeight»>), а не
    /// стороне значка: иначе якорь Left центрирует квадратик 16 px в области
    /// 40 px, и он оказывается на 8 px ниже названия. Проверено пробой раскладки:
    /// она сообщила расхождение центров именно на 8 px. Высота в строку текста
    /// делает выравнивание арифметическим, а не подбором отступа.
    /// </summary>
    private static Label CreateInfoIcon(string tooltip)
    {
        var icon = new Label
        {
            AutoSize = false,
            Size = new Size(InfoIconSize, RowContentHeight),
            Text = "i",
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(18, 171, 229),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent
        };

        var tooltipComponent = new ToolTip();
        tooltipComponent.SetToolTip(icon, tooltip);
        // Подсказка живёт вместе с иконкой: без явного Dispose она утекла бы на
        // каждой перестройке дерева, а перестроек в этом окне было много.
        icon.Disposed += (_, _) => tooltipComponent.Dispose();

        icon.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Квадрат рисуется по центру области: сама область выше значка, чтобы
            // совпадать по вертикали с названием.
            var top = (icon.Height - InfoIconSize) / 2;
            var bounds = new Rectangle(0, top, InfoIconSize - 1, InfoIconSize - 1);
            using var path = RoundedPath(bounds, 4);
            using var pen = new Pen(Color.FromArgb(18, 171, 229));
            e.Graphics.DrawPath(pen, path);
        };

        return icon;
    }

    /// <summary>Сторона квадрата иконки «i».</summary>
    private const int InfoIconSize = 14;

    /// <summary>
    /// Высота содержимого строки заголовка (названия и иконки).
    ///
    /// Общая константа для названия и иконки: если задать их разными числами,
    /// выравнивание по вертикали снова станет подбором, и пробная проверка
    /// покажет расхождение.
    /// </summary>
    private const int RowContentHeight = 20;

    /// <summary>Путь со скруглёнными углами для рисования кнопок и иконок.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
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

        var block = new Panel
        {
            BackColor = RowBackColor,
            Margin = new Padding(TreeLevelIndent, 0, 0, 4)
        };

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = CampaignHeaderHeight,
            ColumnCount = 8,
            Padding = new Padding(4, 2, 4, 2),
            BackColor = RowBackColor,
            Cursor = Cursors.Hand
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var marker = new Label
        {
            AutoSize = false,
            Width = 12,
            Height = RowContentHeight,
            Text = collapsed ? "▸" : "▾",
            ForeColor = Color.FromArgb(200, 208, 216),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 3, 0),
            Cursor = Cursors.Hand
        };

        var active = CreateTreeCheckBox(
            campaign.Active,
            checkedValue => CampaignActiveChanged?.Invoke(
                this,
                new CampaignActiveChangedEventArgs(campaign.Id, checkedValue)));
        active.Margin = new Padding(0, 0, 5, 0);

        var text = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Text = campaign.Name,
            ForeColor = Color.FromArgb(238, 243, 248),
            Font = new Font("Segoe UI", 8.8f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 4, 0)
        };

        var info = CreateInfoIcon("Свойства кампании");
        info.Margin = new Padding(2, 0, 5, 0);
        info.Click += (_, _) =>
            CampaignPropertiesRequested?.Invoke(
                this,
                new CampaignPropertiesRequestedEventArgs(campaign.Id));

        var state = new Label
        {
            AutoSize = true,
            Text = campaign.Active
                ? $"Активна · {activeCount}/{campaign.Quests.Count}"
                : "Отключена",
            ForeColor = campaign.Active
                ? Color.FromArgb(139, 216, 255)
                : Color.FromArgb(135, 145, 157),
            Font = new Font("Segoe UI", 7.8f),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 6, 0)
        };

        var folder = CreateMicroButton("Папка");
        folder.Click += (_, _) =>
            CampaignFolderOpenRequested?.Invoke(
                this,
                new CampaignFolderOpenRequestedEventArgs(campaign.Id));

        var export = CreateMicroButton("Экспорт");
        export.Click += (_, _) =>
            CampaignExportRequested?.Invoke(
                this,
                new CampaignExportRequestedEventArgs(campaign.Id));

        header.Controls.Add(marker, 0, 0);
        header.Controls.Add(active, 1, 0);
        header.Controls.Add(text, 2, 0);
        header.Controls.Add(info, 3, 0);
        header.Controls.Add(state, 5, 0);
        header.Controls.Add(folder, 6, 0);
        header.Controls.Add(export, 7, 0);

        foreach (Control element in new Control[] { header, text, marker })
            element.Click += (_, _) => ToggleCollapsed(campaign.Id);

        block.Controls.Add(header);

        var foreignWarning = ResourceParentRules.Describe(
            campaign.ParentWorldId,
            _world?.Definition.Id);

        if (foreignWarning is not null)
        {
            var warning = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                AutoSize = false,
                AutoEllipsis = true,
                Padding = new Padding(5, 2, 5, 2),
                Text = "⚠ " + foreignWarning,
                ForeColor = AccentColor,
                Font = new Font("Segoe UI", 7.8f)
            };

            block.Controls.Add(warning);
            warning.BringToFront();
        }

        var rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = RowBackColor,
            Padding = new Padding(0)
        };

        if (!collapsed)
        {
            AddSignature(rows, WorldDisplayRules.DescribeStamp(campaign.Metadata, modified: false));
            AddSignature(rows, WorldDisplayRules.DescribeStamp(campaign.Metadata));

            foreach (var quest in campaign.Quests)
                rows.Controls.Add(CreateQuestRow(campaign, quest));

            if (campaign.Quests.Count == 0)
                rows.Controls.Add(CreateNotice("В кампании нет доступных файлов Quest."));
        }

        block.Controls.Add(rows);
        rows.BringToFront();

        var warningHeight = foreignWarning is null ? 0 : 24;
        block.Height = collapsed
            ? CampaignHeaderHeight
            : CampaignHeaderHeight +
              warningHeight +
              SignatureRowHeight * 2 +
              Math.Max(1, campaign.Quests.Count) * QuestRowMinHeight + 6;

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
        var selected =
            quest.QuestId.Equals(_selectedQuestId, StringComparison.OrdinalIgnoreCase) &&
            campaign.Id.Equals(_selectedCampaignId, StringComparison.OrdinalIgnoreCase);

        var enabled = quest.Status == CampaignQuestStatus.Enabled;

        var row = new TableLayoutPanel
        {
            Height = QuestRowMinHeight,
            ColumnCount = 5,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
            Padding = new Padding(4, 1, 4, 1),
            BackColor = selected ? RowSelectedBackColor : RowBackColor,
            Margin = new Padding(QuestRowIndent, 0, 0, 2),
            Cursor = Cursors.Hand,
            AutoSize = false
        };

        if (selected)
        {
            row.Paint += (_, e) =>
            {
                using var brush = new SolidBrush(AccentColor);
                e.Graphics.FillRectangle(
                    brush,
                    0,
                    2,
                    2,
                    Math.Max(0, row.Height - 4));
            };
        }

        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var check = CreateTreeCheckBox(
            enabled,
            checkedValue => QuestEnabledChanged?.Invoke(
                this,
                new QuestEnabledChangedEventArgs(
                    campaign.Id,
                    quest.QuestId,
                    checkedValue)));
        check.Margin = new Padding(0, 0, 5, 0);

        var activation = quest.Activation;
        var details = activation is null
            ? $"#{quest.Order} · v{quest.Version}"
            : activation.Mode == QuestStartMode.Proximity
                ? $"#{quest.Order} · v{quest.Version} · Радиус {activation.Radius:0} м"
                : $"#{quest.Order} · v{quest.Version} · {activation.Mode}";

        var name = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Text = $"{quest.Title}  ·  {details}",
            ForeColor = selected
                ? Color.White
                : enabled
                    ? Color.FromArgb(231, 237, 244)
                    : Color.FromArgb(178, 184, 192),
            Font = new Font(
                "Segoe UI",
                8.2f,
                selected
                    ? FontStyle.Bold
                    : enabled
                        ? FontStyle.Regular
                        : FontStyle.Italic),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            UseMnemonic = false
        };

        var status = new Label
        {
            AutoSize = true,
            Text = enabled ? "Включён" : "Отключён",
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = enabled
                ? Color.FromArgb(139, 216, 255)
                : Color.FromArgb(165, 172, 182),
            Font = new Font("Segoe UI", 7.5f, enabled ? FontStyle.Regular : FontStyle.Italic),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 0, 6, 0)
        };

        var edit = CreateMicroButton("Ред.");
        edit.Click += (_, _) =>
            QuestOpenRequested?.Invoke(
                this,
                new QuestOpenRequestedEventArgs(quest.FullPath));

        var export = CreateMicroButton("Экспорт");
        export.Click += (_, _) =>
            QuestExportRequested?.Invoke(
                this,
                new QuestExportRequestedEventArgs(
                    campaign.Id,
                    quest.QuestId,
                    quest.FullPath));

        row.Controls.Add(check, 0, 0);
        row.Controls.Add(name, 1, 0);
        row.Controls.Add(status, 2, 0);
        row.Controls.Add(edit, 3, 0);
        row.Controls.Add(export, 4, 0);

        foreach (Control element in new Control[] { row, name, status })
        {
            element.Click += (_, _) =>
                QuestSelected?.Invoke(
                    this,
                    new QuestSelectedEventArgs(campaign.Id, quest.QuestId));
        }

        return row;
    }

    private static Control CreateTreeCheckBox(
        bool isChecked,
        Action<bool> changed)
    {
        var value = isChecked;

        var box = new Panel
        {
            Size = new Size(16, 16),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            AccessibleRole = AccessibleRole.CheckButton,
            TabStop = false
        };

        box.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bounds = new Rectangle(1, 1, 14, 14);

            using var fill = new SolidBrush(
                value ? AccentColor : Color.Transparent);
            using var border = new Pen(ButtonBorderColor, 1f);

            e.Graphics.FillRectangle(fill, bounds);
            e.Graphics.DrawRectangle(border, bounds);

            if (!value)
                return;

            using var pen = new Pen(Color.FromArgb(20, 22, 25), 1.6f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            e.Graphics.DrawLines(
                pen,
                new[]
                {
                    new Point(4, 8),
                    new Point(7, 11),
                    new Point(12, 5)
                });
        };

        box.Click += (_, _) =>
        {
            value = !value;
            box.Invalidate();
            changed(value);
        };

        return box;
    }

    private static Label CreateNotice(string message) => new() { AutoSize = true, Text = message, ForeColor = Color.FromArgb(165, 175, 185), Font = new Font("Segoe UI", 8.5f), Padding = new Padding(4, 6, 4, 6), Margin = new Padding(0, 0, 0, 8) };
    private void ResizeBlocks()
    {
        // Ширина уменьшается на ширину полосы прокрутки: она теперь ОДНА на всё
        // дерево, и без этого вычета строки уезжали бы под полосу, а текст
        // обрезался бы по правому краю.
        var width = Math.Max(360, _list.ClientSize.Width - _list.Padding.Horizontal
            - SystemInformation.VerticalScrollBarWidth - 2);

        // Обход только ВЕРХНЕГО уровня: вложенные кампании получают ширину от
        // пункта мира, а не от списка — иначе они вылезали бы за его рамку.
        foreach (Control block in _list.Controls)
        {
            block.Width = width;
            if (block is not Panel panel)
                continue;

            var rows = panel.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            var header = panel.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (rows is null || header is null)
                continue;

            var innerWidth = Math.Max(280, width - panel.Padding.Horizontal - 4);
            var total = LayoutPanelChildren(rows, innerWidth, QuestRowIndent);

            // Высота пункта считается по фактическим высотам содержимого, а не по
            // формуле «строк × константа»: названия переносятся по словам, и
            // фиксированная высота срезала бы длинные.
            panel.Height = Math.Max(CampaignHeaderHeight + 4, header.Height + total + 4);
        }
    }

    /// <summary>
    /// Раскладывает строки контейнера и возвращает суммарную высоту.
    ///
    /// Общая функция для двух уровней дерева: пункт мира и плашка кампании
    /// отличаются только величиной отступа и набором строк. Две копии этого
    /// цикла разошлись бы — как уже было с формулами высоты, из-за чего состав
    /// кампании уезжал под нижний край.
    /// </summary>
    /// <param name="indent">Отступ слева: показывает вложенность уровня.</param>
    private static int LayoutPanelChildren(FlowLayoutPanel rows, int availableWidth, int indent)
    {
        var rowWidth = Math.Max(200, availableWidth - indent);
        var total = 0;

        foreach (Control row in rows.Controls)
        {
            // Вложенная плашка кампании: ширина та же (она — элемент дерева), но
            // отступ задаётся её собственным Margin, а высота считается внутри
            // её же вызова LayoutPanelChildren.
            if (row is Panel nested)
            {
                nested.Width = rowWidth;

                var nestedRows = nested.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
                var nestedHeader = nested.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
                if (nestedRows is not null && nestedHeader is not null)
                {
                    var nestedTotal = LayoutPanelChildren(
                        nestedRows,
                        rowWidth - nested.Padding.Horizontal,
                        QuestRowIndent);
                    nested.Height = Math.Max(CampaignHeaderHeight + 4,
                        nestedHeader.Height + nestedTotal + 4);
                }

                total += nested.Height + nested.Margin.Vertical;
                continue;
            }

            row.Width = rowWidth;

            // Отступ вложенности и замер высоты относятся ТОЛЬКО к строкам
            // квестов. Подписи дат и уведомления — не строки квеста: им задавали
            // бы отступ QuestRowIndent и высоту QuestRowMinHeight, то есть
            // подпись в 18 пикселей занимала бы 26 и сдвигала состав.
            if (row is not TableLayoutPanel)
            {
                total += row.Height + row.Margin.Vertical;
                continue;
            }

            row.Margin = new Padding(indent, 0, 0, 3);
            // Высота задаётся измеренной, а не константой: заголовок
            // переносится по словам, и фиксированная высота срезала
            // длинные названия квестов.
            row.Height = MeasureRowHeight(row);
            total += row.Height + row.Margin.Vertical;
        }

        return total;
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