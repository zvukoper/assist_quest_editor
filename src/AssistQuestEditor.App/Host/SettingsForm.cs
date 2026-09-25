using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Настройки приложения.
///
/// Раздел «Имя и авторство» — ПЕРВЫЙ, а не последний: имя определяет подпись
/// всех созданных ресурсов (created_by / modified_by), и его правят чаще, чем
/// параметры окон. Раздел существует ещё и потому, что имя можно не указывать
/// вовсе: тогда работа идёт анонимно, а настройки — единственное место, где это
/// можно исправить, не перезапуская приложение.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly Button _registerExtensionsButton;
    private readonly Button _resetWindowsButton;
    private readonly TextBox _nameBox;
    private readonly Label _nameStatus;
    private readonly Button _saveName;

    /// <summary>
    /// Имя, которое сейчас ЗАПИСАНО в настройках (null — не указано). Нужно,
    /// чтобы кнопка сохранения была доступна только при реальном изменении:
    /// постоянно активная кнопка не сообщает, есть ли что записывать.
    /// </summary>
    private string? _savedAuthor;

    public SettingsForm()
    {
        Text = "Настройки";
        StartPosition = FormStartPosition.CenterParent;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        // Высота выросла вместе с разделом имени, и это не «на глазок»: `Size`
        // формы включает рамку окна, поэтому содержимое обрезается задолго до
        // нижней границы. Прежняя высота 470 давала клиентскую область около 439
        // px, и раздел имени в неё бы не поместился — кнопки оказались бы за
        // краем и выглядели бы отсутствующими.
        Size = new Size(650, 720);
        MinimumSize = new Size(640, 700);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        WindowGeometryStore.Attach(this, "settings");

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(18, 16, 18, 6),
            Text = "Настройки приложения",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold)
        };

        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(18, 0, 18, 10),
            Text =
                "Имя определяет подпись ресурсов, окна сохраняют координаты и размер.\r\n" +
                "Сохранённые данные хранятся глобально для всех окон Assist Quest Editor.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 9f)
        };

        // --- Раздел имени и авторства ---

        var nameHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 122),
            Size = new Size(600, 28),
            Text = "Имя и авторство",
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var saved = AppUiPreferencesStore.Load().Author;
        _savedAuthor = AuthorIdentity.IsNamed(saved) ? saved!.Trim() : null;

        var nameText = new Label
        {
            AutoSize = false,
            Location = new Point(18, 150),
            Size = new Size(600, 24),
            Text = "Текущее имя: " +
                   (_savedAuthor is null ? "не указано" : _savedAuthor),
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        _nameBox = new TextBox
        {
            Location = new Point(18, 288),
            Width = 380,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle,
            // Пустое поле — это НЕ «оставьте как было», а «имени нет». Показывать
            // в нём подпись «анонимно» значило бы предлагать сохранить служебное
            // слово как имя.
            Text = _savedAuthor ?? string.Empty
        };

        _nameStatus = new Label
        {
            AutoSize = false,
            Location = new Point(18, 176),
            Size = new Size(600, 20),
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8.6f)
        };

        var nameHint = new Label
        {
            AutoSize = false,
            Location = new Point(18, 198),
            Size = new Size(600, 58),
            Text =
                "Имя — это подпись автора: оно записывается в каждый созданный ресурс (мир, кампанию,\r\n" +
                "квест, сцену) как created_by / modified_by. По нему видно, кто и когда правил контент.\r\n" +
                "Имя можно не указывать — тогда изменения подписываются словом «анонимно».",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f)
        };

        var nameFieldHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 264),
            Size = new Size(600, 22),
            Text = "Имя автора",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var useComputerName = new DarkFlatButton
        {
            Location = new Point(18, 328),
            Size = new Size(120, 30),
            Text = "Имя ПК"
        };
        useComputerName.Click += (_, _) => _nameBox.Text = NormalizeName(Environment.MachineName);

        var useAccountName = new DarkFlatButton
        {
            Location = new Point(146, 328),
            Size = new Size(140, 30),
            Text = "Имя учётной записи"
        };
        useAccountName.Click += (_, _) => _nameBox.Text = NormalizeName(Environment.UserName);

        var skipHint = new Label
        {
            AutoSize = false,
            Location = new Point(298, 328),
            Size = new Size(320, 30),
            Text = "Оставьте поле пустым — имя можно указать позже, здесь же.",
            ForeColor = Color.FromArgb(125, 135, 148),
            Font = new Font("Segoe UI", 8.2f)
        };

        _saveName = new DarkFlatButton
        {
            AutoSize = false,
            Location = new Point(18, 366),
            Size = new Size(220, 38),
            Text = "Сохранить имя",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(28, 28, 28),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _saveName.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _saveName.Click += (_, _) => SaveName();

        // --- Раздел Windows ---

        var windowsHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 418),
            Size = new Size(600, 28),
            Text = "Windows и файлы проекта",
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var windowsDescription = new Label
        {
            AutoSize = false,
            Location = new Point(18, 446),
            Size = new Size(600, 50),
            Text =
                "Зарегистрирует текущие форматы .aqquest и .aqscene в Windows, " +
                "добавит их в «Открыть с помощью» и установит иконки Assist Quest. " +
                "Если для расширения уже выбран другой редактор по умолчанию, Windows сохранит этот выбор.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.8f)
        };

        _registerExtensionsButton = new DarkFlatButton
        {
            AutoSize = false,
            Location = new Point(18, 506),
            Size = new Size(285, 42),
            Text = "Зарегистрировать расширения",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(28, 28, 28),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _registerExtensionsButton.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _registerExtensionsButton.Click += RegisterExtensionsButton_Click;

        _resetWindowsButton = new DarkFlatButton
        {
            AutoSize = false,
            Location = new Point(18, 562),
            Size = new Size(260, 42),
            Text = "Сброс настроек окон",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(28, 28, 28),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _resetWindowsButton.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _resetWindowsButton.Click += (_, _) => ResetWindowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var hint = new Label
        {
            AutoSize = false,
            // Ширина ограничена намеренно: правый нижний угол занят кнопкой
            // «Закрыть», и подсказка во всю ширину залезала бы под неё.
            Location = new Point(18, 614),
            Size = new Size(490, 56),
            Text =
                "Сброс настроек окон удаляет сохранённые координаты и размеры. " +
                "Используйте его, если окно оказалось за пределами экранов или запомнился некорректный размер.",
            ForeColor = Color.FromArgb(125, 135, 148),
            Font = new Font("Segoe UI", 8.5f)
        };

        var closeButton = new DarkFlatButton
        {
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            Size = new Size(96, 30),
            Location = new Point(532, 645),
            Text = "Закрыть",
            BackColor = Color.FromArgb(32, 36, 42),
            ForeColor = Color.FromArgb(220, 228, 236)
        };
        closeButton.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 84);
        closeButton.Click += (_, _) => Close();

        Controls.Add(closeButton);
        Controls.Add(hint);
        Controls.Add(_resetWindowsButton);
        Controls.Add(_registerExtensionsButton);
        Controls.Add(windowsDescription);
        Controls.Add(windowsHeader);
        Controls.Add(_saveName);
        Controls.Add(skipHint);
        Controls.Add(useAccountName);
        Controls.Add(useComputerName);
        Controls.Add(_nameBox);
        Controls.Add(nameFieldHeader);
        Controls.Add(nameHint);
        Controls.Add(_nameStatus);
        Controls.Add(nameText);
        Controls.Add(nameHeader);
        Controls.Add(description);
        Controls.Add(title);

        _nameBox.TextChanged += (_, _) => ValidateName();
        ValidateName();
    }

    public event EventHandler? ResetWindowSettingsRequested;

    /// <summary>
    /// Имя сохранено. Главная форма по этому событию обновляет подпись в шапке и
    /// передаёт новое имя сторам: без этого следующие правки мира и кампаний
    /// подписывались бы прежним именем до перезапуска.
    /// </summary>
    public event EventHandler? AuthorChanged;

    /// <summary>
    /// Приводит имя ПК или учётной записи к допустимому псевдониму.
    ///
    /// Значения системы не обязаны быть допустимыми: в имени учётной записи
    /// встречаются точки, дефисы и домен (DOMAIN\user). Молча подставить такое
    /// значение значило бы показать ошибку сразу после нажатия кнопки — то есть
    /// кнопка «не работает».
    /// </summary>
    private static string NormalizeName(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            builder.Append(ResourceMetadata.IsValidAuthor(character.ToString()) || character == '_'
                ? character
                : '_');
        }

        var normalized = builder.ToString();
        return ResourceMetadata.IsValidAuthor(normalized) && AuthorIdentity.IsNamed(normalized)
            ? normalized
            : string.Empty;
    }

    /// <summary>
    /// Проверяет имя и не даёт сохранить недопустимое.
    ///
    /// Пустое поле — ДОПУСТИМОЕ состояние: это и есть анонимная работа. Молча
    /// неактивная кнопка без объяснения выглядела бы как «сохранение не работает»,
    /// поэтому рядом всегда написано, что именно произойдёт.
    /// </summary>
    private void ValidateName()
    {
        var text = _nameBox.Text.Trim();

        string? problem = null;
        if (text.Length > 0 && text != _nameBox.Text)
            problem = "Имя не может начинаться или заканчиваться пробелом.";
        else if (text.Length > 0 && !ResourceMetadata.IsValidAuthor(text))
            problem = "Допустимы буквы (русские и латинские), цифры, пробел и подчёркивание.";
        else if (text.Length > 0 && !AuthorIdentity.IsNamed(text))
            problem = "«анонимно» — служебная подпись. Введите своё имя или оставьте поле пустым.";

        _nameStatus.ForeColor = problem is null
            ? Color.FromArgb(150, 160, 172)
            : Color.FromArgb(207, 12, 12);
        _nameStatus.Text = problem
            ?? (text.Length == 0
                ? "Имя не указано: изменения будут подписаны как «анонимно»."
                : "Будет сохранено: " + text);

        var editable = text.Length == 0 ? null : text;
        _saveName.Enabled = problem is null &&
            !string.Equals(editable, _savedAuthor, StringComparison.Ordinal);
    }

    private void SaveName()
    {
        ValidateName();
        if (!_saveName.Enabled)
            return;

        var text = _nameBox.Text.Trim();

        // В файле хранится null, а не слово «анонимно»: подпись ресурса
        // подставляется при записи, и если бы «анонимно» лежало ещё и в
        // настройках, отличить «имя не указано» от «человек так назвался» было
        // бы нельзя.
        var stored = text.Length == 0 ? null : text;

        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with
        {
            Author = stored,
            // Имя настроили — значит первичный шаг пройден. Без этого флага
            // диалог имени спрашивался бы при КАЖДОМ запуске даже у того, кто
            // ввёл имя здесь, и настройка имени в настройках была бы бесполезной.
            SetupCompleted = true
        });

        _savedAuthor = stored;
        ValidateName();

        AppLogger.Info("SettingsForm: имя автора сохранено.",
            $"author={stored ?? "<анонимно>"}");
        AuthorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RegisterExtensionsButton_Click(object? sender, EventArgs e)
    {
        _registerExtensionsButton.Enabled = false;
        try
        {
            var result = FileAssociationRegistry.Register();

            MessageBox.Show(
                this,
                result.Message,
                result.Success ? "Расширения Assist Quest" : "Ошибка регистрации расширений",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        finally
        {
            _registerExtensionsButton.Enabled = true;
        }
    }
}
