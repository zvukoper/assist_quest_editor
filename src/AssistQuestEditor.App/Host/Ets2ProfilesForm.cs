using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Окно «Профили ETS2».
///
/// Отдельное окно, а не панель сайдбара: список подключённых модов, карт и DLC
/// занимает экран целиком, и рядом с картой для него нет места.
///
/// Форма НИЧЕГО не вычисляет сама: перечень профилей, язык интерфейса, моды,
/// карты, DLC и сохранения собирает <see cref="Ets2ProfileReader"/> — знание о
/// форматах игры лежит в домене, и вторая его копия в форме разошлась бы с
/// первой при первом же обновлении игры. Здесь только чтение по запросу страницы
/// и отправка готового пакета.
/// </summary>
public sealed class Ets2ProfilesForm : WebViewForm
{
    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        // Кириллица — обычные символы: экранирование \uXXXX раздувало бы пакет и
        // делало журналы нечитаемыми.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Ets2ProfileReader _reader;
    private string? _pendingHexFolder;

    public Ets2ProfilesForm(Ets2ProfileReader? reader = null)
        : base(
            "Профили ETS2",
            "ets2Profiles.html",
            new Size(1020, 760),
            "ets2-profiles")
    {
        _reader = reader ?? new Ets2ProfileReader();
        MinimumSize = new Size(640, 460);
        MaximizeBox = true;
        MinimizeBox = false;

        if (!WindowGeometryStore.HasSaved("ets2-profiles"))
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            Bounds = new Rectangle(
                area.Left + Math.Max(0, (area.Width - Width) / 2 + 40),
                area.Top + Math.Max(0, (area.Height - Height) / 2 - 30),
                Width,
                Height);
        }
    }

    /// <summary>Папка документов игры, найденная при создании окна (для подписи).</summary>
    public string? GameRoot => _reader.GameRoot;

    protected override void OnBrowserReady()
    {
        AppLogger.Info("Ets2ProfilesForm: окно профилей ETS2 готово.",
            $"size={Width}x{Height}; gameRoot={_reader.GameRoot ?? "<нет>"}");

        var pending = _pendingHexFolder;
        _pendingHexFolder = null;
        if (!string.IsNullOrWhiteSpace(pending))
            BeginInvoke((Action)(() => OpenProfileByHexFolder(pending)));
    }

    protected override void OnWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = UnwrapMessage(document.RootElement);

            if (!root.TryGetProperty("action", out var actionNode))
                return;

            switch (actionNode.GetString())
            {
                case "ets2_request_profiles":
                    SendCatalog();
                    break;

                // Выбран профиль: грузим его данные. Пустая строка — сброс выбора,
                // окно возвращается к подсказке «выберите профиль».
                case "ets2_select_profile":
                    SendProfile(
                        StringOrNull(root, "area") ?? string.Empty,
                        StringOrNull(root, "hexFolder") ?? string.Empty);
                    break;

                case "close_ets2_profiles":
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ets2ProfilesForm: не удалось разобрать сообщение.", ex);
        }
    }

    /// <summary>Просит закрыть окно: закрывает Симулятор, он владеет ссылкой.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Выбран профиль; аргумент — имя профиля (пустая строка — выбор снят).
    ///
    /// Событие поднимается на КАЖДЫЙ выбор, а не только на показ данных: имя
    /// профиля печатается в шапке Симулятора (требование автора), и знать о нём
    /// должен не только этот файл. Отдаём имя строкой, а не объектом профиля:
    /// Симулятору нужна одна строка для подписи, и полный профиль в подписи
    /// приглашал бы рисовать оттуда что-то ещё.
    /// </summary>
    public event EventHandler<string>? ProfileSelected;

    /// <summary>
    /// Открывает профиль по папке lineage, не заставляя пользователя искать его
    /// вручную в списке. Используется карточкой «Синхронизация с ETS2».
    /// </summary>
    public void OpenProfileByHexFolder(string? hexFolder)
    {
        var wanted = (hexFolder ?? string.Empty).Trim();
        if (wanted.Length == 0)
            return;

        if (Browser.CoreWebView2 is null)
        {
            _pendingHexFolder = wanted;
            return;
        }

        try
        {
            var catalog = _reader.ReadCatalog();
            var profile = catalog.Profiles.FirstOrDefault(item =>
                item.HexFolder.Equals(wanted, StringComparison.OrdinalIgnoreCase));

            if (profile is null)
            {
                AppLogger.Warn(
                    "Ets2ProfilesForm: привязанный профиль не найден в текущем каталоге.",
                    $"hexFolder={wanted}");
                return;
            }

            SendCatalog();
            SendProfile(profile.Area, profile.HexFolder);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "Ets2ProfilesForm: не удалось открыть привязанный профиль.",
                ex,
                $"hexFolder={wanted}");
        }
    }

    /// <summary>
    /// Читает каталог профилей и отправляет его странице.
    ///
    /// Кеша здесь НЕТ намеренно: игра создаёт и удаляет профили в любой момент, а
    /// «Обновить список» в окне обязано означать именно «перечитать диск».
    /// Сохранённый каталог давал бы список, не совпадающий с тем, что лежит в
    /// документах игры.
    /// </summary>
    public void SendCatalog()
    {
        try
        {
            var catalog = _reader.ReadCatalog();

            var payload = JsonSerializer.Serialize(new
            {
                type = "ets2_profiles",
                gameRoot = catalog.GameRoot,
                steamRoot = catalog.SteamRoot,
                // Папка облачного хранилища называется отдельно: профиль может быть
                // прочитан ИЗ НЕЁ, и без этой подписи расхождение облачной и
                // локальной копий выглядело бы как ошибка AQE.
                steamCloudRoot = catalog.SteamCloudRoot,
                activeHexFolder = catalog.ActiveHexFolder,
                activeProfileName = catalog.ActiveProfileName,
                profiles = catalog.Profiles,
                warnings = catalog.Warnings
            }, MessageJsonOptions);

            AppLogger.Info("Ets2ProfilesForm: отправляю каталог профилей.",
                $"count={catalog.Profiles.Count}; active={catalog.ActiveHexFolder ?? "<нет>"}");
            PostJson(payload);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ets2ProfilesForm: не удалось прочитать каталог профилей.", ex);
            PostJson(JsonSerializer.Serialize(new
            {
                type = "ets2_profiles_error",
                message = "Не удалось прочитать профили ETS2: " + ex.Message
            }, MessageJsonOptions));
        }
    }

    /// <summary>
    /// Читает и отправляет данные профиля. Пустой адрес — это сброс выбора:
    /// окно обязано вернуться к подсказке, а не показывать прежний профиль.
    /// </summary>
    public void SendProfile(string area, string hexFolder)
    {
        if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(hexFolder))
        {
            PostJson(JsonSerializer.Serialize(new { type = "ets2_profile_cleared" }, MessageJsonOptions));
            ProfileSelected?.Invoke(this, string.Empty);
            return;
        }

        try
        {
            var data = _reader.ReadProfile(area, hexFolder);
            var payload = JsonSerializer.Serialize(new
            {
                type = "ets2_profile",
                // Аватар едет как data-URL: страница открыта по схеме file://, и
                // Chromium не даёт ей читать соседние файлы ни через fetch, ни
                // через <img src="...">. Тот же приём уже используется для каталога
                // предметов — передать байты сообщением.
                avatarDataUrl = ReadAvatarDataUrl(data.AvatarPath),
                profile = data
            }, MessageJsonOptions);

            AppLogger.Info("Ets2ProfilesForm: отправляю данные профиля.",
                $"area={area}; hex={hexFolder}; mods={data.Mods.Count}; maps={data.Maps.Count}; dlc={data.Dlc.Count}");
            PostJson(payload);

            // Имя идёт в шапку Симулятора ТОЛЬКО после успешного чтения: подпись
            // про нечитаемый профиль обещала бы данные, которых нет.
            ProfileSelected?.Invoke(this, data.ProfileName ?? data.Name ?? hexFolder);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ets2ProfilesForm: не удалось прочитать профиль.", ex);
            PostJson(JsonSerializer.Serialize(new
            {
                type = "ets2_profiles_error",
                message = "Не удалось прочитать профиль: " + ex.Message
            }, MessageJsonOptions));
        }
    }

    /// <summary>
    /// Читает PNG-аватар в data-URL.
    ///
    /// Возвращает <c>null</c>, если файла нет или он не читается: отсутствие
    /// аватара — обычное состояние профиля, а не ошибка, и страница обязана
    /// показать заглушку, а не сломанную картинку.
    /// </summary>
    private static string? ReadAvatarDataUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
                return null;

            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Ets2ProfilesForm: аватар профиля не прочитан.", ex.Message);
            return null;
        }
    }

    private static string? StringOrNull(JsonElement root, string name)
        => root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    /// <summary>
    /// Разворачивает сообщение, присланное строкой.
    ///
    /// Страницы этого приложения отправляют <c>JSON.stringify(...)</c>, поэтому
    /// WebView2 отдаёт строку, а не объект. Копия (<see cref="JsonElement.Clone"/>)
    /// обязательна: документ вложенной строки освобождается сразу, и без копии
    /// элементы указывали бы в освобождённую память.
    /// </summary>
    private static JsonElement UnwrapMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.String)
            return root;

        var text = root.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return root;

        using var inner = JsonDocument.Parse(text);
        return inner.RootElement.Clone();
    }
}
