using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Диалог выгрузки ресурса (мир или кампания).
///
/// Галочка «Архивация в aqezip» — главное, ради чего диалог существует.
/// Умолчание — НЕ отмечена: папка с файлами остаётся читаемой и кладётся в
/// систему контроля версий, а архив необратимо превращает содержимое в один
/// файл. Отмеченный по умолчанию флаг означал бы, что пользователь теряет
/// читаемую копию, не заметив выбора.
///
/// Диалог НЕ трогает диск: он только собирает решение. Выгрузку выполняет
/// вызывающий код, поэтому диалог проверяем без файловой системы, а путь
/// назначения показывается ДО нажатия — иначе «куда оно сохранилось» искали бы
/// после.
/// </summary>
public sealed class ResourceExportForm : Form
{
    private readonly CheckBox _archive;
    private readonly CheckBox _dependencies;
    private readonly Label _destination;
    private readonly Label _contents;
    private readonly string _folderDestinationPreview;
    private readonly string _archiveDestinationPreview;

    /// <param name="kindLabel">«мира» / «кампании» — для заголовка и текста.</param>
    /// <param name="displayName">Название, как его видит пользователь.</param>
    /// <param name="sourceFolder">Папка ресурса: из неё берётся состав.</param>
    /// <param name="fileCount">Сколько файлов уйдёт.</param>
    /// <param name="totalBytes">Сколько байт займут.</param>
    /// <param name="dependencies">Что войдёт сверх самого ресурса (кампании мира, сцены квеста).</param>
    /// <param name="folderDestination">Куда ляжет папка (без учёта занятости имени).</param>
    /// <param name="archiveDestination">Куда ляжет архив (без учёта занятости имени).</param>
    public ResourceExportForm(
        string kindLabel,
        string displayName,
        string sourceFolder,
        int fileCount,
        long totalBytes,
        IReadOnlyList<string> dependencies,
        string folderDestination,
        string archiveDestination)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _folderDestinationPreview = folderDestination;
        _archiveDestinationPreview = archiveDestination;

        Text = "Экспорт " + kindLabel;
        StartPosition = FormStartPosition.CenterScreen;
        // Иконка приложения: окно без неё выглядит чужим в панели задач.
        AppIconService.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // Высота подобрана под самую нижнюю пару контролов: кнопки стоят на
        // y=430 и имеют высоту 38. При прежних 480 пикселях окна клиентская
        // область (480 минус заголовок 31) обрезала их на несколько пикселей —
        // кнопка «Экспортировать» была видна не полностью, и её не удавалось
        // замерить с экрана. Кнопки не поднимаются вверх намеренно: они стоят
        // ровно под подсказкой, и запас снизу нужен именно окну.
        Size = new Size(660, 530);
        BackColor = Color.FromArgb(10, 12, 16);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(18, 14, 18, 4),
            Text = "Экспорт " + kindLabel + ": " + displayName,
            ForeColor = Color.FromArgb(250, 176, 3),
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold)
        };

        _contents = new Label
        {
            AutoSize = false,
            Location = new Point(18, 62),
            Size = new Size(610, 96),
            Text = BuildContents(sourceFolder, fileCount, totalBytes, dependencies),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(214, 222, 232)
        };

        _archive = new CheckBox
        {
            AutoSize = false,
            Location = new Point(18, 168),
            Size = new Size(610, 26),
            Text = "Архивация в aqezip",
            Checked = false,
            ForeColor = Color.FromArgb(238, 243, 248),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        _archive.CheckedChanged += (_, _) => UpdateDestination();

        _dependencies = new CheckBox
        {
            AutoSize = false,
            Location = new Point(18, 198),
            Size = new Size(610, 26),
            Text = "Экспорт с зависимостями",
            Checked = kindLabel.Equals("мира", StringComparison.OrdinalIgnoreCase),
            Enabled = !kindLabel.Equals("мира", StringComparison.OrdinalIgnoreCase),
            ForeColor = Color.FromArgb(238, 243, 248),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var hint = new Label
        {
            AutoSize = false,
            Location = new Point(38, 226),
            Size = new Size(590, 48),
            Text = kindLabel.Equals("мира", StringComparison.OrdinalIgnoreCase)
                ? "Мир экспортируется полностью. В обычном режиме остаётся папка с файлами; " +
                  "при архивации содержимое сжимается в один файл .aqezip для пересылки."
                : "Без этой галочки экспортируется только сам ресурс и остаётся папкой с файлами. " +
                  "С галочкой добавляются родительские зависимости. При архивации содержимое " +
                  "сжимается в один файл .aqezip для пересылки.",
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8.4f, FontStyle.Italic)
        };

        var destinationHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 282),
            Size = new Size(610, 20),
            Text = "Куда сохранится",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f, FontStyle.Bold)
        };

        _destination = new Label
        {
            AutoSize = false,
            Location = new Point(18, 304),
            Size = new Size(610, 78),
            Font = new Font("Consolas", 8.6f),
            ForeColor = Color.FromArgb(139, 216, 255)
        };

        var unique = new Label
        {
            AutoSize = false,
            Location = new Point(18, 386),
            Size = new Size(610, 34),
            Text = "Метка времени в пути не даёт двум выгрузкам перемешаться: «до» и «после» " +
                   "правки сравнимы. Если имя занято, добавляется «(2)» — существующее не затирается.",
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8.4f, FontStyle.Italic)
        };

        var export = new DarkFlatButton
        {
            Location = new Point(18, 430),
            Size = new Size(170, 38),
            Text = "Экспортировать",
            BackColor = Color.FromArgb(250, 176, 3),
            ForeColor = Color.FromArgb(20, 20, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            DialogResult = DialogResult.OK
        };
        export.FlatAppearance.BorderColor = Color.FromArgb(250, 176, 3);

        var cancel = new DarkFlatButton
        {
            Location = new Point(520, 430),
            Size = new Size(108, 38),
            Text = "Отмена",
            DialogResult = DialogResult.Cancel
        };

        Controls.Add(export);
        Controls.Add(cancel);
        Controls.Add(unique);
        Controls.Add(_destination);
        Controls.Add(destinationHeader);
        Controls.Add(hint);
        Controls.Add(_dependencies);
        Controls.Add(_archive);
        Controls.Add(_contents);
        Controls.Add(title);

        AcceptButton = export;
        CancelButton = cancel;

        UpdateDestination();
    }

    /// <summary>Выгрузка архивом. По умолчанию — нет: папка читаема, архив — один файл.</summary>
    public bool ArchiveRequested => _archive.Checked;

    /// <summary>Вошли ли в выгрузку родительские зависимости.</summary>
    public bool DependenciesRequested =>
        ArchiveRequested || _dependencies.Checked || _dependencies.Enabled == false;

    /// <summary>Пункт меню, которым диалог открыт — «мир» или «кампания».</summary>
    public bool IsArchive => _archive.Checked;

    private void UpdateDestination()
    {
        _destination.Text = ArchiveRequested
            ? _archiveDestinationPreview
            : _folderDestinationPreview;
    }

    /// <summary>
    /// Состав выгрузки. Зависимости называются поимённо, а не строкой «включены»:
    /// «в мир войдут 2 кампании» ничего не говорит о том, какие именно, и автор не
    /// может проверить, всё ли уходит.
    /// </summary>
    private static string BuildContents(
        string sourceFolder,
        int fileCount,
        long totalBytes,
        IReadOnlyList<string> dependencies)
    {
        var lines = new List<string>
        {
            "Папка: " + sourceFolder,
            "Файлов: " + fileCount + "   ·   объём: " + FormatBytes(totalBytes)
        };

        lines.Add(dependencies.Count == 0
            ? "Зависимости: нет."
            : "Доступные зависимости: " + string.Join(", ", dependencies));
        lines.Add("Режим «Экспорт с зависимостями» добавляет только перечисленные родительские ресурсы; " +
                  "соседние кампании и квесты в экспорт не попадают.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " Б";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " КБ";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
    }
}
