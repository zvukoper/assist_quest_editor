using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Создание нового ресурса: мира или кампании.
///
/// Одно окно на оба вида, потому что запрашиваемые данные одинаковые (короткое
/// имя, полное имя, описание). Отдельные окна неизбежно разошлись бы в
/// правилах — например, одно проверяло бы имя, а второе нет.
///
/// Имя проверяется ЗДЕСЬ, до записи: по короткому имени строится ПАПКА ресурса,
/// и недопустимое имя либо упало бы на файловой операции, либо создало ресурс,
/// которого потом не найти. Проверка показывается словами, а не исключением.
/// </summary>
public sealed class ResourceCreateForm : Form
{
    private readonly TextBox _name;
    private readonly TextBox _fullName;
    private readonly TextBox _description;
    private readonly Label _validation;
    private readonly Label _folderHint;
    private readonly string _parentFolder;

    /// <param name="kindLabel">«мира» или «кампании» — для заголовка и подсказок.</param>
    /// <param name="parentFolder">Куда ляжет ресурс: показывается, чтобы автор видел адрес.</param>
    /// <param name="suggestedName">
    /// Подставленное имя. Для кампании — «Common», занятое имя которого надо
    /// заметить ДО нажатия, а не узнать из ошибки.
    /// </param>
    public ResourceCreateForm(string kindLabel, string parentFolder, string? suggestedName = null)
    {
        _parentFolder = parentFolder;

        Text = "Новый " + kindLabel;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(560, 400);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(18, 14, 18, 4),
            Text = "Новый " + kindLabel,
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold)
        };

        var nameLabel = new Label
        {
            Location = new Point(18, 58),
            Size = new Size(510, 18),
            Text = "Короткое имя (по нему строится папка)",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.4f)
        };

        _name = new TextBox
        {
            Location = new Point(18, 78),
            Size = new Size(510, 24),
            Text = suggestedName ?? string.Empty,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle
        };
        // Подсказка обновляется при вводе: автор должен видеть будущее имя
        // папки до нажатия, а не после создания ресурса.
        _name.TextChanged += (_, _) => UpdateFolderHint();

        var fullLabel = new Label
        {
            Location = new Point(18, 110),
            Size = new Size(510, 18),
            Text = "Полное имя (показывается в списках; можно пусто)",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.4f)
        };

        _fullName = new TextBox
        {
            Location = new Point(18, 130),
            Size = new Size(510, 24),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle
        };

        var descriptionLabel = new Label
        {
            Location = new Point(18, 162),
            Size = new Size(510, 18),
            Text = "Описание",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.4f)
        };

        _description = new TextBox
        {
            Location = new Point(18, 182),
            Size = new Size(510, 66),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle
        };

        _folderHint = new Label
        {
            AutoSize = false,
            Location = new Point(18, 254),
            Size = new Size(510, 34),
            ForeColor = Color.FromArgb(139, 216, 255),
            Font = new Font("Consolas", 8.4f)
        };

        _validation = new Label
        {
            AutoSize = false,
            Location = new Point(18, 288),
            Size = new Size(510, 34),
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 8.6f)
        };

        var create = new Button
        {
            Location = new Point(18, 326),
            Size = new Size(150, 38),
            Text = "Создать",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            DialogResult = DialogResult.OK
        };
        create.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);

        var cancel = new Button
        {
            Location = new Point(434, 326),
            Size = new Size(94, 38),
            Text = "Отмена",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            DialogResult = DialogResult.Cancel
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(60, 66, 74);

        // Проверка в FormClosing, а не в Click: кнопка с DialogResult закрывает
        // окно сама, и сообщить о недопустимом имени было бы уже негде.
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK)
                return;

            if (NameValue.Length == 0)
            {
                e.Cancel = true;
                _validation.Text = "Короткое имя не может быть пустым: по нему строится папка.";
                _name.Focus();
                return;
            }

            if (!ResourceNaming.IsValidName(NameValue))
            {
                e.Cancel = true;
                _validation.Text =
                    "Недопустимое короткое имя. Разрешены буквы, цифры, пробел и подчёркивание.";
                _name.Focus();
                return;
            }

            if (FullNameValue.Length > 0 && !ResourceNaming.IsValidName(FullNameValue))
            {
                e.Cancel = true;
                _validation.Text =
                    "Недопустимое полное имя. Разрешены буквы, цифры, пробел и подчёркивание.";
                _fullName.Focus();
                return;
            }

            // Занятое имя проверяется ДО записи: иначе создание падало бы
            // исключением стора, и это выглядело бы как поломка окна.
            if (Directory.Exists(Path.Combine(_parentFolder,
                    ResourceNaming.ToFolderName(NameValue))))
            {
                e.Cancel = true;
                _validation.Text = "Ресурс с таким именем уже существует. Выберите другое имя.";
                _name.Focus();
                return;
            }

            _validation.Text = string.Empty;
        };

        Controls.Add(create);
        Controls.Add(cancel);
        Controls.Add(_validation);
        Controls.Add(_folderHint);
        Controls.Add(_description);
        Controls.Add(descriptionLabel);
        Controls.Add(_fullName);
        Controls.Add(fullLabel);
        Controls.Add(_name);
        Controls.Add(nameLabel);
        Controls.Add(title);

        AcceptButton = create;
        CancelButton = cancel;

        UpdateFolderHint();
    }

    /// <summary>Короткое имя ресурса.</summary>
    public string NameValue => _name.Text.Trim();

    /// <summary>Полное имя: пусто означает «показывать короткое».</summary>
    public string FullNameValue => _fullName.Text.Trim();

    /// <summary>Описание ресурса.</summary>
    public string DescriptionValue => _description.Text.Trim();

    private void UpdateFolderHint()
    {
        var leaf = NameValue.Length == 0
            ? "(имя не задано)"
            : ResourceNaming.ToFolderName(NameValue);

        _folderHint.Text = "Папка: " + Path.Combine(_parentFolder, leaf);
    }
}
