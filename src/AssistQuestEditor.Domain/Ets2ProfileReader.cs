using System.Globalization;
using Microsoft.Win32;

namespace AssistQuestEditor.Domain;

/// <summary>
/// Чтение профилей Euro Truck Simulator 2 с диска.
///
/// Читающий, а не пишущий: AQE показывает профили игры как СПРАВОЧНИК, и ни одна
/// операция здесь не изменяет файлы игры. Это принципиально — Steam Cloud следит
/// за папкой профилей, и правка в ней из чужого приложения выглядела бы как
/// расхождение синхронизации.
///
/// ТРИ места хранения профилей, в порядке предпочтения:
///   * <c>&lt;Steam&gt;/userdata/&lt;аккаунт&gt;/227300/remote/profiles/&lt;hex&gt;</c> —
///     облачная копия, самая свежая при включённом Steam Cloud;
///   * <c>steam_profiles/&lt;hex&gt;</c> в документах игры — локальная копия
///     облачных профилей, которую игра пишет при запуске;
///   * <c>profiles/&lt;hex&gt;</c> — прежние локальные профили.
///
/// ПОЧЕМУ ОБЛАКО ЧИТАЕТСЯ ПЕРВЫМ. Профили в документах и в <c>userdata</c> — это
/// ДВЕ РАЗНЫЕ КОПИИ одного профиля, и они расходятся: у профиля «Test2» игра
/// держала настройки в облаке (13 сохранений, 1 активный мод), а в документах
/// оставила только раскладки клавиш — без <c>profile.sii</c> и без сохранений.
/// Прежнее чтение только документов показывало такой профиль пустым, хотя игра
/// прекрасно знала его мод: данные лежали в соседней копии.
///
/// Имя папки — hex-кодировка ASCII-имени профиля, поэтому имена восстанавливаются
/// из имён каталогов, а настоящее имя уточняется из <c>profile.sii</c>.
///
/// Язык интерфейса лежит в <c>&lt;профиль&gt;/config.cfg</c> (поле <c>g_lang</c>).
/// </summary>
public sealed class Ets2ProfileReader
{
    /// <summary>Языки интерфейса игры: код -> понятное название.</summary>
    private static readonly Dictionary<string, string> LanguageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru_ru"] = "Русский", ["en_us"] = "English (US)", ["en_gb"] = "English (UK)",
        ["de_de"] = "Deutsch", ["fr_fr"] = "Français", ["it_it"] = "Italiano",
        ["es_es"] = "Español", ["es_la"] = "Español (LatAm)", ["pt_br"] = "Português (BR)",
        ["pl_pl"] = "Polski", ["cs_cz"] = "Čeština", ["sk_sk"] = "Slovenčina",
        ["hu_hu"] = "Magyar", ["ro_ro"] = "Română", ["tr_tr"] = "Türkçe",
        ["nl_nl"] = "Nederlands", ["da_dk"] = "Dansk", ["sv_se"] = "Svenska",
        ["nb_no"] = "Norsk", ["fi_fi"] = "Suomi", ["et_ee"] = "Eesti",
        ["lv_lv"] = "Latviešu", ["lt_lt"] = "Lietuvių", ["uk_ua"] = "Українська",
        ["bg_bg"] = "Български", ["el_gr"] = "Ελληνικά", ["sr_sp"] = "Srpski",
        ["hr_hr"] = "Hrvatski", ["sl_si"] = "Slovenščina", ["ja_jp"] = "日本語",
        ["ko_kr"] = "한국어", ["zh_cn"] = "中文（简体）", ["zh_tw"] = "中文（繁體）",
        ["th_th"] = "ไทย", ["vi_vn"] = "Tiếng Việt", ["id_id"] = "Bahasa Indonesia",
        ["ar_sa"] = "العربية", ["he_il"] = "עברית", ["fa_ir"] = "فارسی",
        ["hi_in"] = "हिन्दी", ["cat"] = "Català", ["eu_es"] = "Euskara"
    };

    /// <summary>Денежные единицы: код <c>g_currency</c> -> название.</summary>
    private static readonly Dictionary<string, string> CurrencyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "€ (евро)", ["1"] = "£ (фунт)", ["2"] = "$ (доллар)", ["3"] = "¥ (иена)",
        ["4"] = "Kč (крона)", ["5"] = "zł (злотый)", ["6"] = "руб.",
        ["7"] = "kr (крона)", ["8"] = "Ft (форинт)", ["9"] = "лв (лев)",
        ["10"] = "kn (куна)", ["11"] = "R$ (реал)", ["12"] = "₺ (лира)",
        ["13"] = "₽ (рубль)"
    };

    private readonly string? _gameRoot;
    private readonly string? _steamRoot;
    private readonly string? _steamCloudRoot;
    private readonly string? _steamAccountId;

    public Ets2ProfileReader(string? gameRoot = null, string? steamRoot = null)
    {
        _gameRoot = string.IsNullOrWhiteSpace(gameRoot) ? DetectGameRoot() : gameRoot;
        _steamRoot = string.IsNullOrWhiteSpace(steamRoot) ? DetectSteamRoot() : steamRoot;
        _steamAccountId = ReadCurrentSteamAccountId(_steamRoot ?? string.Empty);
        _steamCloudRoot = DetectSteamCloudRoot(_steamRoot);
    }

    /// <summary>Папка документов игры или null, если игра не найдена.</summary>
    public string? GameRoot => _gameRoot;

    /// <summary>Корень Steam или null.</summary>
    public string? SteamRoot => _steamRoot;

    /// <summary>Текущий аккаунт Steam, если он удалось определить.</summary>
    public string? SteamAccountId => _steamAccountId;

    /// <summary>
    /// Папка облачного хранилища профилей ETS2 внутри Steam или null.
    ///
    /// Именно каталог с профилями, а не корень <c>userdata</c>: показывать в окне
    /// голый <c>userdata</c> значило бы отправлять пользователя искать аккаунт сам,
    /// а он уже найден.
    /// </summary>
    public string? SteamCloudRoot => _steamCloudRoot;

    /// <summary>
    /// Находит папку документов ETS2.
    ///
    /// Сначала Документы текущего пользователя, затем прежние жёстко заданные
    /// пути: на этой машине документы лежат на диске <c>E:</c>, и одного
    /// <c>MyDocuments</c> мало — путь известной папки может быть перенаправлен.
    /// </summary>
    public static string? DetectGameRoot()
    {
        var candidates = new List<string>();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
            candidates.Add(Path.Combine(documents, "Euro Truck Simulator 2"));

        candidates.Add(@"E:\Users\Docs\Euro Truck Simulator 2");
        candidates.Add(@"C:\Users\Public\Documents\Euro Truck Simulator 2");

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Находит корень Steam: реестр, затем типовые каталоги.</summary>
    public static string? DetectSteamRoot()
    {
        // Реестр существует только в Windows, а домен собирается под общую
        // платформу: без этой проверки анализатор платформы справедливо ругается,
        // что вызов доступен не везде.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string fromRegistry
                    && Directory.Exists(fromRegistry))
                    return fromRegistry;

                if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string machine
                    && Directory.Exists(machine))
                    return machine;
            }
            catch (Exception)
            {
                // Реестр может быть недоступен (политики, урезанный профиль) — это не
                // повод не показать профили: ниже есть проверка типовых каталогов.
            }
        }

        foreach (var candidate in new[] { @"E:\Steam", @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Идентификатор игры Euro Truck Simulator 2 в Steam.</summary>
    public const string SteamAppId = "227300";

    /// <summary>
    /// Находит папку облачного хранилища профилей ETS2.
    ///
    /// Берётся <c>&lt;userdata&gt;/&lt;аккаунт&gt;/227300/remote/profiles</c>, а не
    /// каталог игры целиком: рядом с профилями в <c>remote</c> лежат кеш Steam и
    /// другие файлы, и перечисление каталога игры целиком выдавало бы их за
    /// профили.
    ///
    /// У одного Steam бывает НЕСКОЛЬКО аккаунтов (<c>userdata/&lt;id&gt;</c>), и
    /// профили ETS2 могут быть не у каждого. Порядок выбора:
    ///   1. аккаунт, помеченный в <c>loginusers.vdf</c> как текущий
    ///      (<c>AutoLogin 1</c> или свежайшая метка входа) — это тот, под которым
    ///      пользователь играет;
    ///   2. иначе самый свежий каталог <c>227300</c> по времени изменения.
    ///
    /// Угадывать по имени аккаунта нельзя: оно ничего не говорит о ETS2. Поэтому
    /// отсутствие каталога — нормальный ответ, и профили тогда читаются только из
    /// документов игры.
    /// </summary>
    public static string? DetectSteamCloudRoot(string? steamRoot)
    {
        if (string.IsNullOrWhiteSpace(steamRoot))
            return null;

        var userData = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userData))
            return null;

        var preferred = ReadCurrentSteamAccountId(steamRoot);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var directory = CloudProfilesPath(userData, preferred);
            if (Directory.Exists(directory))
                return directory;
        }

        try
        {
            return Directory
                .EnumerateDirectories(userData)
                .Select(account => CloudProfilesPath(userData, Path.GetFileName(account)))
                .Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Путь к папке профилей в облачном хранилище аккаунта.</summary>
    private static string CloudProfilesPath(string userData, string accountId)
        => Path.Combine(userData, accountId, SteamAppId, "remote", "profiles");

    /// <summary>
    /// Идентификатор текущего аккаунта Steam из <c>config/loginusers.vdf</c>.
    ///
    /// Файл — разбор Valve Data Format, и полноценный его разборщик здесь не нужен:
    /// нужен только список аккаунтов с признаком <c>AutoLogin 1</c> и меткой
    /// <c>Timestamp</c>. Побеждает аккаунт с автовходом; если автовхода нет ни у
    /// кого — тот, чья метка входа свежее (она обновляется при каждом входе).
    /// Ничья решается именем каталога, чтобы выбор был определён.
    /// </summary>
    private static string? ReadCurrentSteamAccountId(string steamRoot)
    {
        var path = Path.Combine(steamRoot, "config", "loginusers.vdf");
        if (!File.Exists(path))
            return null;

        try
        {
            var accounts = new List<(string Id, bool AutoLogin, long Timestamp)>();
            string? openId = null;
            var autoLogin = false;
            long timestamp = 0;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;

                // Блок аккаунта открывается строкой из одного числового ключа.
                if (AccountBlockHeader(line) is { } id)
                {
                    // Итог предыдущего блока подводится ПЕРЕД началом нового.
                    if (openId is not null)
                        accounts.Add((openId, autoLogin, timestamp));

                    openId = id;
                    autoLogin = false;
                    timestamp = 0;
                    continue;
                }

                if (openId is null || VdfPair(line) is not { } pair)
                    continue;

                if (pair.Key.Equals("AutoLogin", StringComparison.OrdinalIgnoreCase))
                    autoLogin = pair.Value == "1";
                else if (pair.Key.Equals("Timestamp", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(pair.Value, out var parsed))
                    timestamp = parsed;
            }

            if (openId is not null)
                accounts.Add((openId, autoLogin, timestamp));

            return accounts
                .OrderByDescending(account => account.AutoLogin)
                .ThenByDescending(account => account.Timestamp)
                .ThenByDescending(account => account.Id, StringComparer.Ordinal)
                .Select(account => account.Id)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>«"76561198000801794"» → идентификатор аккаунта; иначе null.</summary>
    private static string? AccountBlockHeader(string line)
    {
        if (line.Length < 4 || line[0] != '"' || line[^1] != '"')
            return null;

        var value = line[1..^1];
        return value.Length is >= 4 and <= 20 && value.All(char.IsAsciiDigit) ? value : null;
    }

    /// <summary>«"AutoLogin"   "1"» → пара ключ/значение; иначе null.</summary>
    private static (string Key, string Value)? VdfPair(string line)
    {
        if (line.Length == 0 || line[0] != '"')
            return null;

        var close = line.IndexOf('"', 1);
        if (close < 0)
            return null;

        var key = line[1..close];
        var rest = line[(close + 1)..].TrimStart();
        if (rest.Length < 2 || rest[0] != '"')
            return null;

        var end = rest.IndexOf('"', 1);
        return end < 0 ? null : (key, rest[1..end]);
    }

    /// <summary>
    /// Перечисляет профили во всех местах хранения. Один профиль — ОДНА запись.
    ///
    /// Профиль, лежащий и в облаке, и в документах, показывается один раз: место
    /// записи выбирается по свежести данных (сначала облако, потому что Steam
    /// синхронизирует в него, а игра пишет локальную копию при запуске). Прежнее
    /// перечисление давало ДВА пункта с одним именем, и выбор открывал то одну
    /// копию, то другую — по одному и тому же профилю окно показывало то мод, то
    /// «нет данных», в зависимости от того, какой пункт нажали.
    /// </summary>
    public Ets2ProfileCatalog ReadCatalog()
    {
        var warnings = new List<string>();
        var profiles = new List<Ets2ProfileSummary>();

        if (_gameRoot is null || !Directory.Exists(_gameRoot))
            warnings.Add("Папка документов Euro Truck Simulator 2 не найдена.");

        var activeHex = DetectActiveHexFolder(warnings, out var activeName);

        if (_steamCloudRoot is not null && Directory.Exists(_steamCloudRoot))
        {
            CollectArea(
                "steam_profiles", _steamCloudRoot, Ets2ProfileLocation.SteamCloud,
                activeHex, profiles, warnings);
        }

        if (_gameRoot is not null && Directory.Exists(_gameRoot))
        {
            CollectArea("steam_profiles", Path.Combine(_gameRoot, "steam_profiles"),
                Ets2ProfileLocation.Documents, activeHex, profiles, warnings);
            CollectArea("profiles", Path.Combine(_gameRoot, "profiles"),
                Ets2ProfileLocation.Documents, activeHex, profiles, warnings);
        }

        if (profiles.Count == 0)
        {
            warnings.Add(_steamCloudRoot is null
                ? "Профили не найдены. Steam Cloud этой игры хранится в папке userdata аккаунта Steam."
                : "Профили не найдены ни в документах игры, ни в облаке Steam.");
        }

        return new Ets2ProfileCatalog(
            _gameRoot,
            _steamRoot,
            activeHex,
            activeName,
            profiles,
            warnings,
            _steamCloudRoot);
    }

    private static void CollectArea(
        string area,
        string areaPath,
        string source,
        string? activeHex,
        List<Ets2ProfileSummary> profiles,
        List<string> warnings)
    {
        if (!Directory.Exists(areaPath))
            return;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(areaPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            warnings.Add($"Не удалось прочитать {areaPath}: {ex.Message}");
            return;
        }

        foreach (var directory in directories)
        {
            var hex = Path.GetFileName(directory);

            // Копия уже показана другим местом: облако читается первым, поэтому
            // здесь остаётся менее свежая копия, и подменять ею найденную нельзя.
            if (profiles.Any(item => item.HexFolder.Equals(hex, StringComparison.OrdinalIgnoreCase)))
                continue;

            var saveCount = CountSaves(directory);

            DateTimeOffset? lastWrite = null;
            try
            {
                lastWrite = Directory.GetLastWriteTimeUtc(directory);
            }
            catch (Exception)
            {
                // Время каталога — необязательное украшение списка: его отсутствие
                // не повод прятать сам профиль.
            }

            profiles.Add(new Ets2ProfileSummary(
                area,
                hex,
                DecodeHexName(hex),
                saveCount,
                lastWrite,
                string.Equals(hex, activeHex, StringComparison.OrdinalIgnoreCase),
                source));
        }
    }

    /// <summary>Число слотов сохранения в профиле.</summary>
    private static int CountSaves(string profileDirectory)
    {
        var saveRoot = Path.Combine(profileDirectory, "save");
        if (!Directory.Exists(saveRoot))
            return 0;

        try
        {
            return Directory.EnumerateDirectories(saveRoot).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Определяет активный профиль по журналу запуска (<c>game.log.txt</c>).
    ///
    /// Журнал — единственный источник, меняющийся при СМЕНЕ профиля: файлы
    /// каталогов меняются в разное время и не говорят, какой профиль открыт
    /// сейчас.
    ///
    /// Сканирование идёт С КОНЦА: последняя запись о профиле в журнале — это и
    /// есть профиль текущего запуска. Каталог берётся из строк вида
    /// <c>File /home/steam_profiles/&lt;hex&gt;/...</c>, а имя — из
    /// <c>Set profile finished: 'имя'</c>.
    ///
    /// ⚠ Прежнее чтение искало СТРОКУ <c>"(steam_profiles|profiles)/"</c> как
    /// регулярное выражение, но вызывало <c>IndexOf</c>, который ищет буквально:
    /// такие символы в журнале не встречаются, поэтому активный профиль не
    /// находился НИКОГДА, и ни один профиль не помечался как открытый в игре.
    /// </summary>
    private string? DetectActiveHexFolder(List<string> warnings, out string? activeName)
    {
        activeName = null;

        if (_gameRoot is null)
            return null;

        var logPath = Path.Combine(_gameRoot, "game.log.txt");
        if (!File.Exists(logPath))
            return null;

        try
        {
            var lines = Ets2GameLog.ReadLines(logPath);

            // Активность профиля определяется не последним случайным путём
            // profiles/... в хвосте журнала, а последним событием выбора профиля.
            // Между каталогом профиля и таким событием ETS2 обычно пишет несколько
            // технических строк, поэтому ищем ближайший путь только в пределах
            // одного окна переключения.
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                var isProfileEvent =
                    line.Contains("New profile selected:", StringComparison.Ordinal) ||
                    line.Contains(ProfileFinishedMarker, StringComparison.Ordinal);

                if (!isProfileEvent)
                    continue;

                if (line.Contains("New profile selected:", StringComparison.Ordinal))
                {
                    var marker = "New profile selected:";
                    activeName = ExtractQuoted(
                        line[line.IndexOf(marker, StringComparison.Ordinal)..]);
                }
                else
                {
                    activeName = ExtractQuoted(
                        line[line.IndexOf(ProfileFinishedMarker, StringComparison.Ordinal)..]);
                }

                var from = Math.Max(0, i - 120);
                for (var j = i; j >= from; j--)
                {
                    if (TryExtractProfileHex(lines[j], out var hex))
                        return hex;
                }

                // Иногда путь в журнале появляется сразу после события выбора.
                // Ограниченный поиск вперёд не позволяет случайно захватить старый
                // профиль из другой игровой сессии.
                var to = Math.Min(lines.Length, i + 40);
                for (var j = i + 1; j < to; j++)
                {
                    if (TryExtractProfileHex(lines[j], out var hex))
                        return hex;
                }
            }

            // Запасной путь для старых/урезанных журналов без событий выбора.
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (TryExtractProfileHex(lines[i], out var hex))
                    return hex;
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Не удалось прочитать game.log.txt: " + ex.Message);
        }

        return null;
    }

    private const string ProfileFinishedMarker = "Set profile finished:";

    /// <summary>
    /// Достаёт hex-имя папки профиля из строки журнала.
    ///
    /// Проверяются оба места хранения, причём <c>steam_profiles/</c> — ПЕРВЫМ:
    /// подстрока <c>profiles/</c> входит в него целиком, и обратный порядок
    /// вернул бы «steam_» вместо профиля.
    /// </summary>
    private static bool TryExtractProfileHex(string line, out string hex)
    {
        hex = string.Empty;

        foreach (var marker in new[] { "steam_profiles/", "profiles/" })
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                continue;

            // Для «profiles/» проверяется, что это не хвост «steam_profiles/»:
            // иначе маркер сработал бы внутри уже разобранного пути.
            if (marker == "profiles/" && index >= 6
                && line.AsSpan(index - 6, 6).Equals("steam_", StringComparison.OrdinalIgnoreCase))
                continue;

            var tail = line[(index + marker.Length)..];
            var slash = tail.IndexOf('/');
            var candidate = slash > 0 ? tail[..slash] : tail.Trim();
            if (candidate.Length >= 2 && candidate.All(Uri.IsHexDigit))
            {
                hex = candidate;
                return true;
            }
        }

        return false;
    }

    private static string? ExtractQuoted(string line)
    {
        var start = line.IndexOf('\'');
        if (start < 0)
            return null;

        var end = line.IndexOf('\'', start + 1);
        return end > start ? line[(start + 1)..end] : null;
    }

    /// <summary>
    /// Читает полные данные профиля.
    ///
    /// Сначала берётся облачная копия, если она есть: Steam держит в ней самую
    /// свежую версию профиля, а локальная копия в документах может остаться от
    /// прежнего запуска. Именно это расхождение давало профиль без данных: у
    /// «Test2» в документах нет ни <c>profile.sii</c>, ни сохранений, а в облаке
    /// лежат и настройки (1 активный мод), и 13 сохранений.
    ///
    /// Ни одно поле не обязательно: у профиля без сохранений может не быть ни
    /// <c>profile.sii</c>, ни <c>config.cfg</c>. Тогда поле остаётся пустым, а
    /// причина попадает в <see cref="Ets2ProfileData.Warnings"/> — окно обязано
    /// показать «нет данных», а не выдуманное значение.
    /// </summary>
    public Ets2ProfileData ReadProfile(string area, string hexFolder)
    {
        var warnings = new List<string>();
        var directory = ResolveProfileDirectory(area, hexFolder, warnings);

        if (directory is null)
            throw new DirectoryNotFoundException(
                $"Папка профиля не найдена: {area}/{hexFolder}");

        var source = IsInside(_steamCloudRoot, directory)
            ? Ets2ProfileLocation.SteamCloud
            : Ets2ProfileLocation.Documents;

        string? profileName = null;
        string? companyName = null;
        string? brand = null;
        string? truck = null;
        string? mapPath = null;
        int? faceIndex = null;
        bool? male = null;
        var distance = 0L;
        var experience = 0L;
        DateTimeOffset? created = null;
        DateTimeOffset? saved = null;

        var mods = new List<Ets2ModEntry>();
        var maps = new List<Ets2ModEntry>();
        var dlc = new List<Ets2ModEntry>();

        var profilePath = Path.Combine(directory, "profile.sii");
        if (File.Exists(profilePath))
        {
            try
            {
                var document = Ets2SiiDocument.Parse(Ets2Sii.ReadText(profilePath));
                const string block = "user_profile";

                profileName = document.GetString(block, "profile_name");
                companyName = document.GetString(block, "company_name");
                brand = document.GetString(block, "brand");
                mapPath = document.GetString(block, "map_path");
                faceIndex = document.GetString(block, "face") is { } face && int.TryParse(face, out var faceValue)
                    ? faceValue
                    : null;
                male = document.HasBlock(block) ? document.GetBool(block, "male") : null;
                distance = document.GetInt64(block, "cached_distance");
                experience = document.GetInt64(block, "cached_experience");
                created = ParseUnixTime(document.GetInt64(block, "creation_time"));
                saved = ParseUnixTime(document.GetInt64(block, "save_time"));

                var truckId = document.GetString(block, "user_data");
                var dataValues = document.GetValues(block, "user_data");
                if (dataValues.Count > 11 && dataValues[11] is { Length: > 0 } truckCode)
                    truckId = truckCode;
                truck = truckId;

                foreach (var value in document.GetValues(block, "active_mods"))
                {
                    var entry = Ets2ComponentParser.Parse(value, "mod");
                    if (entry is null)
                        continue;

                    (entry.Kind == "map" ? maps : mods).Add(entry);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Не удалось прочитать profile.sii: " + ex.Message);
            }
        }
        else
        {
            warnings.Add("В профиле нет profile.sii: имя и статистика недоступны.");
        }

        // Активное имя: profile.sii содержит его всегда, но если файла нет —
        // показываем восстановленное из имени папки, а не пустоту.
        profileName = string.IsNullOrWhiteSpace(profileName) ? DecodeHexName(hexFolder) : profileName;

        var (language, currency) = ReadLanguageAndCurrency(directory, warnings);

        var avatar = ReadAvatar(directory);
        if (avatar is null)
            warnings.Add("Аватар профиля (online_avatar.png) не найден.");

        var saves = ReadSaves(directory, warnings);

        // Компоненты слота — самый полный список подключённого: DLC видны только
        // здесь, а карты попадают в общий раздел. Читается dependencies только у
        // самого свежего слота, иначе пришлось бы разбирать десятки файлов.
        MergeDlcFromSaves(directory, saves, dlc, maps, warnings);

        return new Ets2ProfileData(
            area, hexFolder, DecodeHexName(hexFolder), profileName,
            language, LanguageLabels.TryGetValue(language ?? string.Empty, out var languageLabel) ? languageLabel : null,
            currency, CurrencyLabels.TryGetValue(currency ?? string.Empty, out var currencyLabel) ? currencyLabel : null,
            companyName, brand, truck, mapPath, faceIndex, male,
            distance, experience, saves.Count, created, saved, avatar,
            mods, maps, dlc, saves, warnings, source);
    }

    /// <summary>
    /// Находит папку профиля: облачная копия предпочитается локальной.
    ///
    /// Каталог выбирается по НАЛИЧИЮ данных, а не по одному существованию папки:
    /// локальная копия облачного профиля может содержать только раскладки клавиш —
    /// папка есть, а профиля в ней нет. Пустая локальная копия пропускается в
    /// пользу облачной, и об этом говорится в предупреждениях.
    /// </summary>
    private string? ResolveProfileDirectory(string area, string hexFolder, List<string> warnings)
    {
        var cloud = _steamCloudRoot is null
            ? null
            : Path.Combine(_steamCloudRoot, hexFolder);

        if (cloud is not null && Directory.Exists(cloud) && HasProfileData(cloud))
            return cloud;

        var local = _gameRoot is null ? null : Path.Combine(_gameRoot, area, hexFolder);

        if (local is not null && Directory.Exists(local))
        {
            if (!HasProfileData(local) && cloud is not null && Directory.Exists(cloud))
            {
                warnings.Add(
                    "Локальная копия профиля пуста (нет profile.sii и сохранений); " +
                    "данные прочитаны из облака Steam.");
                return cloud;
            }

            return local;
        }

        return null;
    }

    /// <summary>Есть ли в папке сам профиль, а не только его настройки.</summary>
    private static bool HasProfileData(string directory)
        => File.Exists(Path.Combine(directory, "profile.sii"))
        || Directory.Exists(Path.Combine(directory, "save"));

    /// <summary>
    /// Внутри ли каталог указанного корня.
    ///
    /// Сравнение идёт по разделителю в конце: без него «...\profiles» считался бы
    /// родителем «...\profiles2», и место чтения определялось бы неверно.
    /// </summary>
    private static bool IsInside(string? root, string directory)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return Path.TrimEndingDirectorySeparator(directory)
            .StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Язык интерфейса и валюта.
    ///
    /// Сначала <c>config.cfg</c> профиля (это его НАСТРОЙКИ), затем общий
    /// <c>config.cfg</c> игры, затем журнал запуска. Другого порядка быть не может:
    /// общий файл — запасной источник, а не основной.
    /// </summary>
    private (string? Language, string? Currency) ReadLanguageAndCurrency(string directory, List<string> warnings)
    {
        var profileConfig = Path.Combine(directory, "config.cfg");
        if (File.Exists(profileConfig))
        {
            // config.cfg — не SII, а поток строк `uset имя "значение"`.
            var (language, currency) = ReadUsets(profileConfig);
            if (language is not null || currency is not null)
                return (language, currency);
        }

        if (_gameRoot is not null)
        {
            // Корневой config.cfg — ЗАПАСНОЙ источник: он общий для всех профилей
            // и обновляется при запуске игры. Если он есть, но язык в нём не
            // записан, читается журнал запуска.
            var rootConfig = Path.Combine(_gameRoot, "config.cfg");
            if (File.Exists(rootConfig))
            {
                var (language, currency) = ReadUsets(rootConfig);
                if (language is not null || currency is not null)
                    return (language, currency);
            }
        }

        // Общий каталог облачного хранилища: у игры, работающей в облачном режиме,
        // своя копия config.cfg живёт рядом с профилями, и в документах её может
        // не быть вовсе. Читается ПОСЛЕ корневого файла игры и ПЕРЕД журналом:
        // это тоже config.cfg профилей, только из облачной копии.
        if (_steamCloudRoot is not null)
        {
            var cloudConfig = Path.Combine(_steamCloudRoot, "config.cfg");
            if (File.Exists(cloudConfig))
            {
                var (language, currency) = ReadUsets(cloudConfig);
                if (language is not null || currency is not null)
                    return (language, currency);
            }
        }

        if (_gameRoot is not null)
        {
            var logPath = Path.Combine(_gameRoot, "game.log.txt");
            if (File.Exists(logPath))
            {
                try
                {
                    foreach (var line in Ets2GameLog.ReadLines(logPath))
                    {
                        const string marker = "Selected language:";
                        var index = line.IndexOf(marker, StringComparison.Ordinal);
                        if (index < 0)
                            continue;

                        var value = line[(index + marker.Length)..].Trim();
                        if (value.Length > 0)
                            return (value, null);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("Не удалось прочитать язык из game.log.txt: " + ex.Message);
                }
            }
        }

        return (null, null);
    }

    /// <summary>Разбирает поток <c>uset имя "значение"</c> из config.cfg.</summary>
    private static (string? Language, string? Currency) ReadUsets(string path)
    {
        string? language = null;
        string? currency = null;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("uset ", StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = trimmed[5..].TrimStart();
            var space = rest.IndexOf(' ');
            if (space <= 0)
                continue;

            var name = rest[..space];
            var value = rest[(space + 1)..].Trim().Trim('"');

            if (name.Equals("g_lang", StringComparison.OrdinalIgnoreCase))
                language = value;
            else if (name.Equals("g_currency", StringComparison.OrdinalIgnoreCase))
                currency = value;
        }

        return (language, currency);
    }

    /// <summary>Путь к аватару профиля, если он есть.</summary>
    private static string? ReadAvatar(string directory)
    {
        var avatar = Path.Combine(directory, "online_avatar.png");
        return File.Exists(avatar) ? avatar : null;
    }

    /// <summary>
    /// Читает список сохранений.
    ///
    /// Слот без <c>info.sii</c> пропускается: имя и статистика берутся ТОЛЬКО из
    /// него, и показывать слот с выдуманным именем хуже, чем не показывать вовсе.
    /// </summary>
    private static List<Ets2SaveEntry> ReadSaves(string profileDirectory, List<string> warnings)
    {
        var result = new List<Ets2SaveEntry>();
        var saveRoot = Path.Combine(profileDirectory, "save");
        if (!Directory.Exists(saveRoot))
            return result;

        foreach (var slotDirectory in Directory.EnumerateDirectories(saveRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var infoPath = Path.Combine(slotDirectory, "info.sii");
            if (!File.Exists(infoPath))
                continue;

            try
            {
                var document = Ets2SiiDocument.Parse(Ets2Sii.ReadText(infoPath));
                const string block = "save_container";

                DateTimeOffset? modified = null;
                var fileTime = document.GetInt64(block, "file_time");
                if (fileTime > 0)
                    modified = ParseUnixTime(fileTime);

                result.Add(new Ets2SaveEntry(
                    Path.GetFileName(slotDirectory),
                    document.GetString(block, "name") ?? string.Empty,
                    modified,
                    document.GetInt64(block, "time"),
                    document.GetInt64(block, "info_money_account"),
                    (int)document.GetInt64(block, "info_players_experience"),
                    (int)document.GetInt64(block, "info_visited_cities"),
                    document.GetValues(block, "dependencies").Count));
            }
            catch (Exception ex)
            {
                warnings.Add($"Сохранение «{Path.GetFileName(slotDirectory)}» не прочитано: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Собирает DLC из <c>dependencies</c> самого свежего сохранения.
    ///
    /// Именно сохранение, а не профиль: список DLC в профиле НЕ хранится, а
    /// <c>dependencies</c> слота — это ровно тот набор, с которым слот загрузится.
    /// Поэтому источник назван явно, чтобы строка в окне не выглядела выдуманной.
    /// </summary>
    private void MergeDlcFromSaves(
        string profileDirectory,
        List<Ets2SaveEntry> saves,
        List<Ets2ModEntry> dlc,
        List<Ets2ModEntry> maps,
        List<string> warnings)
    {
        if (saves.Count == 0)
            return;

        var saveRoot = Path.Combine(profileDirectory, "save");
        var newest = saves
            .OrderByDescending(save => save.ModifiedAt ?? DateTimeOffset.MinValue)
            .First();

        var infoPath = Path.Combine(saveRoot, newest.Slot, "info.sii");
        if (!File.Exists(infoPath))
            return;

        try
        {
            var document = Ets2SiiDocument.Parse(Ets2Sii.ReadText(infoPath));
            foreach (var value in document.GetValues("save_container", "dependencies"))
            {
                var entry = Ets2ComponentParser.Parse(value, "mod");
                if (entry is null)
                    continue;

                if (entry.Kind == "dlc" || entry.Kind == "rdlc")
                    dlc.Add(entry);
                else if (entry.Kind == "map")
                    maps.Add(entry);
            }
        }
        catch (Exception ex)
        {
            warnings.Add("Список DLC не прочитан: " + ex.Message);
        }

        // Одинаковые компоненты из профиля и из сохранения схлопываются: список
        // подключённого должен читаться как перечень, а не как журнал загрузок.
        Deduplicate(dlc);
        Deduplicate(maps);
    }

    private static void Deduplicate(List<Ets2ModEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var key = entries[i].Kind + "|" + entries[i].Id;
            if (!seen.Add(key))
                entries.RemoveAt(i);
        }

        entries.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private static DateTimeOffset? ParseUnixTime(long seconds)
    {
        if (seconds <= 0)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Восстанавливает имя профиля из имени папки: игра кодирует ASCII-имя
    /// шестнадцатеричными парами (<c>5465737432</c> = <c>Test2</c>).
    ///
    /// Не-ASCII имена так не восстанавливаются — тогда возвращается исходная
    /// строка, и настоящее имя уточняется из <c>profile.sii</c>.
    /// </summary>
    public static string DecodeHexName(string hex)
    {
        if (hex.Length < 4 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            return hex;

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return hex;

            bytes[i] = value;
        }

        var text = System.Text.Encoding.ASCII.GetString(bytes);
        foreach (var ch in text)
        {
            if (ch is < (char)0x20 or > (char)0x7E)
                return hex;
        }

        return text;
    }
}
