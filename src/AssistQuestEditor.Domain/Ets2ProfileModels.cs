namespace AssistQuestEditor.Domain;

/// <summary>
/// Один подключённый к профилю компонент: мод, карта или DLC.
///
/// <see cref="Kind"/> — не украшение, а РАЗДЕЛ, в который компонент попадает в
/// окне: мод, карта и DLC показываются тремя списками (требование автора), и
/// решение о разделе принимается ЗДЕСЬ, один раз, а не разметкой страницы.
/// </summary>
public sealed record Ets2ModEntry(string Id, string Name, string Kind)
{
    /// <summary>Подпись вида для интерфейса.</summary>
    public string KindLabel => Kind switch
    {
        "map" => "Карта",
        "dlc" => "DLC",
        "rdlc" => "DLC (набор)",
        _ => "Мод"
    };
}

/// <summary>Краткая запись сохранения профиля (из <c>save/&lt;слот&gt;/info.sii</c>).</summary>
public sealed record Ets2SaveEntry(
    string Slot,
    string Name,
    DateTimeOffset? ModifiedAt,
    long InGameMinutes,
    long Money,
    int Experience,
    int VisitedCities,
    int DependencyCount)
{
    /// <summary>Игровое время слота в формате «чч:мм».</summary>
    public string InGameTimeLabel
    {
        get
        {
            var minutes = Math.Max(0, InGameMinutes);
            return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
        }
    }
}

/// <summary>
/// Физическое место, откуда прочитан профиль.
///
/// Профили Steam Cloud существуют в ДВУХ копиях: в документах игры и в хранилище
/// облака внутри <c>userdata</c> Steam. Копии расходятся — Steam обновляет свою
/// при синхронизации, игра пишет свою при запуске, — и профиль, у которого нет
/// <c>profile.sii</c> в документах, вполне может иметь его в облаке. Поэтому
/// место чтения называется явно: без этого расхождение копий выглядело бы как
/// ошибка AQE, а не как свойство облачного режима игры.
/// </summary>
public sealed record Ets2ProfileLocation(string Directory, string Source)
{
    /// <summary>Копия в папке документов игры.</summary>
    public const string Documents = "documents";

    /// <summary>Копия в хранилище Steam Cloud (<c>userdata</c>).</summary>
    public const string SteamCloud = "steam_cloud";

    /// <summary>Подпись места для интерфейса.</summary>
    public string SourceLabel => Label(Source);

    /// <summary>
    /// Подпись места для интерфейса по коду места.
    ///
    /// Правило живёт ЗДЕСЬ, а не у каждой записи отдельно: место чтения
    /// показывается и в списке профилей, и в карточке профиля, и две копии
    /// правила разошлись бы при добавлении третьего места хранения.
    /// </summary>
    public static string Label(string source)
        => source == SteamCloud ? "облако Steam" : "документы игры";
}

/// <summary>Запись профиля в списке выбора (без чтения самого профиля).</summary>
public sealed record Ets2ProfileSummary(
    string Area,
    string HexFolder,
    string Name,
    int SaveCount,
    DateTimeOffset? LastWrite,
    bool IsActive,
    string Source = Ets2ProfileLocation.Documents)
{
    /// <summary>Подпись места чтения для интерфейса.</summary>
    public string SourceLabel => Ets2ProfileLocation.Label(Source);
}

/// <summary>Полные данные профиля, показанные в окне.</summary>
public sealed record Ets2ProfileData(
    string Area,
    string HexFolder,
    string Name,
    string? ProfileName,
    string? Language,
    string? LanguageLabel,
    string? Currency,
    string? CurrencyLabel,
    string? CompanyName,
    string? Brand,
    string? Truck,
    string? MapPath,
    int? FaceIndex,
    bool? Male,
    long DistanceKm,
    long Experience,
    int SaveCount,
    DateTimeOffset? ProfileCreated,
    DateTimeOffset? ProfileSaved,
    string? AvatarPath,
    IReadOnlyList<Ets2ModEntry> Mods,
    IReadOnlyList<Ets2ModEntry> Maps,
    IReadOnlyList<Ets2ModEntry> Dlc,
    IReadOnlyList<Ets2SaveEntry> Saves,
    IReadOnlyList<string> Warnings,
    string Source = Ets2ProfileLocation.Documents)
{
    /// <summary>Подпись места чтения данных для интерфейса.</summary>
    public string SourceLabel => Ets2ProfileLocation.Label(Source);
}

/// <summary>Каталог профилей: что нашлось на диске и какой профиль сейчас в игре.</summary>
public sealed record Ets2ProfileCatalog(
    string? GameRoot,
    string? SteamRoot,
    string? ActiveHexFolder,
    string? ActiveProfileName,
    IReadOnlyList<Ets2ProfileSummary> Profiles,
    IReadOnlyList<string> Warnings,
    string? SteamCloudRoot = null)
{
    /// <summary>Профили найдены хотя бы в одном месте.</summary>
    public bool Found => Profiles.Count > 0;
}

/// <summary>
/// Отнесение мода к разделу «карта».
///
/// Правило вынесено отдельно от чтения файлов, потому что это ЗНАНИЕ О ПРЕДМЕТЕ,
/// а не о формате: набор признаков карт меняется вместе с модами сообщества, и
/// его правят здесь, не трогая разбор SII. Прятать это в разметку страницы нельзя —
/// тогда один и тот же мод попадал бы в «карты» или «моды» по-разному в разных
/// местах интерфейса.
/// </summary>
public static class Ets2MapModClassifier
{
    /// <summary>Признаки картографических модификаций в id или названии.</summary>
    private static readonly string[] MapMarkers =
    {
        "map", "promods", "rusmap", "sibirmap", "srmap", "roex", "roextended",
        "kazakhstan", "southern region", "southern_region", "great steppe",
        "great_steppe", "volga", "caucasus", "middle east", "middle_east",
        "afroeurasia", "red sea", "redsea", "eaa", "north map", "northmap"
    };

    /// <summary>
    /// Карта ли это. Проверяются и id, и человекочитаемое имя: у локальных модов
    /// id — имя файла (<c>sibirmap13_sv_for160</c>), а у мастерских — числовой
    /// идентификатор, и признак остаётся только в названии.
    /// </summary>
    public static bool IsMap(string id, string name)
    {
        foreach (var marker in MapMarkers)
        {
            if (id.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Разбор строк компонентов профиля.
///
/// Формат приходит из игры и НЕ нормализован: у <c>active_mods</c> это
/// <c>"источник|имя"</c>, у <c>dependencies</c> сохранения — <c>"вид|код|название"</c>.
/// Разбирать его в двух местах (здесь и в разметке) значило бы однажды получить
/// разное число компонентов на странице и в списке.
/// </summary>
public static class Ets2ComponentParser
{
    /// <summary>
    /// Разбирает строку компонента. Возвращает <c>null</c> для пустых строк:
    /// пустая строка в массиве означает «место ещё не занято», а не компонент.
    /// </summary>
    public static Ets2ModEntry? Parse(string raw, string fallbackKind)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split('|');
        if (parts.Length >= 3)
        {
            // «вид|код|название» — из dependencies сохранения.
            var kind = NormalizeKind(parts[0], fallbackKind);
            var code = parts[1].Trim();
            var name = string.Join('|', parts[2..]).Trim();
            if (name.Length == 0)
                name = code;
            return new Ets2ModEntry(code, name, kind);
        }

        if (parts.Length == 2)
        {
            var id = parts[0].Trim();
            var name = parts[1].Trim();
            if (name.Length == 0)
                name = id;
            var kind = Ets2MapModClassifier.IsMap(id, name) ? "map" : fallbackKind;
            return new Ets2ModEntry(id, name, kind);
        }

        var single = raw.Trim();
        return new Ets2ModEntry(single, single,
            Ets2MapModClassifier.IsMap(single, single) ? "map" : fallbackKind);
    }

    private static string NormalizeKind(string rawKind, string fallback) => rawKind.Trim().ToLowerInvariant() switch
    {
        "mod" => "mod",
        "map" => "map",
        "dlc" => "dlc",
        "rdlc" => "rdlc",
        _ => fallback
    };
}
