using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Первичная настройка: псевдоним автора и язык интерфейса.
///
/// Отдельный модальный диалог, а не встроенная панель главного окна: без
/// псевдонима приложение работать не должно (им подписываются все созданные
/// ресурсы — created_by/modified_by), а главное окно к моменту настройки уже
/// поднимает WebView и мир. Диалог позволяет не пускать к работе, не усложняя
/// саму форму.
///
/// Закрытие крестиком возвращает <see cref="DialogResult.Cancel"/>: отказ — это
/// нормальный исход, и вызывающий код обязан его обработать (приложение
/// завершается), а не продолжать работу с пустым псевдонимом.
/// </summary>
public sealed class FirstRunSetupForm : Form
{
    private readonly TextBox _author;
    private readonly Label _validation;
    private readonly Button _save;
    private readonly ComboBox _language;
    private readonly Label _preview;

    public FirstRunSetupForm(string? suggestedAuthor = null, string? language = null)
    {
        Text = "Первичная настройка Assist Quest Editor";
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(560, 430);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(18, 14, 18, 4),
            Text = "Как вас подписывать?",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold)
        };

        var explanation = new Label
        {
            Dock = DockStyle.Top,
            Height = 76,
            Padding = new Padding(18, 0, 18, 8),
            Text =
                "Псевдоним попадает в каждый созданный ресурс — мир, кампанию и квест —\r\n" +
                "как подпись автора (created_by / modified_by). По ней видно, кто и когда\r\n" +
                "правил контент, поэтому без псевдонима работа не начинается.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 9f)
        };

        var authorHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 132),
            Size = new Size(510, 22),
            Text = "Псевдоним",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        _author = new TextBox
        {
            Location = new Point(18, 156),
            Width = 510,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle,
            // Значение по умолчанию — «User_ГГММДДЧЧмм»: оно всегда допустимо и
            // сразу показывает ФОРМУ имени, а не оставляет поле пустым.
            Text = string.IsNullOrWhiteSpace(suggestedAuthor)
                ? ResourceMetadata.DefaultAuthor(DateTimeOffset.Now)
                : suggestedAuthor
        };

        var useComputerName = new DarkFlatButton
        {
            Location = new Point(18, 190),
            Size = new Size(120, 30),
            Text = "Имя ПК"
        };
        useComputerName.Click += (_, _) => _author.Text = NormalizeCandidate(Environment.MachineName);

        var useAccountName = new DarkFlatButton
        {
            Location = new Point(146, 190),
            Size = new Size(140, 30),
            Text = "Имя учётной записи"
        };
        useAccountName.Click += (_, _) => _author.Text = NormalizeCandidate(Environment.UserName);

        _validation = new Label
        {
            AutoSize = false,
            Location = new Point(18, 226),
            Size = new Size(510, 34),
            ForeColor = Color.FromArgb(207, 12, 12),
            Font = new Font("Segoe UI", 8.6f)
        };

        var languageHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 264),
            Size = new Size(510, 22),
            Text = "Язык интерфейса",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        _language = new ComboBox
        {
            Location = new Point(18, 288),
            Width = 240,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244)
        };
        // Пока доступен только русский. Пункт всё равно выбирается явно: список
        // из одного языка выглядит странно, но молчаливое «язык не спрашивали»
        // пришлось бы объяснять при появлении второго.
        _language.Items.Add("Русский");
        _language.SelectedIndex = 0;

        _preview = new Label
        {
            AutoSize = false,
            Location = new Point(18, 322),
            Size = new Size(510, 22),
            ForeColor = Color.FromArgb(160, 170, 182),
            Font = new Font("Segoe UI", 8.4f)
        };

        _save = new DarkFlatButton
        {
            Location = new Point(18, 348),
            Size = new Size(180, 38),
            Text = "Сохранить и продолжить",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _save.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _save.Click += (_, _) => TryAccept();

        // Отмена закрывает диалог с Cancel: приложение не должно продолжать
        // работу без подписи, поэтому «отмена» = «выйти», а не «пропустить».
        var cancel = new DarkFlatButton
        {
            Location = new Point(410, 348),
            Size = new Size(118, 38),
            Text = "Выйти",
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(_save);
        Controls.Add(cancel);
        Controls.Add(_preview);
        Controls.Add(_language);
        Controls.Add(languageHeader);
        Controls.Add(_validation);
        Controls.Add(useAccountName);
        Controls.Add(useComputerName);
        Controls.Add(_author);
        Controls.Add(authorHeader);
        Controls.Add(explanation);
        Controls.Add(title);

        AcceptButton = _save;

        _author.TextChanged += (_, _) => ValidateAuthor();
        ValidateAuthor();
    }

    /// <summary>Псевдоним, введённый пользователем. Осмыслен только при OK.</summary>
    public string Author => _author.Text.Trim();

    /// <summary>Код выбранного языка интерфейса.</summary>
    public string Language => "ru";

    /// <summary>
    /// Приводит имя ПК или учётной записи к допустимому ПСЕВДОНИМУ.
    ///
    /// Кнопки «Имя ПК» и «Имя учётной записи» существуют, чтобы не набирать
    /// вручную, но значения системы не обязаны быть допустимыми: в имени
    /// учётной записи встречаются точки, дефисы и домен (`DOMAIN\user`). Молча
    /// подставить такое значение значило бы показать красную ошибку сразу после
    /// нажатия кнопки — то есть кнопка «не работает». Недопустимые символы
    /// заменяются подчёркиванием, как и требует формат псевдонима.
    /// </summary>
    private static string NormalizeCandidate(string? value)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(candidate))
            return ResourceMetadata.DefaultAuthor(DateTimeOffset.Now);

        var builder = new System.Text.StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            builder.Append(ResourceMetadata.IsValidAuthor(character.ToString()) || character == '_'
                ? character
                : '_');
        }

        var normalized = builder.ToString();
        return ResourceMetadata.IsValidAuthor(normalized)
            ? normalized
            : ResourceMetadata.DefaultAuthor(DateTimeOffset.Now);
    }

    /// <summary>
    /// Проверяет ввод и не даёт закрыть диалог с недопустимым псевдонимом.
    ///
    /// Кнопка блокируется, а причина пишется рядом с полем: молча неактивная
    /// кнопка не объясняет, что не так, и на первом запуске это выглядит как
    /// сломанное приложение.
    ///
    /// Имя не <c>Validate</c>: у WinForms-формы уже есть унаследованный
    /// <c>ContainerControl.Validate()</c>, и совпадение имён компилятор считает
    /// ошибкой (CS0108).
    /// </summary>
    private void ValidateAuthor()
    {
        var text = _author.Text.Trim();

        string? problem = null;
        if (text.Length == 0)
            problem = "Псевдоним не может быть пустым.";
        else if (text != _author.Text)
            problem = "Псевдоним не может начинаться или заканчиваться пробелом.";
        else if (!ResourceMetadata.IsValidAuthor(text))
            problem = "Допустимы буквы (русские и латинские), цифры, пробел и подчёркивание.";

        _validation.Text = problem ?? string.Empty;
        _save.Enabled = problem is null;

        _preview.Text = problem is null
            ? $"В ресурсах будет: Created by: {text}"
            : string.Empty;
    }

    private void TryAccept()
    {
        ValidateAuthor();
        if (!_save.Enabled)
            return;

        DialogResult = DialogResult.OK;
        Close();
    }
}
