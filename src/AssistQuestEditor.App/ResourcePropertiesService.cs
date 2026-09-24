using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>Что показать в окне «Сведения» и что предложить изменить.</summary>
public sealed record ResourceProperties(
    string KindLabel,
    string DisplayName,
    string ShortName,
    string Id,
    int Version,
    string? Description,
    string? ImagePath,
    string? ImageFileName,
    string? ParentLabel,
    ResourceMetadata? Metadata,
    IReadOnlyList<string> Composition,
    string FolderPath);

/// <summary>
/// Редактирование и просмотр свойств мира и кампании.
///
/// Один класс на два вида ресурсов: свойства у них одинаковые (изображение,
/// полное имя, описание), и отдельные сервисы неизбежно разошлись бы в правилах
/// — например, один проверял бы имя, а второй нет. Различие только в способе
/// записи на диск, и он вынесен в конец.
/// </summary>
public static class ResourcePropertiesService
{
    /// <summary>
    /// Собирает свойства мира для показа.
    ///
    /// Состав считается ПО ДИСКУ, а не по каталогу в памяти: окно обещает
    /// показать, что лежит в папке, и каталог в памяти мог отстать (файл
    /// добавлен в проводнике).
    /// </summary>
    public static ResourceProperties DescribeWorld(WorldRecord world, CampaignStore campaigns)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(campaigns);

        var composition = new List<string>();

        var campaignNames = campaigns.Records
            .Select(record => record.Definition.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        composition.Add(campaignNames.Length == 0
            ? "Кампаний нет — их можно создать."
            : $"Кампаний: {campaignNames.Length} — " + string.Join(", ", campaignNames));

        var quests = campaigns.Records.Sum(record => record.Definition.Quests.Count);
        composition.Add($"Квестов в кампаниях: {quests}");

        var saves = WorldPaths.SavesFolderPath(world.FolderPath);
        var saveCount = Directory.Exists(saves)
            ? Directory.EnumerateFiles(saves, "*.aqsave").Count()
            : 0;
        composition.Add(saveCount == 0
            ? "Сохранений нет."
            : $"Сохранений: {saveCount}");

        return new ResourceProperties(
            KindLabel: "мира",
            DisplayName: world.DisplayName,
            ShortName: world.Definition.Name,
            Id: world.Definition.Id,
            Version: world.Definition.Version,
            Description: world.Definition.Description,
            ImagePath: ResolveImagePath(world.FolderPath, world.Definition.ImageFile,
                WorldPaths.WorldImageFileName),
            ImageFileName: string.IsNullOrWhiteSpace(world.Definition.ImageFile)
                ? WorldPaths.WorldImageFileName
                : world.Definition.ImageFile,
            ParentLabel: null,
            Metadata: world.Definition.Metadata,
            Composition: composition,
            FolderPath: world.FolderPath);
    }

    /// <summary>
    /// Собирает свойства кампании.
    ///
    /// Родитель показывается ВСЕГДА, даже если он верен. Причина: кампанию можно
    /// перенести в чужую папку, и «родитель совпадает» — это ответ, а не
    /// отсутствие ответа.
    /// </summary>
    public static ResourceProperties DescribeCampaign(
        CampaignStore.CampaignRecord campaign,
        WorldRecord? world)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var parent = campaign.Definition.WorldId;
        var parentLabel = world is null
            ? string.IsNullOrWhiteSpace(parent) ? null : "Мир: " + parent
            : string.IsNullOrWhiteSpace(parent)
                // Пусто у файлов, созданных до появления поля. Это не ошибка,
                // поэтому и пугающей формулировки быть не должно.
                ? "Родитель не указан — считается «" + world.DisplayName + "»."
                : parent.Equals(world.Definition.Id, StringComparison.OrdinalIgnoreCase)
                    ? "Мир: " + world.DisplayName
                    : "Мир: " + parent + " (в папке мира «" + world.DisplayName + "»!)";

        var composition = new List<string>
        {
            $"Квестов: {campaign.Definition.Quests.Count} " +
            $"(включено: {campaign.Definition.Quests.Count(q => q.Status == CampaignQuestStatus.Enabled)})"
        };

        var files = campaign.Definition.Files.Count;
        composition.Add(files == 0
            ? "Каталог кампании пуст."
            : $"Файлов ресурса: {files}");

        composition.Add(campaign.Definition.Active
            ? "Кампания активна: симуляция использует её мир."
            : "Кампания отключена.");

        if (campaign.Definition.Geo is { } geo)
            composition.Add($"Координата: {geo.Latitude:0.0000}, {geo.Longitude:0.0000}");

        return new ResourceProperties(
            KindLabel: "кампании",
            DisplayName: WorldDisplayRules.DisplayName(campaign.Definition.Name,
                campaign.Definition.FullName),
            ShortName: campaign.Definition.Name,
            Id: campaign.Definition.Id,
            Version: campaign.Definition.Version,
            Description: campaign.Definition.Description,
            ImagePath: ResolveImagePath(campaign.FolderPath, campaign.Definition.ImageFile,
                WorldPaths.CampaignImageFileName),
            ImageFileName: string.IsNullOrWhiteSpace(campaign.Definition.ImageFile)
                ? WorldPaths.CampaignImageFileName
                : campaign.Definition.ImageFile,
            ParentLabel: parentLabel,
            Metadata: campaign.Definition.Metadata,
            Composition: composition,
            FolderPath: campaign.FolderPath);
    }

    /// <summary>
    /// Строит строки «Сведения» для окна.
    ///
    /// Отсутствующие значения НЕ пропускаются молча (кроме описания): «Автор не
    /// указан» — это факт, который автору нужно знать, особенно когда он решает,
    /// перезаписывать ли ресурс при импорте.
    /// </summary>
    public static IReadOnlyList<string> BuildInfoLines(ResourceProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var lines = new List<string>
        {
            "Название: " + properties.DisplayName
        };

        if (!string.Equals(properties.ShortName, properties.DisplayName, StringComparison.Ordinal))
            lines.Add("Короткое имя: " + properties.ShortName);

        lines.Add($"Id: {properties.Id}   ·   версия: {properties.Version}");

        var created = WorldDisplayRules.Describe(properties.Metadata, modified: false);
        lines.Add(created ?? "Created by: не указан");

        var modified = WorldDisplayRules.Describe(properties.Metadata);
        if (modified is not null && modified != created)
            lines.Add(modified);

        if (properties.ParentLabel is not null)
            lines.Add(properties.ParentLabel);

        lines.AddRange(properties.Composition);

        lines.Add("Папка: " + properties.FolderPath);

        if (properties.ImagePath is not null)
            lines.Add("Изображение: " + Path.GetFileName(properties.ImagePath));
        else
            lines.Add("Изображение не задано — можно добавить в окне редактирования.");

        if (!string.IsNullOrWhiteSpace(properties.Description))
            lines.Add("Описание: " + properties.Description);

        return lines;
    }

    /// <summary>
    /// Копирует выбранное изображение в папку ресурса под каноническим именем,
    /// приводя его к КВАДРАТУ.
    ///
    /// Имя НЕ сохраняется от источника: у ресурса одно изображение, и складывать
    /// его как «IMG_2024.png» значит терять связь между файлом и ролью. Путь
    /// записывается в определение как имя файла, а не абсолютный путь, чтобы
    /// архив и папка оставались переносимыми.
    ///
    /// Квадрат обязателен: изображение показывается эскизом в списках, и
    /// прямоугольники давали «пляшущие» рамки. Обрезка идёт по центру — правило
    /// живёт в <see cref="ImageCropRules"/>, чтобы окно свойств показывало в
    /// предпросмотре РОВНО то, что будет записано.
    ///
    /// Прежняя версия копировала БАЙТЫ источника под именем `.png`. Для JPEG это
    /// давало файл с расширением PNG и содержимым JPEG: расширение врало,
    /// сторонние программы открывали его через раз, а любая проверка «это PNG?»
    /// падала. Теперь содержимое всегда настоящее PNG.
    /// </summary>
    public static string CopyImageIn(string sourceImagePath, string targetFolder, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!File.Exists(sourceImagePath))
            throw new FileNotFoundException("Файл изображения не найден.", sourceImagePath);

        var leaf = Path.GetFileName(fileName);
        if (!WorldArchiveRules.IsSafeEntryPath(leaf))
            throw new InvalidOperationException("Недопустимое имя файла изображения: " + fileName);

        Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, leaf);

        // Копирование поверх самого себя — не ошибка: «Открыть» в проводнике
        // Windows отдаёт именно этот путь.
        var samePath = Path.GetFullPath(sourceImagePath).Equals(Path.GetFullPath(target),
            StringComparison.OrdinalIgnoreCase);

        using var stream = File.OpenRead(sourceImagePath);
        using var source = Image.FromStream(stream);

        var crop = ImageCropRules.CenteredSquare(source.Width, source.Height);

        if (crop.IsNoOp && samePath)
        {
            // Файл уже квадратный и уже на месте: перекодирование только
            // потеряло бы качество, ничего не изменив.
            return leaf;
        }

        // Небольшой палитровый PNG при пересохранении теряет альфу, поэтому
        // пишем в 32-битном формате — обычный случай для тематической картинки.
        using var square = new Bitmap(crop.Side, crop.Side,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(square))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(source,
                new Rectangle(0, 0, crop.Side, crop.Side),
                new Rectangle(crop.X, crop.Y, crop.Side, crop.Side),
                GraphicsUnit.Pixel);
        }

        // Промежуточный файл рядом с целью: писать сразу в target нельзя, если
        // источник и цель совпадают — файл откроется на чтение и на запись
        // одновременно, и часть данных потеряется.
        var temporary = target + ".tmp";
        square.Save(temporary, System.Drawing.Imaging.ImageFormat.Png);

        stream.Dispose();
        File.Move(temporary, target, overwrite: true);

        return leaf;
    }

    /// <summary>
    /// Существующий файл изображения или <c>null</c>.
    ///
    /// Отсутствие файла при заданном имени — НЕ ошибка: изображение могло быть
    /// удалено в проводнике, и окно свойств должно показать описание, а не
    /// отказаться открываться.
    /// </summary>
    private static string? ResolveImagePath(string folder, string? declared, string canonical)
    {
        var candidate = string.IsNullOrWhiteSpace(declared) ? canonical : declared;
        var leaf = Path.GetFileName(candidate);
        if (string.IsNullOrWhiteSpace(leaf))
            return null;

        var path = Path.Combine(folder, leaf);
        return File.Exists(path) ? path : null;
    }
}
