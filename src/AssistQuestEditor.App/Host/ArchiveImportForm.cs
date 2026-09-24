using System.IO.Compression;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Диалог импорта архива `.aqezip`.
///
/// Открывается по двойному клику на файле и показывает содержимое ДО распаковки:
/// имя, полное имя, автора с датой, описание и состав архива. Согласие на
/// импорт, данное до просмотра, — это согласие вслепую, а импорт может
/// перезаписать работу автора.
///
/// Один диалог на все виды содержимого (мир, кампания, квест), потому что
/// манифест у них общий. Отдельные диалоги разошлись бы в мелочах — например,
/// один показывал бы автора, а другой нет.
/// </summary>
public sealed class ArchiveImportForm : Form
{
    private readonly ArchiveInspection _inspection;
    private readonly CheckBox _overwrite;
    private readonly Label _conflict;
    private readonly PictureBox _imagePreview;

    public ArchiveImportForm(ArchiveInspection inspection)
    {
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));

        var manifest = inspection.Manifest;
        var kindLabel = KindName(manifest.Kind);
        var displayName = string.IsNullOrWhiteSpace(manifest.FullName) ? manifest.Name : manifest.FullName!;

        Text = "Импорт архива Assist Quest";
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(700, 620);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(18, 14, 18, 4),
            Text = "Импорт " + kindLabel + " из архива",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold)
        };

        var file = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(18, 0, 18, 4),
            Text = "Файл: " + Path.GetFileName(inspection.ArchivePath),
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f)
        };

        var info = new Label
        {
            AutoSize = false,
            Location = new Point(18, 96),
            Size = new Size(505, 130),
            Text = BuildDescription(manifest, kindLabel, displayName, inspection),
            Font = new Font("Segoe UI", 9.2f)
        };

        var imageFrame = new Panel
        {
            Location = new Point(540, 96),
            Size = new Size(128, 128),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(16, 18, 22)
        };

        _imagePreview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(16, 18, 22)
        };
        imageFrame.Controls.Add(_imagePreview);

        var contentsHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 232),
            Size = new Size(650, 22),
            Text = $"Содержимое архива ({inspection.FileCount} файлов, {FormatBytes(inspection.TotalBytes)})",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var contents = new ListBox
        {
            Location = new Point(18, 256),
            Size = new Size(650, 200),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.8f),
            HorizontalScrollbar = true
        };

        foreach (var entry in inspection.Entries)
            contents.Items.Add(entry.Path + "   (" + FormatBytes(entry.Bytes) + ")");

        if (contents.Items.Count == 0)
            contents.Items.Add("(файлов нет)");

        // Галочка перезаписи НЕ отмечена по умолчанию: безопасное поведение —
        // распаковать рядом. Отмеченная по умолчанию галочка означала бы, что
        // невнимательное нажатие затирает существующий мир.
        _overwrite = new CheckBox
        {
            AutoSize = false,
            Location = new Point(18, 468),
            Size = new Size(650, 24),
            Text = "Перезаписать существующий ресурс с таким же именем",
            Checked = false,
            ForeColor = Color.FromArgb(220, 228, 236),
            Font = new Font("Segoe UI", 9f)
        };

        _conflict = new Label
        {
            AutoSize = false,
            Location = new Point(18, 492),
            Size = new Size(650, 40),
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 8.6f)
        };

        _overwrite.CheckedChanged += (_, _) => UpdateConflictText();

        var import = new DarkFlatButton
        {
            Location = new Point(18, 538),
            Size = new Size(180, 38),
            Text = "Импортировать",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        import.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);

        var cancel = new DarkFlatButton
        {
            Location = new Point(560, 538),
            Size = new Size(108, 38),
            Text = "Отмена",
            DialogResult = DialogResult.Cancel
        };

        // Импорт запускает вызывающий код: диалог не должен знать о файловой
        // системе и сторах, иначе его нельзя проверить без диска и мира.
        import.Click += (_, _) =>
        {
            if (OverwriteRequested && !_overwriteConfirms)
            {
                // Второй шаг подтверждения: перезапись уничтожает работу, и
                // одного нажатия для неё мало.
                MessageBox.Show(this,
                    "Существующий ресурс будет удалён и заменён содержимым архива. " +
                    "Это действие нельзя отменить.",
                    "Подтвердите перезапись", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _overwriteConfirms = true;
                UpdateConflictText();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.Add(import);
        Controls.Add(cancel);
        Controls.Add(_conflict);
        Controls.Add(_overwrite);
        Controls.Add(contents);
        Controls.Add(contentsHeader);
        Controls.Add(imageFrame);
        Controls.Add(info);
        Controls.Add(file);
        Controls.Add(title);

        AcceptButton = import;
        CancelButton = cancel;

        UpdateConflictText();
        RefreshImagePreview();
    }

    /// <summary>
    /// Импортировать ли с перезаписью. По умолчанию — нет.
    /// </summary>
    public bool OverwriteRequested => _overwrite.Checked;

    private bool _overwriteConfirms;

    private void RefreshImagePreview()
    {
        _imagePreview.Image?.Dispose();
        _imagePreview.Image = null;

        try
        {
            using var archive = ZipFile.OpenRead(_inspection.ArchivePath);
            var preferred = _inspection.Manifest.Kind switch
            {
                WorldArchiveKinds.World => new[] { "world.png" },
                WorldArchiveKinds.Campaign => new[] { "campaign.png" },
                _ => Array.Empty<string>()
            };

            var entry = preferred
                .Select(name => archive.Entries.FirstOrDefault(item =>
                    item.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(item => item is not null);

            entry ??= archive.Entries.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Name) &&
                IsImageExtension(Path.GetExtension(item.Name)));

            if (entry is null)
                return;

            using var stream = entry.Open();
            using var source = Image.FromStream(stream);
            _imagePreview.Image = new Bitmap(source);
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "ArchiveImportForm: не удалось показать изображение из архива.",
                _inspection.ArchivePath + ": " + ex.Message);
        }
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static string KindName(string kind) => kind.ToLowerInvariant() switch
    {
        WorldArchiveKinds.World => "мира",
        WorldArchiveKinds.Campaign => "кампании",
        WorldArchiveKinds.Quest => "квеста",
        _ => "ресурса"
    };

    /// <summary>
    /// Описание содержимого. Собирается текстом, а не списком полей, потому что
    /// отсутствующие значения (нет автора, нет описания) не должны оставлять
    /// пустых строк: «Автор: » читается как ошибка чтения.
    /// </summary>
    private static string BuildDescription(
        WorldArchiveManifest manifest,
        string kindLabel,
        string displayName,
        ArchiveInspection inspection)
    {
        var lines = new List<string> { "Название: " + displayName };

        if (!string.IsNullOrWhiteSpace(manifest.FullName) &&
            !string.Equals(manifest.FullName, manifest.Name, StringComparison.Ordinal))
        {
            lines.Add("Короткое имя: " + manifest.Name);
        }

        lines.Add("Id: " + manifest.Id + "   ·   версия: " + manifest.Version);

        if (manifest.Metadata is not null)
        {
            var created = WorldDisplayRules.Describe(manifest.Metadata, modified: false);
            if (created is not null)
                lines.Add(created);

            var modified = WorldDisplayRules.Describe(manifest.Metadata);
            if (modified is not null && modified != created)
                lines.Add(modified);
        }
        else
        {
            // Отсутствие подписи — факт, о котором надо сказать: без него
            // непонятно, кто автор архива.
            lines.Add("Автор не указан.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.ParentWorldId))
            lines.Add("Родительский мир: " + manifest.ParentWorldId);

        if (!string.IsNullOrWhiteSpace(manifest.ParentCampaignId))
            lines.Add("Родительская кампания: " + manifest.ParentCampaignId);

        lines.Add(manifest.IncludesDependencies
            ? "Зависимости внутри: архив самодостаточен."
            : "Зависимости НЕ внутри: ресурс может потребовать файлы, которых у вас нет.");
        lines.Add("Размер архива: " + FormatBytes(inspection.ArchiveBytes));

        if (!string.IsNullOrWhiteSpace(manifest.Description))
            lines.Add("Описание: " + manifest.Description);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Поясняет последствие выбранного варианта.
    ///
    /// Формулировка меняется вместе с галочкой, и это не украшение: одно и то же
    /// действие («импорт») даёт разные последствия, и пользователь должен узнать
    /// об этом ДО нажатия.
    /// </summary>
    private void UpdateConflictText()
    {
        if (OverwriteRequested)
        {
            _conflict.ForeColor = Color.FromArgb(207, 12, 12);
            _conflict.Text = _overwriteConfirms
                ? "Существующий ресурс будет заменён. Нажмите «Импортировать» ещё раз для подтверждения."
                : "Существующий ресурс будет УДАЛЁН. Потребуется подтверждение.";
            return;
        }

        _conflict.ForeColor = Color.FromArgb(150, 200, 235);
        _conflict.Text = "Если ресурс с таким именем уже есть, он будет распакован рядом " +
                         "под новым именем — существующий останется нетронутым.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024L)
            return bytes + " Б";

        if (bytes < 1024L * 1024L)
            return (bytes / 1024d).ToString("0.0") + " КБ";

        return (bytes / (1024d * 1024d)).ToString("0.0") + " МБ";
    }
}
