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
        Size = new Size(660, 480);
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

        var hint = new Label
        {
            AutoSize = false,
            Location = new Point(38, 196),
            Size = new Size(590, 44),
            Text = "Без галочки ресурс выгружается папкой и файлами — их видно и можно править. " +
                   "С галочкой содержимое сжимается в один файл .aqezip для пересылки.",
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8.4f, FontStyle.Italic)
        };

        var destinationHeader = new Label
        {
            AutoSize = false,
            Location = new Point(18, 248),
            Size = new Size(610, 20),
            Text = "Куда сохранится",
            ForeColor = Color.FromArgb(170, 180, 192),
            Font = new Font("Segoe UI", 8.6f, FontStyle.Bold)
        };

        _destination = new Label
        {
            AutoSize = false,
            Location = new Point(18, 270),
            Size = new Size(610, 78),
            Font = new Font("Consolas", 8.6f),
            ForeColor = Color.FromArgb(139, 216, 255)
        };

        var unique = new Label
        {
            AutoSize = false,
            Location = new Point(18, 352),
            Size = new Size(610, 34),
            Text = "Метка времени в пути не даёт двум выгрузкам перемешаться: «до» и «после» " +
                   "правки сравнимы. Если имя занято, добавляется «(2)» — существующее не затирается.",
            ForeColor = Color.FromArgb(150, 160, 172),
            Font = new Font("Segoe UI", 8.4f, FontStyle.Italic)
        };

        var export = new DarkFlatButton
        {
            Location = new Point(18, 396),
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
            Location = new Point(520, 396),
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
        Controls.Add(_archive);
        Controls.Add(_contents);
        Controls.Add(title);

        AcceptButton = export;
        CancelButton = cancel;

        UpdateDestination();
    }

    /// <summary>Выгрузка архивом. По умолчанию — нет: папка читаема, архив — один файл.</summary>
    public bool ArchiveRequested => _archive.Checked;

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
            ? "Зависимостей нет: выгружается только сам ресурс."
            : "Вместе с ресурсом уйдут: " + string.Join(", ", dependencies));
        lines.Add("Зависимости включаются всегда — без них пересланный ресурс не работает.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " Б";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " КБ";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
    }
}
