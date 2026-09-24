using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Выбор мира. Показывается при старте, когда миров ещё нет, и тогда
/// единственное осмысленное действие — создать первый.
///
/// Отдельный диалог, а не пустая главная форма: мир — ПРОЕКТ, и без него нечего
/// открывать. Раньше приложение просто открывало редактор над папкой `data`
/// рядом с EXE, поэтому «нет мира» было неотличимо от «мир есть».
///
/// Поле имени пульсирует, когда миров нет: это единственное, что нужно сделать,
/// а неподвижная форма не подсказывает, куда нажимать.
/// </summary>
public sealed class WorldChooserForm : Form
{
    private readonly WorldStore _store;
    private readonly TextBox _name;
    private readonly Label _validation;
    private readonly Button _create;
    private readonly ListBox _existing;
    private readonly Label _status;

    public WorldChooserForm(WorldStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));

        var hasWorlds = _store.HasWorlds;

        Text = hasWorlds ? "Выбор мира" : "Создание первого мира";
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(600, hasWorlds ? 570 : 310);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(18, 14, 18, 4),
            Text = hasWorlds ? "Выберите мир" : "Миров пока нет",
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold)
        };

        var explanation = new Label
        {
            Dock = DockStyle.Top,
            Height = hasWorlds ? 62 : 74,
            Padding = new Padding(18, 0, 18, 8),
            Text = hasWorlds
                ? "Мир — это проект: внутри него живут кампании, а в кампаниях — квесты.\r\n" +
                  "Выберите существующий мир или создайте новый."
                : "Мир — это проект: внутри него живут кампании, а в кампаниях — квесты.\r\n" +
                  "Создайте первый мир, чтобы начать. В нём сразу появится кампания Common\r\n" +
                  "с демонстрационным квестом. Либо пропустите этот шаг — тогда будет\r\n" +
                  "установлен готовый демонстрационный мир для обучения.",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 9f)
        };

        Controls.Add(explanation);
        Controls.Add(title);

        // Строка состояния: кнопка «Пропустить» запускает импорт архива, а это
        // заметное действие — пользователь должен видеть, что происходит.
        _status = new Label
        {
            AutoSize = false,
            Location = new Point(18, (hasWorlds ? 448 : 244) + 8),
            Size = new Size(550, 22),
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f)
        };

        var nameHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, hasWorlds ? 128 : 116),
            Size = new Size(550, 22),
            Text = hasWorlds ? "Новый мир" : "Название мира",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        _name = new TextBox
        {
            Location = new Point(18, hasWorlds ? 152 : 140),
            Width = 550,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.FromArgb(23, 24, 25),
            ForeColor = Color.FromArgb(231, 237, 244),
            BorderStyle = BorderStyle.FixedSingle
        };

        _validation = new Label
        {
            AutoSize = false,
            Location = new Point(18, hasWorlds ? 184 : 172),
            Size = new Size(550, 22),
            ForeColor = Color.FromArgb(207, 12, 12),
            Font = new Font("Segoe UI", 8.6f)
        };

        _create = new DarkFlatButton
        {
            Location = new Point(18, hasWorlds ? 210 : 198),
            Size = new Size(200, 38),
            Text = hasWorlds ? "Создать и открыть" : "Создать мир",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _create.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);
        _create.Click += (_, _) => TryCreate();

        Controls.Add(_create);
        Controls.Add(_validation);
        Controls.Add(_name);
        Controls.Add(nameHeader);

        if (hasWorlds)
        {
            var existingHeader = new Label
            {
                AutoSize = false,
                Location = new Point(18, 262),
                Size = new Size(550, 22),
                Text = "Существующие миры",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };

            _existing = new ListBox
            {
                Location = new Point(18, 286),
                Size = new Size(550, 150),
                BackColor = Color.FromArgb(23, 24, 25),
                ForeColor = Color.FromArgb(231, 237, 244),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5f)
            };

            foreach (var world in _store.Worlds)
                _existing.Items.Add(new WorldItem(world));

            _existing.SelectedIndex = 0;

            var open = new DarkFlatButton
            {
                Location = new Point(18, 448),
                Size = new Size(160, 38),
                Text = "Открыть"
            };
            open.Click += (_, _) => TryOpenExisting();

            // «Выйти» — в ПРАВОМ нижнем углу, отдельно от действий с миром:
            // это не выбор мира, а отказ от работы, и стоять в одном ряду с
            // «Создать»/«Открыть» он не должен.
            var quit = new DarkFlatButton
            {
                Location = new Point(450, 448),
                Size = new Size(118, 38),
                Text = "Выйти",
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(open);
            Controls.Add(quit);
            Controls.Add(_status);
            Controls.Add(_existing);
            Controls.Add(existingHeader);

            // Двойной клик по миру — привычный способ открыть: без него
            // приходится целиться в кнопку, хотя список и так под курсором.
            _existing.DoubleClick += (_, _) => TryOpenExisting();
        }
        else
        {
            _existing = new ListBox { Visible = false };

            // «Пропустить» — отдельная кнопка, а не пункт списка: она делает
            // ДРУГОЕ действие (импортирует архив), и смешивать его с «создать
            // пустой мир» нельзя — результат разный.
            var skip = new DarkFlatButton
            {
                Location = new Point(18, 198),
                Size = new Size(330, 38),
                Text = "Пропустить (создастся демо-мир для обучения)",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            skip.Click += (_, _) => TryImportDemoWorld();

            // «Выйти» — в правом нижнем углу, в стороне от действий с миром.
            var quit = new DarkFlatButton
            {
                Location = new Point(450, 198),
                Size = new Size(118, 38),
                Text = "Выйти",
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(skip);
            Controls.Add(quit);
            Controls.Add(_status);
        }

        AcceptButton = _create;

        _name.TextChanged += (_, _) => ValidateName();
        ValidateName();

        // Пульс поля имени, когда миров нет: единственное действие обязано быть
        // заметным. Анимация — часть диалога, поэтому встраивается в тот же
        // WinForms-таймер, а не в CSS: это нативный диалог.
        if (!hasWorlds)
        {
            var pulse = new System.Windows.Forms.Timer { Interval = 750 };
            var highlight = false;
            pulse.Tick += (_, _) =>
            {
                highlight = !highlight;
                _name.BackColor = highlight
                    ? Color.FromArgb(38, 34, 22)
                    : Color.FromArgb(23, 24, 25);
            };
            pulse.Start();
            FormClosed += (_, _) => pulse.Dispose();
        }
    }

    /// <summary>Выбранный или созданный мир. Осмыслен только при OK.</summary>
    public WorldRecord? SelectedWorld { get; private set; }

    /// <summary>
    /// Проверяет имя нового мира.
    ///
    /// Имя не <c>Validate</c>: у формы уже есть унаследованный
    /// <c>ContainerControl.Validate()</c>, и совпадение имён — ошибка CS0108.
    /// </summary>
    private void ValidateName()
    {
        var text = _name.Text;
        string? problem = null;

        if (text.Trim().Length == 0)
            problem = null; // Пустое поле — это ещё не ошибка, а «ничего не введено».
        else if (!ResourceNaming.IsValidName(text))
            problem = "Название не может содержать символы пути (: \\ / * ? \" < > |) и пробелы по краям.";
        else if (_store.Worlds.Any(world =>
                     string.Equals(world.Definition.Id, FolderId(text), StringComparison.OrdinalIgnoreCase)))
            problem = "Мир с таким названием уже есть.";

        _validation.Text = problem ?? string.Empty;
        _create.Enabled = problem is null && text.Trim().Length > 0;
    }

    /// <summary>
    /// Id, которым обернётся папка. Дублируется здесь только для ПРОВЕРКИ
    /// занятости: создать мир всё равно даёт <see cref="WorldStore"/>, и он —
    /// единственный источник истины. Расхождение привело бы лишь к тому, что
    /// ошибку показал бы Host, а не поле.
    /// </summary>
    private static string FolderId(string name) =>
        ResourceNaming.ToFolderName(name).Replace(' ', '_').ToLowerInvariant();

    private void TryCreate()
    {
        ValidateName();
        if (!_create.Enabled)
            return;

        try
        {
            SelectedWorld = _store.CreateWorld(_name.Text.Trim());
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            // Ошибку Host показываем в диалоге, а не падаем: упавшее создание
            // мира (занятая папка, нет прав) — обычная ситуация, и пользователю
            // нужно вернуться к вводу, а не получить закрытое приложение.
            _validation.Text = "Не удалось создать мир: " + ex.Message;
            AppLogger.Error("WorldChooserForm: не удалось создать мир.", ex, "name=" + _name.Text.Trim());
        }
    }

    private void TryOpenExisting()
    {
        if (_existing.SelectedItem is not WorldItem item)
            return;

        SelectedWorld = item.Record;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// «Пропустить»: импортирует поставляемый демо-мир из архива.
    ///
    /// Импорт идёт ТЕМ ЖЕ кодом, что и любой архив от другого автора
    /// (<see cref="WorldStore.ImportBundledDemoWorld"/>): отдельная ветка
    /// «сгенерировать демо» неизбежно разошлась бы с настоящим импортом, и
    /// кнопка перестала бы проверять рабочую дорогу.
    ///
    /// Ошибка импорта не закрывает диалог: пользователь должен вернуться к
    /// выбору (создать мир вручную), а не остаться без приложения.
    /// </summary>
    private void TryImportDemoWorld()
    {
        _status.ForeColor = Color.FromArgb(170, 180, 192);
        _status.Text = "Импорт демонстрационного мира…";
        _create.Enabled = false;
        Application.DoEvents();

        try
        {
            SelectedWorld = _store.ImportBundledDemoWorld();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.ForeColor = Color.FromArgb(207, 12, 12);
            _status.Text = "Не удалось установить демо-мир: " + ex.Message +
                           " — создайте мир вручную или выйдите.";
            _create.Enabled = _name.Text.Trim().Length > 0;
            AppLogger.Error("WorldChooserForm: не удалось установить демо-мир.", ex);
        }
    }

    /// <summary>
    /// Элемент списка миров. Показывает отображаемое имя и подпись изменения:
    /// по ним автор узнаёт, тот ли это мир, не открывая его.
    /// </summary>
    private sealed record WorldItem(WorldRecord Record)
    {
        public override string ToString()
        {
            var signature = WorldDisplayRules.Describe(Record.Definition.Metadata);
            return signature is null
                ? Record.DisplayName
                : Record.DisplayName + "   —   " + signature;
        }
    }
}
