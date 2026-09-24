using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно свойств ресурса (мир или кампания): изображение, полное имя, описание —
/// плюс необязательная панель «Сведения».
///
/// Одно окно на оба режима намеренно. «Ред.» и «ℹ️» спрашивают про ОДНО и то же
/// содержимое, и разные формы неизбежно показывали бы его по-разному: правя
/// описание, автор не видел бы автора и дату, по которым решается перезапись при
/// импорте.
///
/// Диалог НЕ пишет на диск: он возвращает собранные значения, а запись делает
/// вызывающий код. Иначе окно невозможно проверить без файловой системы, а
/// «сохранилось ли» пришлось бы выяснять по содержимому папки.
/// </summary>
public sealed class ResourcePropertiesForm : Form
{
    private readonly string _targetFolder;
    private readonly PictureBox _preview;
    private readonly Label _imageHint;
    private readonly TextBox _fullName;
    private readonly TextBox _shortName;
    private readonly TextBox _description;
    private readonly Label _validation;
    private readonly bool _editEnabled;

    private string? _pickedImagePath;

    /// <param name="properties">Что показывать и что менять.</param>
    /// <param name="editMode">true — «Ред.» (поля доступны), false — «Сведения».</param>
    public ResourcePropertiesForm(ResourceProperties properties, bool editMode)
    {
        Properties = properties ?? throw new ArgumentNullException(nameof(properties));
        _targetFolder = properties.FolderPath;
        _editEnabled = editMode;

        var kind = properties.KindLabel;

        Text = (editMode ? "Редактирование " : "Сведения о ") + kind + " — " + properties.DisplayName;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(880, 640);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(18, 14, 18, 4),
            Text = (editMode ? "Свойства " : "Сведения о ") + kind,
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold)
        };

        // Слева — изображение, справа — поля. Порядок не косметический: картинку
        // узнают быстрее, чем читают имя, и она служит опознавательным знаком
        // среди похожих миров.
        var left = new Panel
        {
            Location = new Point(18, 62),
            Size = new Size(280, 380),
            BackColor = Color.FromArgb(16, 18, 22),
            BorderStyle = BorderStyle.FixedSingle
        };

        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(16, 18, 22)
        };

        _imageHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };

        // ЛКМ по картинке открывает файл во внешнем просмотрщике: окно Zoom
        // показывает её мелко, а разглядывать эскизы приходится часто.
        _preview.Cursor = Cursors.Hand;
        _preview.Click += (_, _) => OpenImageExternally();

        left.Controls.Add(_preview);
        left.Controls.Add(_imageHint);

        var right = new Panel
        {
            Location = new Point(310, 62),
            Size = new Size(540, 380),
            BackColor = Color.FromArgb(13, 16, 20)
        };

        var info = new Label
        {
            Location = new Point(12, 10),
            Size = new Size(516, 190),
            Text = string.Join(Environment.NewLine,
                ResourcePropertiesService.BuildInfoLines(properties)),
            ForeColor = Color.FromArgb(214, 222, 232),
            Font = new Font("Segoe UI", 8.8f)
        };

        var shortLabel = new Label
        {
            Location = new Point(12, 208),
            Size = new Size(516, 18),
            Text = "Короткое имя (для папки и ссылок)",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.4f)
        };

        _shortName = new TextBox
        {
            Location = new Point(12, 228),
            Size = new Size(516, 24),
            Text = properties.ShortName,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle,
            Enabled = editMode
        };

        var fullLabel = new Label
        {
            Location = new Point(12, 258),
            Size = new Size(516, 18),
            Text = "Полное имя (показывается в списках)",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.4f)
        };

        _fullName = new TextBox
        {
            Location = new Point(12, 278),
            Size = new Size(516, 24),
            Text = properties.DisplayName == WorldDisplayRules.UnnamedLabel
                ? string.Empty
                : properties.DisplayName,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle,
            Enabled = editMode
        };

        var descriptionLabel = new Label
        {
            Location = new Point(12, 308),
            Size = new Size(516, 18),
            Text = "Описание",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.4f)
        };

        _description = new TextBox
        {
            Location = new Point(12, 328),
            Size = new Size(516, 44),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = properties.Description ?? string.Empty,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle,
            Enabled = editMode
        };

        right.Controls.Add(_shortName);
        right.Controls.Add(_fullName);
        right.Controls.Add(_description);
        right.Controls.Add(shortLabel);
        right.Controls.Add(fullLabel);
        right.Controls.Add(descriptionLabel);
        right.Controls.Add(info);

        var pickImage = new Button
        {
            Location = new Point(18, 452),
            Size = new Size(190, 34),
            Text = "Выбрать изображение…",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            Enabled = editMode
        };
        pickImage.FlatAppearance.BorderColor = Color.FromArgb(60, 66, 74);
        pickImage.Click += (_, _) => PickImage();

        var clearImage = new Button
        {
            Location = new Point(216, 452),
            Size = new Size(120, 34),
            Text = "Убрать изображение",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            Enabled = editMode
        };
        clearImage.FlatAppearance.BorderColor = Color.FromArgb(60, 66, 74);
        clearImage.Click += (_, _) => ClearImage();

        _validation = new Label
        {
            Location = new Point(18, 494),
            Size = new Size(610, 40),
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 8.6f)
        };

        var save = new Button
        {
            Location = new Point(18, 542),
            Size = new Size(170, 38),
            Text = "Сохранить",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Enabled = editMode,
            DialogResult = editMode ? DialogResult.OK : DialogResult.Cancel
        };
        save.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        save.Click += (_, _) => { /* проверка в OnFormClosing */ };

        // Кнопка закрытия всегда называется по делу: в режиме «Сведения»
        // «Сохранить» нечего, и слово «Сохранить» обещало бы запись.
        var close = new Button
        {
            Location = new Point(740, 542),
            Size = new Size(108, 38),
            Text = editMode ? "Отмена" : "Закрыть",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(220, 228, 236),
            DialogResult = DialogResult.Cancel
        };
        close.FlatAppearance.BorderColor = Color.FromArgb(60, 66, 74);

        Controls.Add(save);
        Controls.Add(close);
        Controls.Add(_validation);
        Controls.Add(clearImage);
        Controls.Add(pickImage);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(title);

        CancelButton = close;
        // Проверка имени перенесена в закрытие: если вешать её на Click, кнопка
        // с DialogResult уже закрывает окно, и ошибку показать негде.
        FormClosing += OnFormClosingHandler;

        RefreshImage(properties.ImagePath);
    }

    /// <summary>Исходные свойства: нужны вызывающему, чтобы сравнить с изменениями.</summary>
    public ResourceProperties Properties { get; }

    /// <summary>Полное имя после правки (пусто, если пользователь его очистил).</summary>
    public string FullNameValue => _fullName.Text.Trim();

    /// <summary>Короткое имя после правки.</summary>
    public string ShortNameValue => _shortName.Text.Trim();

    /// <summary>Описание после правки.</summary>
    public string DescriptionValue => _description.Text.Trim();

    /// <summary>
    /// Имя файла изображения, который надо скопировать в папку ресурса.
    /// <c>null</c> — изображение не менялось. Пустая строка — изображение снято.
    /// </summary>
    public string? PickedImagePath => _pickedImagePath;

    /// <summary>Каноническое имя файла изображения внутри папки ресурса.</summary>
    public string ImageFileName => Properties.ImageFileName ?? WorldPaths.WorldImageFileName;

    /// <summary>Менялось ли изображение вообще.</summary>
    public bool ImageChanged => _pickedImagePath is not null;

    private void PickImage()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Изображение ресурса",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Все файлы|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        // Проверка размера ДО копирования: многомегабайтная картинка в поставке
        // замедлит открытие окна, а причина будет неочевидна.
        var length = new FileInfo(dialog.FileName).Length;
        if (length > 8 * 1024 * 1024)
        {
            var answer = MessageBox.Show(this,
                "Файл больше 8 МБ (" + (length / (1024.0 * 1024.0)).ToString("0.0") + " МБ). " +
                "Изображение ресурса показывается в списках, и большой файл замедлит работу." +
                Environment.NewLine + Environment.NewLine + "Всё равно использовать его?",
                "Большое изображение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
                return;
        }

        _pickedImagePath = dialog.FileName;
        RefreshImage(dialog.FileName);
        _imageHint.Text = "Будет скопировано как " + ImageFileName;
    }

    private void ClearImage()
    {
        if (!ImageChanged && Properties.ImagePath is null)
        {
            _imageHint.Text = "Изображение не задано";
            return;
        }

        _pickedImagePath = string.Empty;
        _preview.Image = null;
        _imageHint.Text = "Изображение будет убрано";
    }

    private void OpenImageExternally()
    {
        var path = _pickedImagePath is { Length: > 0 } picked ? picked : Properties.ImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _imageHint.Text = "Изображение не задано";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ResourcePropertiesForm: не удалось открыть изображение.", ex.Message);
            _imageHint.Text = "Не удалось открыть изображение";
        }
    }

    /// <summary>
    /// Показывает изображение, не блокируя файл.
    ///
    /// Без копии в память `Image.FromFile` держит файл открытым, и следующее
    /// «Выбрать изображение» поверх того же файла падает с «используется другим
    /// процессом» — то есть второй выбор подряд не работал бы.
    /// </summary>
    private void RefreshImage(string? path)
    {
        _preview.Image?.Dispose();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _preview.Image = null;
            _imageHint.Text = "Изображение не задано";
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var source = Image.FromStream(stream);
            _preview.Image = new Bitmap(source);
            _imageHint.Text = Path.GetFileName(path) + " — ЛКМ, чтобы открыть";
        }
        catch (Exception ex)
        {
            _preview.Image = null;
            _imageHint.Text = "Файл не читается как изображение";
            AppLogger.Warn("ResourcePropertiesForm: изображение не читается.", $"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет значения перед закрытием.
    ///
    /// Проверка в закрытии, а не в Click: кнопка с DialogResult закрывает окно
    /// сама, и показать ошибку после нажатия было бы уже негде. Плюс так
    /// проверяются и Enter, и крестик — то есть любой путь сохранения.
    /// </summary>
    private void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
    {
        if (!_editEnabled || DialogResult != DialogResult.OK)
            return;

        var shortName = ShortNameValue;
        if (shortName.Length == 0)
        {
            e.Cancel = true;
            _validation.Text = "Короткое имя не может быть пустым: по нему строится папка ресурса.";
            _shortName.Focus();
            return;
        }

        if (!ResourceNaming.IsValidName(shortName))
        {
            e.Cancel = true;
            _validation.Text =
                "Недопустимое короткое имя. Разрешены буквы, цифры, пробел и подчёркивание; " +
                "имя не может оканчиваться точкой.";
            _shortName.Focus();
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

        if (DescriptionValue.Length > 4000)
        {
            e.Cancel = true;
            _validation.Text = "Описание слишком длинное (более 4000 символов).";
            _description.Focus();
            return;
        }

        _validation.Text = string.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Bitmap-копия не освобождается вместе с контролом при закрытии окна
            // в некоторых сценариях — освобождаем явно, чтобы файл не оставался
            // заблокированным до сборки мусора.
            _preview.Image?.Dispose();
            _preview.Image = null;
        }

        base.Dispose(disposing);
    }
}
