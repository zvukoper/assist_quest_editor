using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Выбор родителя при импорте кампании или квеста.
///
/// Родитель — это АДРЕС ресурса, а не украшение: файл кампании лежит внутри
/// папки мира, файл квеста — внутри папки кампании. Поэтому выбор обязателен и
/// показан явно: положить ресурс в посторонний мир нельзя «по умолчанию» —
/// автор увидит его не там, где искал, и решит, что импорт не сработал.
///
/// Мир из манифеста предлагается ОТМЕЧЕННЫМ, но не применяется молча: это
/// рекомендация, а решение остаётся за пользователем. Если такого мира нет,
/// об этом говорится словами, а не выбором первого попавшегося.
/// </summary>
public sealed class ImportParentForm : Form
{
    private readonly ListBox _worlds;
    private readonly ListBox _campaigns;
    private readonly Label _hint;

    private readonly List<WorldRecord> _worldList;
    private readonly List<CampaignStore.CampaignRecord> _campaignList = new();

    /// <param name="kindLabel">«кампании» или «квеста» — для заголовка.</param>
    /// <param name="manifest">Манифест архива: из него берётся рекомендуемый родитель.</param>
    /// <param name="worlds">Миры, доступные для размещения.</param>
    /// <param name="defaultWorldId">Мир, открытый сейчас: предлагается, если манифест молчит.</param>
    public ImportParentForm(
        string kindLabel,
        WorldArchiveManifest manifest,
        IReadOnlyList<WorldRecord> worlds,
        string? defaultWorldId,
        Func<string, IReadOnlyList<CampaignStore.CampaignRecord>> campaignsForWorld)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(campaignsForWorld);

        _worldList = worlds.ToList();
        var needsCampaign = kindLabel.Equals("квеста", StringComparison.OrdinalIgnoreCase);

        Text = "Куда импортировать " + kindLabel;
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(620, needsCampaign ? 500 : 380);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(18, 14, 18, 4),
            Text = "Импорт " + kindLabel + " — выберите родителя",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold)
        };

        var worldLabel = new Label
        {
            Location = new Point(18, 56),
            Size = new Size(560, 20),
            Text = "Мир",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f, FontStyle.Bold)
        };

        _worlds = new ListBox
        {
            Location = new Point(18, 78),
            Size = new Size(560, 140),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };

        foreach (var world in _worldList)
        {
            var suffix = world.Definition.Id.Equals(manifest.ParentWorldId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
                ? "   ← указан в архиве"
                : string.Empty;

            _worlds.Items.Add(world.DisplayName + "   [" + world.Definition.Id + "]" + suffix);
        }

        _campaigns = new ListBox
        {
            Visible = needsCampaign,
            Location = new Point(18, 244),
            Size = new Size(560, 130),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };

        var campaignLabel = new Label
        {
            Visible = needsCampaign,
            Location = new Point(18, 222),
            Size = new Size(560, 20),
            Text = "Кампания",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f, FontStyle.Bold)
        };

        _hint = new Label
        {
            AutoSize = false,
            Location = new Point(18, needsCampaign ? 378 : 226),
            Size = new Size(560, 40),
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 8.6f)
        };

        // Приоритет выбора: рекомендация из архива, затем открытый мир, затем
        // единственный доступный. Иначе первый в списке — и ресурс уедет не туда.
        var preferred = _worldList.FindIndex(world =>
            world.Definition.Id.Equals(manifest.ParentWorldId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
        if (preferred < 0)
        {
            preferred = _worldList.FindIndex(world =>
                world.Definition.Id.Equals(defaultWorldId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
        }
        if (preferred < 0 && _worldList.Count == 1)
            preferred = 0;

        if (preferred >= 0)
            _worlds.SelectedIndex = preferred;

        _worlds.SelectedIndexChanged += (_, _) => ReloadCampaigns(campaignsForWorld);

        var accept = new DarkFlatButton
        {
            Location = new Point(18, needsCampaign ? 424 : 272),
            Size = new Size(170, 38),
            Text = "Импортировать",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            DialogResult = DialogResult.OK
        };
        accept.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        // Проверка не в Click: кнопка с DialogResult закрывает окно сама, и
        // сообщить о неполном выборе было бы уже негде.
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK)
                return;

            if (SelectedWorld is null)
            {
                e.Cancel = true;
                _hint.Text = "Выберите мир: без родителя у ресурса нет адреса.";
                return;
            }

            if (needsCampaign && SelectedCampaign is null)
            {
                e.Cancel = true;
                _hint.Text = "Выберите кампанию: квест лежит внутри кампании.";
                return;
            }

            _hint.Text = string.Empty;
        };

        var cancel = new DarkFlatButton
        {
            Location = new Point(494, needsCampaign ? 424 : 272),
            Size = new Size(84, 38),
            Text = "Отмена",
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(accept);
        Controls.Add(cancel);
        Controls.Add(_hint);
        Controls.Add(_campaigns);
        Controls.Add(campaignLabel);
        Controls.Add(_worlds);
        Controls.Add(worldLabel);
        Controls.Add(title);

        AcceptButton = accept;
        CancelButton = cancel;

        ReloadCampaigns(campaignsForWorld);
    }

    /// <summary>Выбранный мир-родитель.</summary>
    public WorldRecord? SelectedWorld =>
        _worlds.SelectedIndex >= 0 && _worlds.SelectedIndex < _worldList.Count
            ? _worldList[_worlds.SelectedIndex]
            : null;

    /// <summary>Выбранная кампания-родитель (только для квеста).</summary>
    public CampaignStore.CampaignRecord? SelectedCampaign =>
        _campaigns.Visible && _campaigns.SelectedIndex >= 0 &&
        _campaigns.SelectedIndex < _campaignList.Count
            ? _campaignList[_campaigns.SelectedIndex]
            : null;

    /// <summary>
    /// Перечитывает кампании выбранного мира.
    ///
    /// Список строится при каждой смене мира, а не один раз: у разных миров
    /// разные кампании, и список от прежнего мира отправил бы квест в чужую.
    /// </summary>
    private void ReloadCampaigns(Func<string, IReadOnlyList<CampaignStore.CampaignRecord>> source)
    {
        _campaignList.Clear();
        _campaigns.Items.Clear();

        var world = SelectedWorld;
        if (world is null)
        {
            _hint.Text = "Выберите мир: без родителя у ресурса нет адреса.";
            return;
        }

        _campaignList.AddRange(source(world.FolderPath));

        foreach (var campaign in _campaignList)
        {
            var name = WorldDisplayRules.DisplayName(campaign.Definition.Name,
                campaign.Definition.FullName);
            var quests = campaign.Definition.Quests.Count;
            _campaigns.Items.Add(name + "   [" + campaign.Definition.Id + " · квестов: " + quests + "]");
        }

        if (_campaignList.Count > 0)
            _campaigns.SelectedIndex = 0;

        _hint.Text = _campaignList.Count == 0 && _campaigns.Visible
            ? "В этом мире нет кампаний: квест некуда положить."
            : string.Empty;
    }
}
