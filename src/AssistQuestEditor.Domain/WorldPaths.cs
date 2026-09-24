namespace AssistQuestEditor.Domain;

/// <summary>
/// Раскладка папок мира.
///
/// Правила живут в Domain, а не в файловом сторе: они — контракт обмена между
/// игроками (импорт «просто скопировал папку — и структура не сломалась»), и их
/// нужно покрывать тестами, не поднимая файловую систему.
///
/// <code>
/// worlds/
///   &lt;Мир&gt;/
///     world.aqworld                — свойства мира
///     world.png                    — изображение мира (если задано)
///     Saves/                       — сохранения и автосохранение ЭТОГО мира
///     campaigns/
///       common/                    — общая кампания, создаётся автоматически
///         campaign.aqcampaign
///         quests/*.aqquest
///         scenes/*.aqscene
///       &lt;Кампания&gt;/
///         ...
/// </code>
/// </summary>
public static class WorldPaths
{
    public const string WorldsFolder = "worlds";
    public const string CampaignsFolder = "campaigns";
    public const string SavesFolder = "Saves";
    public const string QuestsFolder = "quests";
    public const string ScenesFolder = "scenes";
    public const string ExportedFolder = "Exported";

    public const string WorldFileName = "world.aqworld";
    public const string WorldImageFileName = "world.png";
    public const string CampaignImageFileName = "campaign.png";
    // Имя файла кампании. То же значение используется CampaignStore в слое
    // приложения, но контракт раскладки обязан быть самодостаточным: Domain не
    // может ссылаться на App.
    public const string CampaignFileName = "campaign.aqcampaign";

    /// <summary>Корень миров внутри пользовательской папки приложения.</summary>
    public static string WorldsRoot(string userRoot) =>
        Path.Combine(userRoot, WorldsFolder);

    public static string WorldFolder(string userRoot, string worldFolderName) =>
        Path.Combine(WorldsRoot(userRoot), worldFolderName);

    public static string WorldFilePath(string worldFolder) =>
        Path.Combine(worldFolder, WorldFileName);

    public static string CampaignsRoot(string worldFolder) =>
        Path.Combine(worldFolder, CampaignsFolder);

    public static string CampaignFolder(string worldFolder, string campaignFolderName) =>
        Path.Combine(CampaignsRoot(worldFolder), campaignFolderName);

    public static string CampaignFilePath(string campaignFolder) =>
        Path.Combine(campaignFolder, CampaignFileName);

    public static string QuestsFolderPath(string campaignFolder) =>
        Path.Combine(campaignFolder, QuestsFolder);

    public static string ScenesFolderPath(string campaignFolder) =>
        Path.Combine(campaignFolder, ScenesFolder);

    /// <summary>
    /// Каталог сохранений мира.
    ///
    /// Внутри мира, а не в общем каталоге приложения: состояние прохождения
    /// несовместимо между мирами (разные квесты, точки и правила), и складывать
    /// их в одну папку значило бы позволить загрузить чужой снимок.
    /// </summary>
    public static string SavesFolderPath(string worldFolder) =>
        Path.Combine(worldFolder, SavesFolder);

    /// <summary>
    /// Каталог экспорта: `Документы\Assist Quest Editor\Exported\&lt;метка времени&gt;\...`
    ///
    /// Метка времени в имени подпапки, а не один общий каталог: вложение
    /// содержит СТРУКТУРУ мира (мир/кампании/квесты), и две выгрузки подряд
    /// иначе перемешались бы.
    /// </summary>
    public static string ExportFolder(string userRoot, DateTimeOffset moment) =>
        Path.Combine(
            userRoot,
            ExportedFolder,
            moment.ToLocalTime().ToString(
                "yyyy-MM-dd_HH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture));
}
