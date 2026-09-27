using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class WorldSelectionRequestedEventArgs : EventArgs
{
    public WorldSelectionRequestedEventArgs(string worldId) => WorldId = worldId;
    public string WorldId { get; }
}

public sealed class CampaignSelectionRequestedEventArgs : EventArgs
{
    public CampaignSelectionRequestedEventArgs(string campaignId) => CampaignId = campaignId;
    public string CampaignId { get; }
}

public sealed class SimulatorForm : WebViewForm
{
    private readonly IDataChannelHub _hub;
    private readonly IQuestRuntimeController _runtime;
    private readonly QuestGraphStore _questGraph;
    private readonly CampaignStore _campaignStore;
    private readonly ILocationResolver _locationResolver;
    private readonly IDynamicEventDispatcher _dynamicEventDispatcher;
    private readonly RoadIndex _roads;
    private readonly RoadRoutePlanner _routePlanner;
    private RouteState _routeState = RouteState.Empty;
    private RoutePlan _routePlan = RoutePlan.Empty;
    private RouteCursor _routeCursor = RouteCursor.Initial;
    private string? _selectedRouteWaypointId;
    private bool _routeEnabled;
    private bool _routeEditingEnabled;
    private DateTimeOffset? _routeMovementLastTick;
    private double _routeLastHeading;
    private int? _routeStoppedWaypointIndex;
    private string? _routeStoppedWaypointId;
    private bool _resumeRouteAfterStop;
    private int? _routeTargetWaypointIndex;
    private string? _routeTargetWaypointId;
    private double _routeTravelRealSeconds;
    private double _routeTravelGameSeconds;
    private DateTimeOffset? _conditionsLastRealTick;
    private TimeSpan? _conditionsLastGameElapsed;

    /// <summary>
    /// Раскрытые разделы правого сайдбара. Хранятся в пользовательских настройках
    /// (ui-settings.json), а не в localStorage страницы: localStorage живёт в
    /// профиле WebView2, а профиль меняется вместе с отпечатком сборки, поэтому
    /// после обновления версии разделы оказывались закрытыми.
    /// </summary>
    private List<string> _sidebarSections = new();

    /// <summary>
    /// Позиция игрока на прошлом тике начисления условий.
    ///
    /// Нужна, чтобы считать ПРОЙДЕННУЮ дистанцию: усталость растёт и по
    /// километрам, а не только по игровым часам. Обнуляется вместе с остальными
    /// якорями, иначе первый тик после паузы или загрузки засчитал бы весь путь,
    /// пройденный до неё, как одну огромную «поездку».
    /// </summary>
    private WorldCoordinate? _conditionsLastPosition;
    private bool _inventoryPausedSimulation;

    /// <summary>
    /// Состояние камеры карты. Host — владелец сохраняемого состояния, WebView
    /// только сообщает изменения положения и масштаба.
    /// </summary>
    private SimulatorMapViewState? _mapView;
    private long _mapViewRestoreToken;
    // Сохранения принадлежат МИРУ: снимок одного мира нельзя загрузить в
    // другой, где другие квесты и точки. Раньше стор брал общий каталог
    // `Документы\Assist Quest Editor\saves`, лежащий ВНЕ дерева миров — его
    // никто не создавал, и автосохранение падало с DirectoryNotFoundException,
    // выглядя как «автосохранение не работает». Без мира (режим CI) остаётся
    // прежний корень: там мир не выбран, и терять нечего.
    private readonly SimulationSaveStore _saveStore;
    private string _worldSelectionJson = "{\\\"type\\\":\\\"world_selection\\\",\\\"worlds\\\":[],\\\"campaigns\\\":[],\\\"worldId\\\":\\\"\\\",\\\"campaignId\\\":\\\"\\\"}";
    // Подпись авторства кэшируется так же, как выбор мира: она состояние Host,
    // и после перезагрузки страницы web-сторона обязана получить её заново.
    private string _authorJson = "{\\\"type\\\":\\\"author\\\",\\\"named\\\":false,\\\"name\\\":\\\"\\\"}";
    private readonly Action<string> _openQuestEditor;
    private readonly System.Windows.Forms.Timer _runtimeTimer;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private string _lastQuestSnapshotLogKey = string.Empty;
    private readonly List<SimulatorJournalEntry> _journalEntries = new();
    private JournalForm? _journalForm;
    private CampaignsForm? _campaignsForm;

    /// <summary>
    /// Единое окно игрока: слева инвентарь, справа «Персонаж / Репутация».
    /// </summary>
    private InventoryForm? _inventoryForm;

    /// <summary>Мир, которому принадлежит окно. Null — режим без выбранного мира (CI).</summary>
    private readonly WorldRecord? _world;

    private bool _journalDetached;
    private bool _snapshotRequestScheduled;
    private bool _journalRefreshScheduled;
    // Выделение квеста живёт в Host, а не только в Web UI: карта, окно кампаний
    // и левый сайдбар должны показывать ОДИН выбранный квест.
    private string _selectedCampaignId = string.Empty;
    private string _selectedQuestId = string.Empty;

    // Активный режим визуализации Location. Хранится в Host, чтобы снимок
    // (который перерисовывает всю карту) не сбрасывал показанный набор: снимки
    // приходят часто, и режим исчезал бы через доли секунды после нажатия.
    private LocationVisualisationRequest? _locationVisualisation;

    /// <summary>
    /// Момент последнего автосохранения.
    ///
    /// Показывается под кнопкой запуска («Автосохранение: дата и время»), чтобы
    /// игрок понимал, к какому состоянию вернётся мир. Null означает «ещё не
    /// сохранялось»: подпись тогда это и говорит, вместо пустоты или выдуманной
    /// даты.
    /// </summary>
    private DateTimeOffset? _autoSaveAt;

    /// <summary>
    /// Мир уже автосохранён при закрытии.
    ///
    /// Форма закрывается двумя событиями подряд (FormClosing, затем FormClosed),
    /// и без флага автосохранение выполнялось бы дважды на каждый выход.
    /// </summary>
    private bool _closedAutosaved;

    public SimulatorForm(
        IDataChannelHub hub,
        IQuestRuntimeController runtime,
        QuestGraphStore questGraph,
        CampaignStore campaignStore,
        Action<string> openQuestEditor,
        ILocationResolver locationResolver,
        IDynamicEventDispatcher dynamicEventDispatcher,
        RoadIndex? roads = null,
        WorldRecord? world = null,
        JunctionIndex? junctions = null)
        : base(
            "Симулятор",
            "simulator.html",
            new Size(1440, 900),
            "simulator")
    {
        _hub = hub;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _questGraph = questGraph ?? throw new ArgumentNullException(nameof(questGraph));
        // Каталог кампаний ограничивается МИРОМ: два мира могут иметь кампанию
        // с одним и тем же id (например Common), и без ограничения они бы
        // наложились друг на друга в списке и на карте.
        _world = world;
        _campaignStore = (campaignStore ?? throw new ArgumentNullException(nameof(campaignStore)))
            .ScopedTo(world?.FolderPath);
        // Симулятор при каждом открытии обязан начинаться выключенным:
        // автозагрузка восстанавливает данные мира, но сама симуляция не стартует.
        _runtime.SetSimulationRunning(false);
        _saveStore = new SimulationSaveStore(
            world is null
                ? AppPaths.SimulationSaveRoot
                : WorldPaths.SavesFolderPath(world.FolderPath));
        _openQuestEditor = openQuestEditor ?? throw new ArgumentNullException(nameof(openQuestEditor));
        _locationResolver = locationResolver ?? throw new ArgumentNullException(nameof(locationResolver));
        _dynamicEventDispatcher = dynamicEventDispatcher ?? throw new ArgumentNullException(nameof(dynamicEventDispatcher));
        _roads = roads ?? new RoadIndex(Array.Empty<RoadSegment>());
        _routePlanner = new RoadRoutePlanner(_roads.Segments, junctions?.ToPoints() ?? Array.Empty<JunctionPoint>());
        // Пользовательские настройки читаются ОДНИМ вызовом: их файл — общий, и
        // два независимых Load() подряд читали бы его дважды на каждое открытие
        // Симулятора.
        var preferences = AppUiPreferencesStore.Load();
        _journalDetached = preferences.JournalDetached;
        _sidebarSections = preferences.SidebarSections is { } savedSections
            ? new List<string>(savedSections)
            : new List<string>();
        Opacity = 0;
        GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _questGraph.Changed += QuestGraph_Changed;
        _runtime.Published += Runtime_Published;
        _runtimeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _runtimeTimer.Tick += (_, _) =>
        {
            UpdateRouteMovement();
            _runtime.Tick();
            _dynamicEventDispatcher.Tick();
            UpdatePlayerConditions();

            if (_runtime.SimulationRunning)
                PushLiveState();
        };
        _runtimeTimer.Start();

        if (_hub.Events is EventChannel<SimulatorEvent> events)
        {
            events.Published += Events_Published;
        }

        // Штатное закрытие окна симулятора: мир автосохраняется, если симуляция
        // была запущена. Форма закрывается при выходе из приложения через
        // MainForm.FormClosed, поэтому отдельной ветки «выход из приложения» не
        // нужно — это тот же путь.
        FormClosing += (_, _) => AutosaveOnClose();

        FormClosed += (_, _) =>
        {
            _journalForm?.Close();
            _journalForm = null;

            // Окно инвентаря закрывается вместе с Симулятором: оно показывает
            // его состояние, и без хозяина осталось бы висеть с устаревшими
            // данными, обновлять которые некому.
            if (_inventoryForm is not null)
            {
                _inventoryForm.Close();
                _inventoryForm = null;
            }

            if (_campaignsForm is not null)
            {
                _campaignsForm.CampaignActiveChanged -= CampaignsForm_CampaignActiveChanged;
                _campaignsForm.QuestEnabledChanged -= CampaignsForm_QuestEnabledChanged;
                _campaignsForm.QuestOpenRequested -= CampaignsForm_QuestOpenRequested;
                _campaignsForm.CampaignFolderOpenRequested -= CampaignsForm_CampaignFolderOpenRequested;
                _campaignsForm.Close();
                _campaignsForm = null;
            }

            _runtimeTimer.Stop();
            _runtimeTimer.Dispose();
            GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _runtime.Published -= Runtime_Published;
            _questGraph.Changed -= QuestGraph_Changed;
            if (_hub.Events is EventChannel<SimulatorEvent> events)
            {
                events.Published -= Events_Published;
            }
        };
    }

    protected override void OnBrowserReady()
    {
        Opacity = 1;
        AppLogger.Info("SimulatorForm: browser ready, отправляю snapshot.");

        // Мир восстанавливается ДО первого снимка: карта должна сразу показать
        // актуальное состояние, а не мигнуть «началом мира» и перерисоваться.
        AutoLoadWorld();
        PushRoads();
        PushSnapshot();
        PostJson(_worldSelectionJson);
        PostJson(_authorJson);
        if (_journalDetached)
        {
            BeginInvoke((Action)OpenJournalWindow);
        }
    }

    /// <summary>
    /// Автосохранение при штатном закрытии окна.
    ///
    /// Делается до уничтожения каналов и таймера: после закрытия читать состояние
    /// уже неоткуда. Флаг защищает от повторного вызова на FormClosed.
    /// </summary>
    private void AutosaveOnClose()
    {
        if (_closedAutosaved)
            return;

        _closedAutosaved = true;
        if (!_runtime.SimulationRunning && !_runtime.IsPaused)
        {
            AppLogger.Info("SimulatorForm: закрытие без автосохранения (симуляция уже остановлена).");
            return;
        }

        StopSimulation("закрытие симулятора");
    }

    /// <summary>
    /// Отправляет дорожную геометрию отдельным сообщением.
    ///
    /// Почему не файлом: страница открыта по схеме file://, а Chromium блокирует
    /// для неё выборку данных по этой схеме — fetch, XHR и даже &lt;img&gt; не могут
    /// прочитать файл, лежащий рядом со страницей (проверено замером). Тот же
    /// канал уже использует окно проверки перекрёстков, и 3,5 МБ через него
    /// проходят целиком.
    ///
    /// Отправка идёт ДО снимка: карта рисуется сразу с дорогами, а не мигает
    /// пустым фоном до прихода второго сообщения.
    /// </summary>
    private void PushRoads()
    {
        if (Browser.CoreWebView2 is null || _roads.IsEmpty)
        {
            if (_roads.IsEmpty)
                AppLogger.Warn("Дорожная геометрия пуста: слой дорог не будет показан.");
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "roads",
            segments = _roads.ToFlatArray(),
            debugNodes = _routePlanner.DebugNodesFlatArray(),
            segmentCount = _roads.SegmentCount
        }, SnapshotJsonOptions);

        AppLogger.Info("SimulatorForm: отправляю дорожную геометрию.",
            $"segments={_roads.SegmentCount}; jsonChars={payload.Length}");
        PostJson(payload);
    }

    /// <summary>Передаёт Симулятору тот же селектор мира и кампании, что видит MainForm.</summary>
    public void SetWorldSelectionJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        _worldSelectionJson = json;
        if (Browser.CoreWebView2 is not null)
            PostJson(json);
    }

    /// <summary>
    /// Передаёт Симулятору подпись авторства для его шапки.
    ///
    /// Отдельный канал нужен потому, что авторство — состояние Host, а не Web:
    /// псевдоним меняется в настройках, и подпись обязана обновиться в обоих
    /// окнах, не дожидаясь перезапуска Симулятора.
    /// </summary>
    public void SetAuthorJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        _authorJson = json;
        if (Browser.CoreWebView2 is not null)
            PostJson(json);
    }

    public void PushSnapshot()
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        if (_campaignsForm is not null && !_campaignsForm.IsDisposed)
            _campaignsForm.SetCatalog(_campaignStore.BuildSimulatorCatalog());

        var snapshot = _hub.GetSnapshot();
        var pointCount = snapshot.World.Points.Count;
        AppLogger.Info("SimulatorForm: формирование snapshot.",
            $"points={pointCount}; selected={snapshot.Selection.Point?.Id ?? "<none>"}; player={snapshot.Player.Position}");
        LogQuestSnapshot("snapshot");

        var payload = JsonSerializer.Serialize(new
        {
            type = "snapshot",
            version = VersionInfo.InformationalVersion,
            snapshot,
            journal = _journalEntries.ToArray(),
            itemCatalog = ItemCatalogFactory.CreateStarter(),
            // Каталог НПЦ нужен UI, чтобы показать имя и портрет рядом с числом
            // репутации: сама репутация хранится только по стабильному Id.
            npcCatalog = NpcCatalogFactory.CreateStarter(),
            // Диапазон, цвет и процент считает домен: UI не должен дублировать
            // таблицу порогов репутации.
            reputationViews = snapshot.Reputation.Entries.ToDictionary(
                pair => pair.Key,
                pair => ReputationScale.Describe(pair.Value.Value),
                StringComparer.OrdinalIgnoreCase),
            runtime = _runtime.State,
            simulationRunning = _runtime.SimulationRunning,
            simulationPaused = _runtime.IsPaused,
            simulationSpeed = _runtime.SimulationSpeed,
            // Подпись под кнопкой запуска: к какому состоянию мира игрок вернётся.
            // Берётся СИСТЕМНАЯ дата текущего автосохранения (локальное время
            // машины, до секунды), а не игровое время мира: подпись отвечает на
            // вопрос «когда это записано на диск», и по ней игрок сверяется с
            // часами Windows.
            autoSaveLabel = _autoSaveAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
            enabledQuestIds = _runtime.EnabledQuestIds,
            questCatalog = BuildQuestCatalog(snapshot),
            selectedQuest = new
            {
                campaignId = _selectedCampaignId,
                questId = _selectedQuestId
            },
            questGraph = _runtime.ActiveGraph ?? _questGraph.Value,
            journalDetached = _journalDetached,
            // Раскрытые разделы сайдбара едут со снимком: Web их не хранит, а
            // рисует присланное. Так разделы переживают перезапуск и не зависят
            // от того, менялся ли профиль WebView2.
            sidebarSections = _sidebarSections,
            // Режим визуализации Location едет вместе со снимком: снимок
            // перерисовывает всю карту, и без этого набор точек исчезал бы
            // через доли секунды после нажатия «Показать в симуляторе».
            locationVisualisation = _locationVisualisation,
            route = BuildRouteSnapshot(),
            mapView = _mapView,
            mapViewRestoreToken = _mapViewRestoreToken,
            // Индикатор светового дня: астрономию считает домен, UI только рисует.
            daylight = BuildDaylight(snapshot),
            // Свойства мира из кампании: блок «Окружение» показывает их и умеет
            // записывать обратно в файл кампании.
            worldSettings = BuildWorldSettings()
        }, SnapshotJsonOptions);

        AppLogger.Info("SimulatorForm: отправляю snapshot в WebView2.", $"jsonChars={payload.Length}; points={pointCount}");
        PostJson(payload);

        // Окно инвентаря получает ТОТ ЖЕ json, что и карта. Отдельная сборка
        // снимка для него означала бы два описания состояния мира, и правка
        // одного поля в Симуляторе молча ломала бы инвентарь.
        if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
            _inventoryForm.SetSnapshotJson(payload);
    }

    /// <summary>
    /// Открывает окно инвентаря (клавиша I) или сообщает о нём.
    ///
    /// Окно ОДНО: повторное нажатие I поднимает уже открытое, а не создаёт второе.
    /// Два окна с одним содержимым — это два места, где его надо обновлять, и
    /// рано или поздно они покажут разное.
    /// </summary>
    private void OpenInventoryWindow()
    {
        if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
        {
            _inventoryForm.WindowState = FormWindowState.Normal;
            _inventoryForm.BringToFront();
            _inventoryForm.Activate();
            return;
        }

        _inventoryForm = new InventoryForm();
        _inventoryForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _inventoryForm.InventoryItemSeenRequested += InventoryForm_ItemSeenRequested;
        // Клавиша I и Escape внутри окна закрывают его: страница шлёт просьбу, а
        // закрывает Симулятор — он владеет ссылкой и после закрытия отвечает
        // снимком.
        _inventoryForm.CloseRequested += (_, _) => CloseInventoryWindow();
        _inventoryForm.FormClosed += (_, _) =>
        {
            _inventoryForm.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            var resumeSimulation = _inventoryPausedSimulation;
            _inventoryPausedSimulation = false;
            _inventoryForm = null;

            if (resumeSimulation && _runtime.IsPaused)
                ResumeSimulation();

            RequestSnapshot("inventory window closed");
        };

        _inventoryPausedSimulation = _runtime.SimulationRunning;
        if (_inventoryPausedSimulation)
            PauseSimulation();

        _inventoryForm.Show(this);
        AppLogger.Info("SimulatorForm: окно инвентаря открыто.",
            $"size={_inventoryForm.Width}x{_inventoryForm.Height}");
    }

    /// <summary>Закрывает окно инвентаря, если оно открыто.</summary>
    private void CloseInventoryWindow()
    {
        if (_inventoryForm is null || _inventoryForm.IsDisposed)
            return;

        _inventoryForm.Close();
    }

    private void SimulatorForm_GlobalHotKeyPressed(object? sender, WebViewHotKeyEventArgs e)
    {
        if (e.Key == Keys.I)
        {
            if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
                CloseInventoryWindow();
            else
                OpenInventoryWindow();

            return;
        }

        if (e.Key != Keys.Escape)
            return;

        if (_inventoryForm is not null &&
            !_inventoryForm.IsDisposed &&
            ReferenceEquals(sender, _inventoryForm))
        {
            CloseInventoryWindow();
            return;
        }

        if (sender is Form child &&
            !ReferenceEquals(child, this) &&
            !child.IsDisposed)
        {
            child.Close();
        }
    }

    /// <summary>
    /// Игрок увидел предмет в окне инвентаря: снимаем значок «новый».
    ///
    /// Запись идёт через ТОТ ЖЕ путь, что и для панели: состояние показа живёт в
    /// Симуляторе, и второй учёт в окне дал бы возвращение значка после
    /// следующего снимка.
    /// </summary>
    private void InventoryForm_ItemSeenRequested(object? sender, InventoryItemSeenEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.ItemId))
            return;

        MarkInventoryItemSeen(e.ItemId);
        RequestSnapshot("inventory item seen in window");
    }

    protected override void OnWebMessage(string json)
    {
        AppLogger.Info("SimulatorForm: обработка web action.", $"json={json}");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var action = root.GetProperty("action").GetString() ?? string.Empty;

            switch (action)
            {
                case "set_player_position":
                    SetPlayerPosition(root);
                    break;

                case "set_map_view":
                    SetMapView(root);
                    return;

                case "route_add_waypoint":
                    AddRouteWaypoint(root);
                    break;

                case "route_insert_waypoint":
                    InsertRouteWaypoint(root);
                    break;

                case "route_move_waypoint":
                    MoveRouteWaypoint(root);
                    break;

                case "route_delete_waypoint":
                    DeleteRouteWaypoint(Required(root, "id"));
                    break;

                case "route_select_waypoint":
                    SelectRouteWaypoint(Required(root, "id"));
                    break;

                case "route_set_waypoint_speed":
                    SetRouteWaypointSpeed(
                        Required(root, "id"),
                        Number(root, "speed", RouteState.DefaultSpeedKmhValue));
                    break;

                case "route_set_waypoint_offroad":
                    SetRouteWaypointOffRoad(
                        Required(root, "id"),
                        root.GetProperty("offRoad").GetBoolean());
                    break;

                case "route_set_default_speed":
                    SetRouteDefaultSpeed(
                        Number(root, "speed", RouteState.DefaultSpeedKmhValue));
                    break;

                case "route_toggle":
                    SetRouteEnabled(root.GetProperty("enabled").GetBoolean());
                    break;
                case "route_edit_toggle":
                    SetRouteEditingEnabled(root.GetProperty("enabled").GetBoolean());
                    break;

                case "route_clear":
                    ClearRoute();
                    break;

                case "route_save_file":
                    SaveRouteToFile();
                    break;

                case "route_load_file":
                    LoadRouteFromFileDialog();
                    break;

                case "select_point":
                    SelectPoint(root);
                    break;

                case "activate_dynamic_event":
                    ActivateDynamicEvent(root);
                    break;

                case "clear_selection":
                    _hub.Get<WorldSelectionState>("world-selection").Set(
                        new WorldSelectionState(null, "Карта симулятора"),
                        "Карта симулятора");
                    break;

                case "create_temporary_point":
                    CreateTemporaryPoint(root);
                    break;

                case "focus_journal_coordinate":
                    FocusJournalCoordinate(root);
                    break;

                case "sleep_field":
                    SleepPlayer(fullSleep: false);
                    break;

                case "sleep_full":
                    SleepPlayer(fullSleep: true);
                    break;

                case "set_fact":
                    SetFact(root);
                    break;

                case "set_flag":
                    SetFlag(root);
                    break;

                case "set_variable":
                    SetVariable(root);
                    break;

                case "set_quest_status":
                    SetQuestStatus(root);
                    break;

                case "set_inventory":
                    SetInventory(root);
                    break;

                case "mark_inventory_seen":
                    MarkInventorySeen(root);
                    break;

                // Инвентарь открывается и закрывается клавишей I. Решение
                // принимает Host, а не страница: окно — настоящая форма Windows,
                // и создавать её из JS было бы невозможно.
                case "toggle_inventory":
                    if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
                        CloseInventoryWindow();
                    else
                        OpenInventoryWindow();
                    break;

                case "set_vitals":
                    SetPlayerVitals(root);
                    break;

                case "set_stress":
                    SetStress(root);
                    break;

                case "set_progress":
                    SetPlayerProgress(root);
                    break;

                case "set_character_stat":
                    SetCharacterStat(root);
                    break;

                case "set_reputation":
                    SetReputation(root);
                    break;

                case "set_telemetry":
                    SetTelemetry(root);
                    break;

                case "set_environment":
                    SetEnvironment(root);
                    break;

                case "set_world_time":
                    SetWorldTime(root);
                    break;

                case "save_world_to_campaign":
                    SaveWorldToCampaign(root);
                    break;

                case "load_world_from_campaign":
                    LoadWorldFromCampaign();
                    break;

                // Явный запрос свежего снимка: правка игрового времени и погоды
                // должна быть видна сразу, а не после следующего события канала.
                case "request_snapshot":
                    RequestSnapshot("web action request_snapshot");
                    break;

                // Выход из режима визуализации Location. Режим живёт в Host и
                // едет вместе с каждым снимком, поэтому сброса в UI недостаточно:
                // без этого сообщения режим вернулся бы на первом же обновлении.
                case "clear_location_visualisation":
                    _locationVisualisation = null;
                    AppLogger.Info("SimulatorForm: режим визуализации Location выключен.");
                    break;

                case "emit_event":
                    EmitEvent(root);
                    break;

                case "interface_choice":
                    InterfaceChoice(root);
                    break;

                case "interface_dialogue_continue":
                    InterfaceDialogueContinue(root);
                    break;

                case "detach_journal":
                    DetachJournal();
                    break;

                case "set_sidebar_sections":
                    SetSidebarSections(root);
                    break;
                case "open_journal":
                    OpenJournalWindow();
                    break;

                // Подпись авторства в шапке — это вход в настройки, а не отдельный
                // диалог имени: имя меняется там же, где его объясняют. Сам
                // Симулятор настроек не открывает — он просит об этом MainForm,
                // который владеет и настройками, и псевдонимом.
                case "open_settings":
                    OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case "open_campaigns":
                    OpenCampaignsWindow(root);
                    break;

                case "select_quest":
                    SelectQuest(Required(root, "campaignId"), Required(root, "questId"));
                    break;

                case "return_journal_to_sidebar":
                    ReturnJournalToSidebar();
                    break;

                case "simulation_start":
                    StartSimulation();
                    break;

                case "simulation_stop":
                    StopSimulation("симуляция остановлена вручную");
                    break;

                case "simulation_pause":
                    PauseSimulation();
                    break;

                case "simulation_resume":
                    ResumeSimulation();
                    break;

                case "simulation_set_speed":
                    _runtime.SetSimulationSpeed(Number(root, "speed", _runtime.SimulationSpeed));
                    AppLogger.Info("SimulatorForm: кратность игрового времени изменена.",
                        $"speed={_runtime.SimulationSpeed}");
                    // Снимок обязателен: кратность живёт в Runtime, а UI узнаёт о
                    // ней ТОЛЬКО из снимка. Без этого ответа кнопка ff выглядела
                    // неработающей — нажатие уходило, но ни кнопка, ни часы, ни
                    // подсказка не менялись.
                    RequestSnapshot("simulation speed changed");
                    break;

                case "select_world":
                    WorldSwitchRequested?.Invoke(this, new WorldSelectionRequestedEventArgs(Required(root, "worldId")));
                    break;

                case "select_campaign":
                    CampaignSelectionRequested?.Invoke(this, new CampaignSelectionRequestedEventArgs(Required(root, "campaignId")));
                    break;

                case "set_quest_enabled":
                    SetQuestEnabled(root);
                    break;

                case "set_campaign_active":
                    SetCampaignActive(root);
                    break;

                case "open_quest_editor":
                    OpenQuestEditor(root);
                    break;

                case "open_campaign_folder":
                    OpenCampaignFolder(root);
                    break;

                case "reset":
                    if (_locationResolver is ILocationResolutionSession locationSession)
                        locationSession.Reset();

                    if (_hub is SimulatorDataChannelHub simulatorHub)
                    {
                        simulatorHub.Reset();
                    }
                    _runtime.Reset();
                    _dynamicEventDispatcher.Reset();
                    // «Сбросить» обнуляет сохранённое прохождение, но НЕ выключает
                    // симуляцию: пользователь продолжает работу в чистом мире с
                    // той же сессией.
                    //
                    // Автосохранение после сброса НЕ пишется: автосохранение — это
                    // только реакция на выключение симуляции. Пустой слот означает
                    // «прохождения нет», и следующий запуск честно возьмёт мир из
                    // кампании.
                    _saveStore.ClearSession();
                    _autoSaveAt = null;
                    _mapView = null;
                    _mapViewRestoreToken++;
                    SetRouteStateAfterLoad(RouteState.Empty);

                    // Свойства мира берутся из КАМПАНИИ, а не остаются какими были:
                    // сброс возвращает мир к состоянию «на входе», и погода с
                    // временем — часть этого состояния. Раньше сброс их не трогал,
                    // и после испорченной правки координаты не возвращались.
                    ApplyWorldFromCampaign("сброс симулятора");

                    AppLogger.Info("SimulatorForm: прохождение сброшено.",
                        $"simulationRunning={_runtime.SimulationRunning}");
                    break;

                case "reload_catalog":
                    ReloadCatalog("web action reload_catalog");
                    break;

                case "list_saves":
                    PostSaveList();
                    break;

                case "create_save":
                    CreateSave(root);
                    break;

                case "load_save":
                    LoadSave(root);
                    break;

                case "overwrite_save":
                    OverwriteSave(root);
                    break;

                case "delete_save":
                    DeleteSave(root);
                    break;

                case "rename_save":
                    RenameSave(root);
                    break;

                default:
                    return;
            }

            RequestSnapshot("web action " + action);
        }
        catch (Exception ex)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "error",
                message = ex.Message
            }));
        }
    }

    private void CreateTemporaryPoint(JsonElement root)
    {
        var x = root.TryGetProperty("x", out var xNode) && xNode.TryGetDouble(out var xValue) ? xValue : 0;
        var y = root.TryGetProperty("y", out var yNode) && yNode.TryGetDouble(out var yValue) ? yValue : 0;
        var z = root.TryGetProperty("z", out var zNode) && zNode.TryGetDouble(out var zValue) ? zValue : 0;
        var name = root.TryGetProperty("name", out var nameNode)
            ? nameNode.GetString()
            : null;
        var position = new WorldCoordinate(x, y, z);
        var point = new WorldPoint(
            "temporary:" + Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(name) ? "Временная точка" : name,
            "Temporary",
            position)
        {
            Color = "#fab003"
        };

        _hub.Get<WorldSelectionState>("world-selection").Set(
            new WorldSelectionState(point, point.Name),
            "Simulator: создана временная точка");
        PostJson(JsonSerializer.Serialize(new
        {
            type = "focus_point",
            position,
            name = point.Name
        }, SnapshotJsonOptions));
        AppLogger.Info("Simulator: создана временная точка.", $"position={point.Position}; id={point.Id}; name={point.Name}");
    }

    private void FocusJournalCoordinate(JsonElement root)
    {
        var position = new WorldCoordinate(
            Number(root, "x", 0),
            Number(root, "y", 0),
            Number(root, "z", 0));

        CreateTemporaryPoint(JsonSerializer.SerializeToElement(new
        {
            x = position.X,
            y = position.Y,
            z = position.Z,
            name = "Координата из журнала"
        }));
    }

    private void FocusJournalCoordinate(WorldCoordinate position) =>
        CreateTemporaryPoint(JsonSerializer.SerializeToElement(new
        {
            x = position.X,
            y = position.Y,
            z = position.Z,
            name = "Координата из журнала"
        }));

    private void SetPlayerPosition(JsonElement root)
    {
        if (_routeEnabled && _runtime.SimulationRunning)
            throw new InvalidOperationException(
                "Координаты игрока нельзя менять во время движения по маршруту.");

        var previousTravelRealSeconds = _routeTravelRealSeconds;
        var previousTravelGameSeconds = _routeTravelGameSeconds;
        var old = _hub.Get<PlayerState>("player").Value;
        var position = new WorldCoordinate(
            Number(root, "x", old.Position.X),
            Number(root, "y", old.Position.Y),
            Number(root, "z", old.Position.Z));

        _hub.Get<PlayerState>("player").Set(
            old with { Position = position },
            "Редактор игрока");

        if (_routeState.Waypoints.Count > 0)
        {
            // Перемещение игрока пользователем — явный пересчёт маршрута.
            // Алгоритм отбрасывает точки, которые остались позади игрока, и
            // перенумеровывает оставшуюся пользовательскую структуру.
            _routeStoppedWaypointIndex = null;
            _routeStoppedWaypointId = null;
            _resumeRouteAfterStop = false;
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;

            RebuildRoute(
                "player position changed",
                publishSnapshot: false,
                trimToPlayer: true,
                renumberTrimmed: true);

            _routeTravelRealSeconds = previousTravelRealSeconds;
            _routeTravelGameSeconds = previousTravelGameSeconds;

            PersistSession("автосохранение: player position changed", force: true);
            PushRouteSnapshot();
        }

        if (_runtime.State.Status == QuestRuntimeStatus.Waiting)
        {
            QuestLogger.Info("Quest Runtime: игрок перемещён во время ожидания.",
                QuestLogger.Json(new
                {
                    position,
                    waitingFor = _runtime.State.WaitingFor,
                    currentNodeId = _runtime.State.CurrentNodeId
                }));
        }
    }

    private void SelectPoint(JsonElement root)
    {
        var id = Required(root, "id");
        var point = _hub.Get<WorldState>("world").Value.Points
            .FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (point is null)
        {
            throw new InvalidOperationException("СДО-точка не найдена: " + id);
        }

        _hub.Get<WorldSelectionState>("world-selection").Set(
            new WorldSelectionState(point, "Карта симулятора"),
            "Карта симулятора");
    }


    private sealed record RouteFileCoordinate(double X, double Y, double Z);

    private sealed record RouteFileWaypoint(
        string Id,
        int Number,
        double X,
        double Y,
        double Z,
        double SpeedKmh,
        bool IsOffRoad);

    private sealed record RouteFileLeg(
        int StartWaypointIndex,
        int EndWaypointIndex,
        double LengthMeters,
        IReadOnlyList<RouteFileCoordinate> Polyline);

    private sealed record RouteFile(
        int Version,
        double DefaultSpeedKmh,
        IReadOnlyList<RouteFileWaypoint> Waypoints,
        IReadOnlyList<RouteFileLeg> Legs);

    private static readonly JsonSerializerOptions RouteFileJsonOptions = new(SnapshotJsonOptions)
    {
        WriteIndented = true
    };

    private void SaveRouteToFile()
    {
        if (_routeState.Waypoints.Count == 0)
            throw new InvalidOperationException("Маршрут пуст — сохранять нечего.");

        using var dialog = new SaveFileDialog
        {
            Filter = "Assist Quest Route (*.aqewaypoints)|*.aqewaypoints|Все файлы (*.*)|*.*",
            DefaultExt = "aqewaypoints",
            AddExtension = true,
            FileName = "route.aqewaypoints",
            Title = "Сохранить маршрут"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var normalized = _routeState.Normalize();
        var file = new RouteFile(
            2,
            normalized.DefaultSpeedKmh,
            normalized.Waypoints.Select((waypoint, index) => new RouteFileWaypoint(
                waypoint.Id,
                waypoint.EffectiveNumber(index),
                waypoint.Position.X,
                waypoint.Position.Y,
                waypoint.Position.Z,
                waypoint.SpeedKmh,
                waypoint.IsOffRoad)).ToArray(),
            _routePlan.Legs.Select(leg => new RouteFileLeg(
                leg.StartWaypointIndex,
                leg.EndWaypointIndex,
                leg.LengthMeters,
                leg.Polyline.Select(point =>
                    new RouteFileCoordinate(point.X, point.Y, point.Z)).ToArray())).ToArray());

        var json = JsonSerializer.Serialize(file, RouteFileJsonOptions);
        File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
        AppLogger.Info(
            "SimulatorForm: маршрут сохранён.",
            $"path={dialog.FileName}; waypoints={file.Waypoints.Count}; version={file.Version}");
    }

    private void LoadRouteFromFileDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Assist Quest Route (*.aqewaypoints)|*.aqewaypoints|Все файлы (*.*)|*.*",
            DefaultExt = "aqewaypoints",
            CheckFileExists = true,
            Title = "Загрузить маршрут"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenRouteFileFromAssociation(dialog.FileName);
    }

    private enum RouteFileLoadAction
    {
        Cancel,
        MovePlayerToStart,
        RecalculateToPlayer
    }

    private RouteFileLoadAction ShowRouteFileLoadDialog(string fileName)
    {
        using var dialog = new Form
        {
            Text = "Загрузка маршрута",
            Width = 620,
            Height = 210,
            MinimumSize = new Size(620, 210),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        var label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"Вы загружаете маршрут "{fileName}""
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };

        var result = RouteFileLoadAction.Cancel;
        var cancel = new Button { Text = "Отмена", AutoSize = true };
        cancel.Click += (_, _) =>
        {
            result = RouteFileLoadAction.Cancel;
            dialog.Close();
        };

        var recalculate = new Button
        {
            Text = "Пересчитать маршрут до игрока",
            AutoSize = true
        };
        recalculate.Click += (_, _) =>
        {
            result = RouteFileLoadAction.RecalculateToPlayer;
            dialog.Close();
        };

        var moveStart = new Button
        {
            Text = "Переместить игрока к началу маршрута",
            AutoSize = true
        };
        moveStart.Click += (_, _) =>
        {
            result = RouteFileLoadAction.MovePlayerToStart;
            dialog.Close();
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(recalculate);
        buttons.Controls.Add(moveStart);
        root.Controls.Add(label, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(root);
        dialog.CancelButton = cancel;

        dialog.ShowDialog(this);
        return result;
    }

    public void OpenRouteFileFromAssociation(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => OpenRouteFileFromAssociation(path)));
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();

        RouteFile file;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            file = JsonSerializer.Deserialize<RouteFile>(json, RouteFileJsonOptions)
                ?? throw new InvalidDataException("Файл маршрута пуст.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SimulatorForm: маршрут не загружен.", ex, "path=" + path);
            MessageBox.Show(
                this,
                "Не удалось прочитать маршрут: " + ex.Message,
                "Загрузка маршрута",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (file.Waypoints is null || file.Waypoints.Count == 0)
        {
            MessageBox.Show(
                this,
                "Файл не содержит путевых точек.",
                "Загрузка маршрута",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var name = Path.GetFileName(path);
        var action = ShowRouteFileLoadDialog(name);
        if (action == RouteFileLoadAction.Cancel)
            return;

        try
        {
            var waypoints = file.Waypoints.Select((item, index) => new RouteWaypoint(
                string.IsNullOrWhiteSpace(item.Id)
                    ? Guid.NewGuid().ToString("N")
                    : item.Id,
                new WorldCoordinate(item.X, item.Y, item.Z),
                Math.Clamp(
                    double.IsFinite(item.SpeedKmh)
                        ? item.SpeedKmh
                        : RouteState.DefaultSpeedKmhValue,
                    RouteState.MinSpeedKmh,
                    RouteState.MaxSpeedKmh),
                item.IsOffRoad,
                item.Number > 0 ? item.Number : index + 1)).ToArray();

            _routeState = new RouteState(
                Math.Clamp(
                    double.IsFinite(file.DefaultSpeedKmh)
                        ? file.DefaultSpeedKmh
                        : RouteState.DefaultSpeedKmhValue,
                    RouteState.MinSpeedKmh,
                    RouteState.MaxSpeedKmh),
                waypoints).Normalize();

            _selectedRouteWaypointId = null;
            _routeStoppedWaypointIndex = null;
            _routeStoppedWaypointId = null;
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;
            _routeCursor = RouteCursor.Initial;
            _routeLastHeading = 0d;
            _resumeRouteAfterStop = false;
            _routeTravelRealSeconds = 0d;
            _routeTravelGameSeconds = 0d;
            _routeEnabled = false;
            _routeEditingEnabled = false;

            if (action == RouteFileLoadAction.MovePlayerToStart)
            {
                var player = _hub.Get<PlayerState>("player").Value;
                _hub.Get<PlayerState>("player").Set(
                    player with { Position = _routeState.Waypoints[0].Position },
                    "Загрузка маршрута: перемещение к началу");

                RebuildRoute(
                    "route file loaded: move player to start",
                    publishSnapshot: false,
                    trimToPlayer: true,
                    renumberTrimmed: false);
            }
            else
            {
                RebuildRoute(
                    "route file loaded: recalculate to player",
                    publishSnapshot: false,
                    trimToPlayer: true,
                    renumberTrimmed: true);
            }
            PushRouteSnapshot(fitToRoute: true);
            PersistSession("автосохранение: маршрут загружен", force: true);
            AppLogger.Info(
                "SimulatorForm: маршрут загружен.",
                $"path={path}; waypoints={_routeState.Waypoints.Count}; action={action}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SimulatorForm: ошибка применения маршрута.", ex, "path=" + path);
            MessageBox.Show(
                this,
                "Не удалось загрузить маршрут: " + ex.Message,
                "Загрузка маршрута",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void AddRouteWaypoint(JsonElement root)
    {
        EnsureRouteEditingAllowed();

        var position = new WorldCoordinate(
            Number(root, "x", _hub.Get<PlayerState>("player").Value.Position.X),
            Number(root, "y", _hub.Get<PlayerState>("player").Value.Y),
            Number(root, "z", _hub.Get<PlayerState>("player").Value.Z));

        var waypoint = new RouteWaypoint(
            "route:" + Guid.NewGuid().ToString("N"),
            position,
            _routeState.DefaultSpeedKmh);

        _routeState = new RouteState(
            _routeState.DefaultSpeedKmh,
            _routeState.Waypoints.Concat(new[] { waypoint }).ToArray())
            .RenumberWaypoints();

        _selectedRouteWaypointId = waypoint.Id;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _resumeRouteAfterStop = false;
        RebuildRoute("waypoint added", publishSnapshot: false, trimToPlayer: false);
        PersistSession("автосохранение: waypoint added", force: true);
        PushRouteSnapshot();
    }

    private void InsertRouteWaypoint(JsonElement root)
    {
        EnsureRouteEditingAllowed();

        var player = _hub.Get<PlayerState>("player").Value;
        var position = new WorldCoordinate(
            Number(root, "x", player.Position.X),
            Number(root, "y", player.Position.Y),
            Number(root, "z", player.Position.Z));

        var insertAt = Math.Clamp(
            root.TryGetProperty("insertAt", out var insertNode) &&
            insertNode.TryGetInt32(out var parsed)
                ? parsed
                : _routeState.Waypoints.Count,
            0,
            _routeState.Waypoints.Count);

        var list = _routeState.Waypoints.ToList();
        var inserted = new RouteWaypoint(
            "route:" + Guid.NewGuid().ToString("N"),
            position,
            _routeState.DefaultSpeedKmh);

        list.Insert(insertAt, inserted);
        _routeState = new RouteState(
            _routeState.DefaultSpeedKmh,
            list).RenumberWaypoints();

        _selectedRouteWaypointId = inserted.Id;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _resumeRouteAfterStop = false;
        RebuildRoute("waypoint inserted", publishSnapshot: false, trimToPlayer: false);
        PersistSession("автосохранение: waypoint inserted", force: true);
        PushRouteSnapshot();
    }

    private void MoveRouteWaypoint(JsonElement root)
    {
        EnsureRouteEditingAllowed();

        var id = Required(root, "id");
        var found = false;
        var waypoints = _routeState.Waypoints.Select(item =>
        {
            if (!item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return item;

            found = true;
            return item with
            {
                Position = new WorldCoordinate(
                    Number(root, "x", item.Position.X),
                    Number(root, "y", item.Position.Y),
                    Number(root, "z", item.Position.Z))
            };
        }).ToArray();

        if (!found)
            return;

        _routeState = _routeState with { Waypoints = waypoints };
        _selectedRouteWaypointId = id;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _resumeRouteAfterStop = false;
        RebuildRoute("route waypoint moved", publishSnapshot: false, trimToPlayer: false);
        PersistSession("автосохранение: waypoint moved", force: true);
        PushRouteSnapshot();
    }

    private void DeleteRouteWaypoint(string id)
    {
        EnsureRouteEditingAllowed();

        var remaining = _routeState.Waypoints
            .Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (remaining.Length == _routeState.Waypoints.Count)
            return;

        _routeState = new RouteState(
            _routeState.DefaultSpeedKmh,
            remaining).RenumberWaypoints();

        _selectedRouteWaypointId = null;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _resumeRouteAfterStop = false;
        RebuildRoute("waypoint deleted", publishSnapshot: false, trimToPlayer: false);
        PersistSession("автосохранение: waypoint deleted", force: true);
        PushRouteSnapshot();
    }

    private void SelectRouteWaypoint(string id)
    {
        var waypoint = _routeState.Waypoints.FirstOrDefault(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        if (waypoint is null)
            return;

        _selectedRouteWaypointId = waypoint.Id;
        RequestSnapshot("route waypoint selected");
    }

    private void SetRouteWaypointSpeed(string id, double speed)
    {
        EnsureRouteEditingAllowed();

        speed = Math.Clamp(
            double.IsFinite(speed) ? speed : RouteState.DefaultSpeedKmhValue,
            RouteState.MinSpeedKmh,
            RouteState.MaxSpeedKmh);

        var changed = false;
        var waypoints = _routeState.Waypoints.Select(item =>
        {
            if (!item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return item;

            changed = true;
            return item with { SpeedKmh = speed };
        }).ToArray();

        if (!changed)
            return;

        _routeState = _routeState with { Waypoints = waypoints };

        if (_routeStoppedWaypointId is string stoppedId &&
            stoppedId.Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            _routeStoppedWaypointId = null;
            _routeStoppedWaypointIndex = null;
            _resumeRouteAfterStop = false;
        }

        RebuildRoute(
            "waypoint speed changed",
            publishSnapshot: false,
            trimToPlayer: false);
        PersistSession("автосохранение: waypoint speed changed", force: true);
        PushRouteSnapshot();
    }

    private void SetRouteWaypointOffRoad(string id, bool offRoad)
    {
        EnsureRouteEditingAllowed();

        var waypoints = _routeState.Waypoints.Select(item =>
            item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                ? item with { IsOffRoad = offRoad }
                : item).ToArray();

        _routeState = _routeState with { Waypoints = waypoints };

        if (_routeStoppedWaypointId is string stoppedId &&
            stoppedId.Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            _routeStoppedWaypointId = null;
            _routeStoppedWaypointIndex = null;
            _resumeRouteAfterStop = false;
        }

        RebuildRoute(
            "waypoint off-road changed",
            publishSnapshot: false,
            trimToPlayer: false);
        PersistSession("автосохранение: waypoint off-road changed", force: true);
        PushRouteSnapshot();
    }

    private void SetRouteDefaultSpeed(double speed)
    {
        EnsureRouteEditingAllowed();
        _routeState = _routeState.WithDefaultSpeed(speed);
        RebuildRoute(
            "route default speed changed",
            publishSnapshot: false,
            trimToPlayer: false);
        PersistSession("автосохранение: route default speed changed", force: true);
        PushRouteSnapshot();
    }

    private void SetRouteEditingEnabled(bool enabled)
    {
        if (enabled && (_runtime.SimulationRunning || _runtime.IsPaused))
        {
            throw new InvalidOperationException(
                "Для редактирования маршрута нужно выключить симуляцию.");
        }

        _routeEditingEnabled = enabled;
        AppLogger.Info(
            "SimulatorForm: режим редактирования маршрута изменён.",
            $"enabled={enabled}");
        RequestSnapshot("route editing " + (enabled ? "enabled" : "disabled"));
    }

    private void SetRouteEnabled(bool enabled)
    {
        _routeMovementLastTick = null;

        if (!enabled)
        {
            _routeEnabled = false;
            SetPlayerMovementIdle();
            _resumeRouteAfterStop = false;
            AppLogger.Info("SimulatorForm: движение по маршруту выключено.");
            PersistSession("автосохранение: движение по маршруту выключено", force: true);
            RequestSnapshot("route movement disabled");
            return;
        }

        if (_routeState.Waypoints.Count == 0)
            throw new InvalidOperationException("Маршрут пуст — движение по нему невозможно.");

        var resumed = _routeStoppedWaypointId is not null;

        if (resumed)
        {
            var stoppedId = _routeStoppedWaypointId!;
            _routeState = _routeState with
            {
                Waypoints = _routeState.Waypoints
                    .Where(item => !item.Id.Equals(
                        stoppedId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };

            // Точка остановки теперь пройдена. Удаляем её без перенумерации:
            // номера остальных точек — часть пользовательской структуры маршрута.
            _routeStoppedWaypointId = null;
            _routeStoppedWaypointIndex = null;
            _routeTargetWaypointId = null;
            _routeTargetWaypointIndex = null;

            if (_routeState.Waypoints.Count == 0)
            {
                _routeEnabled = false;
                _routePlan = RoutePlan.Empty;
                _routeCursor = RouteCursor.Initial;
                _resumeRouteAfterStop = false;

                AppendJournal(
                    "RouteMovementCompleted",
                    DateTimeOffset.UtcNow,
                    "Движение по маршруту",
                    "Маршрут завершён после продолжения с последней остановочной точки.",
                    _hub.Get<PlayerState>("player").Value.Position);

                PersistSession(
                    "автосохранение: маршрут пройден",
                    force: true);
                RequestSnapshot("route movement completed");
                return;
            }
        }

        RebuildRoute(
            resumed
                ? "route movement resumed after stop"
                : "route movement enabled",
            publishSnapshot: false,
            trimToPlayer: !resumed,
            renumberTrimmed: !resumed);

        if (!_routePlan.IsUsable)
        {
            _routeEnabled = false;
            throw new InvalidOperationException(
                "Движение по маршруту недоступно:" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    _routePlan.Errors.Select(error => "• " + error)));
        }

        _routeEnabled = true;
        _resumeRouteAfterStop = false;

        LogRouteMovementStart(resumed);

        AppLogger.Info(
            "SimulatorForm: движение по маршруту включено.",
            $"waypoints={_routeState.Waypoints.Count}; target={CurrentTargetNumberText()}; resumed={resumed}");

        PersistSession(
            resumed
                ? "автосохранение: движение по маршруту продолжено"
                : "автосохранение: движение по маршруту включено",
            force: true);
        RequestSnapshot("route movement enabled");
    }

    private void ClearRoute()
    {
        EnsureRouteEditingAllowed();

        _routeState = RouteState.Empty;
        _routePlan = RoutePlan.Empty;
        _routeCursor = RouteCursor.Initial;
        _selectedRouteWaypointId = null;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _resumeRouteAfterStop = false;
        _routeTargetWaypointIndex = null;
        _routeTargetWaypointId = null;
        _routeTravelRealSeconds = 0d;
        _routeTravelGameSeconds = 0d;
        _routeEnabled = false;
        _routeMovementLastTick = null;
        SetPlayerMovementIdle();
        RequestSnapshot("route cleared");
        PersistSession("автосохранение: route cleared", force: true);
    }

    private void EnsureRouteEditingAllowed()
    {
        if (_runtime.SimulationRunning || _runtime.IsPaused)
        {
            throw new InvalidOperationException(
                "Для редактирования маршрута нужно выключить симуляцию.");
        }

        if (!_routeEditingEnabled)
        {
            throw new InvalidOperationException(
                "Включите «Редактирование» в разделе «Движение по маршруту».");
        }
    }

    private void RebuildRoute(
        string reason,
        bool publishSnapshot = true,
        bool trimToPlayer = false,
        bool renumberTrimmed = false,
        string? anchorWaypointId = null)
    {
        _routeState = _routeState.Normalize();
        var playerPosition = _hub.Get<PlayerState>("player").Value.Position;

        if (_routeState.Waypoints.Count == 0)
        {
            _routePlan = RoutePlan.Empty;
            _routeCursor = RouteCursor.Initial;
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;

            if (publishSnapshot)
                RequestSnapshot("route rebuilt: " + reason);
            return;
        }

        // RoutePlan содержит только ФИКСИРОВАННУЮ часть маршрута. Позиция игрока
        // никогда не записывается в полилинию: динамический сегмент строится только
        // в BuildRouteSnapshot и в RouteMovementEngine.Advance.
        _routePlan = _routePlanner.Build(_routeState);

        if (trimToPlayer && _routePlan.IsUsable && _routeState.Waypoints.Count > 1)
        {
            var preferredWaypointIndex = _routePlan.Errors.Count == 0 &&
                _routePlan.Points.Count > 0
                ? RouteMovementEngine.PreferredWaypointForPlayer(
                    _routePlan,
                    playerPosition)
                // Если ошибка находится уже ПОЗАДИ игрока, не позволяем ей
                // блокировать пересчёт до текущей позиции. После отсечения
                // новый RoutePlan будет построен только для оставшейся структуры.
                : RouteMovementEngine.PreferredForwardWaypoint(
                    _routeState.Waypoints,
                    playerPosition);

            if (preferredWaypointIndex is int cutIndex && cutIndex > 0)
            {
                var removedNumbers = _routeState.Waypoints
                    .Take(cutIndex)
                    .Select((item, index) => item.EffectiveNumber(index))
                    .ToArray();

                _routeState = _routeState.RemoveLeadingWaypoints(cutIndex);

                // Это ЯВНЫЙ пересчёт пользователя (перемещение игрока / загрузка
                // с выбором «пересчитать»), поэтому только здесь нумерация может
                // стать 1..N. Автоматическое прохождение использует trim=false.
                if (renumberTrimmed)
                    _routeState = _routeState.RenumberWaypoints();

                _routePlan = _routePlanner.Build(_routeState);

                AppLogger.Info(
                    "SimulatorForm: ведущие точки маршрута отсечены.",
                    $"reason={reason}; removed={string.Join(",", removedNumbers)}; " +
                    $"renumbered={renumberTrimmed}; remaining={_routeState.Waypoints.Count}");
            }
        }

        if (!_routePlan.IsUsable)
        {
            _routeCursor = RouteCursor.Initial;
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;

            foreach (var error in _routePlan.Errors)
                AppLogger.Warn("SimulatorForm: ошибка маршрута.", error);

            if (publishSnapshot)
                RequestSnapshot("route rebuilt: " + reason);
            return;
        }

        var nextPointIndex = -1;

        // Сохранённая логическая цель используется только как нижняя граница:
        // сам динамический сегмент всё равно начинается от текущего игрока и идёт
        // к ближайшей подходящей фиксированной точке маршрута.
        if (!string.IsNullOrWhiteSpace(anchorWaypointId))
        {
            var targetIndex = _routeState.Waypoints
                .Select((item, index) => (item, index))
                .Where(item => item.item.Id.Equals(
                    anchorWaypointId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => (int?)item.index)
                .FirstOrDefault();

            if (targetIndex is int minTarget)
            {
                var candidates = _routePlan.Points
                    .Select((point, index) => (point, index))
                    .Where(item => item.point.DestinationWaypointIndex >= minTarget)
                    .ToArray();

                nextPointIndex = NearestCandidateRoutePoint(
                    candidates,
                    playerPosition);
            }
        }

        if (nextPointIndex < 0)
        {
            nextPointIndex = RouteMovementEngine.NextRoutePointForPlayer(
                _routePlan,
                playerPosition);
        }

        if (nextPointIndex >= 0)
        {
            _routeCursor = RouteCursor.Initial.ForPoint(
                nextPointIndex,
                _routeLastHeading);
            SetTargetFromRoutePoint(nextPointIndex);
        }
        else
        {
            _routeCursor = RouteCursor.Initial;
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;
        }

        AppLogger.Info(
            "SimulatorForm: маршрут перестроен.",
            $"reason={reason}; waypoints={_routeState.Waypoints.Count}; " +
            $"routePoints={_routePlan.Points.Count}; segments={_routePlan.Segments.Count}; " +
            $"target={CurrentTargetNumberText()}; errors={_routePlan.Errors.Count}");

        if (publishSnapshot)
            RequestSnapshot("route rebuilt: " + reason);
    }

    private static int NearestCandidateRoutePoint(
        IReadOnlyList<(RoutePoint point, int index)> candidates,
        WorldCoordinate position)
    {
        var bestIndex = -1;
        var bestDistance = double.PositiveInfinity;

        foreach (var candidate in candidates)
        {
            var dx = candidate.point.Position.X - position.X;
            var dz = candidate.point.Position.Z - position.Z;
            var distance = dx * dx + dz * dz;

            if (!double.IsFinite(distance))
                continue;

            if (distance < bestDistance - 0.000001d ||
                (Math.Abs(distance - bestDistance) <= 0.000001d &&
                 candidate.index > bestIndex))
            {
                bestDistance = distance;
                bestIndex = candidate.index;
            }
        }

        return bestIndex;
    }

    private void SetTargetFromRoutePoint(int routePointIndex)
    {
        if (routePointIndex < 0 ||
            routePointIndex >= _routePlan.Points.Count)
        {
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;
            return;
        }

        var point = _routePlan.Points[routePointIndex];
        var targetIndex = point.DestinationWaypointIndex;

        if (targetIndex < 0 || targetIndex >= _routeState.Waypoints.Count)
        {
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;
            return;
        }

        _routeTargetWaypointIndex = targetIndex;
        _routeTargetWaypointId = _routeState.Waypoints[targetIndex].Id;
    }

    private string CurrentTargetNumberText()
    {
        if (_routeTargetWaypointId is not string id)
            return "нет";

        for (var index = 0; index < _routeState.Waypoints.Count; index++)
        {
            if (_routeState.Waypoints[index].Id.Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "№" + _routeState.Waypoints[index].EffectiveNumber(index);
            }
        }

        return "нет";
    }

    private void UpdateRouteMovement()
    {
        if (!_routeEnabled || !_runtime.SimulationRunning)
        {
            _routeMovementLastTick = null;
            return;
        }

        if (_routeState.Waypoints.Count == 0 || !_routePlan.IsUsable)
        {
            _routeEnabled = false;
            SetPlayerMovementIdle();
            return;
        }

        if (_routeStoppedWaypointId is not null)
        {
            _routeMovementLastTick = DateTimeOffset.UtcNow;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_routeMovementLastTick is null)
        {
            _routeMovementLastTick = now;
            return;
        }

        var elapsed = Math.Min(
            1d,
            Math.Max(
                0d,
                (now - _routeMovementLastTick.Value).TotalSeconds));
        _routeMovementLastTick = now;

        if (elapsed <= 0d)
            return;

        var current = _hub.Get<PlayerState>("player").Value;
        var previousTargetId = _routeTargetWaypointId;

        var result = RouteMovementEngine.Advance(
            _routeState,
            _routePlan,
            _routeCursor,
            current.Position,
            elapsed);

        _routeTravelRealSeconds += elapsed;
        _routeTravelGameSeconds +=
            elapsed * Math.Max(0d, _runtime.SimulationSpeed);

        _routeCursor = result.Cursor;
        _routeLastHeading = result.HeadingDegrees;

        var nextPlayer = current with
        {
            Position = result.Position,
            SpeedKmh = result.SpeedKmh,
            Heading = result.HeadingDegrees,
            Paused = false
        };

        if (current.Position != nextPlayer.Position ||
            Math.Abs(current.SpeedKmh - nextPlayer.SpeedKmh) > 0.001d ||
            Math.Abs(current.Heading - nextPlayer.Heading) > 0.001d)
        {
            _hub.Get<PlayerState>("player").Set(
                nextPlayer,
                "Движение по маршруту");
        }

        if (current.SpeedKmh > 0.001d &&
            result.SpeedKmh > 0.001d &&
            Math.Abs(current.SpeedKmh - result.SpeedKmh) > 0.001d)
        {
            AppendJournal(
                "RouteSpeedChanged",
                DateTimeOffset.UtcNow,
                "Движение по маршруту",
                $"Изменение скорости: {current.SpeedKmh:0.#} → {result.SpeedKmh:0.#} км/ч.",
                result.Position);
        }

        // Фиксируем пройденные путевые точки ДО их удаления из RouteState,
        // поэтому журнал сохраняет их пользовательские номера.
        foreach (var passedId in result.PassedWaypointIds)
        {
            var passed = _routeState.Waypoints.FirstOrDefault(item =>
                item.Id.Equals(
                    passedId,
                    StringComparison.OrdinalIgnoreCase));

            if (passed is null)
                continue;

            var passedIndex = _routeState.Waypoints
                .Select((item, index) => (item, index))
                .FirstOrDefault(item => item.item.Id.Equals(
                    passed.Id,
                    StringComparison.OrdinalIgnoreCase))
                .index;

            AppendJournal(
                "RouteWaypointPassed",
                DateTimeOffset.UtcNow,
                "Движение по маршруту",
                $"Пройдена точка №{passed.EffectiveNumber(passedIndex)}.",
                result.Position);
        }

        var stoppedId = result.StoppedWaypointId;
        if (stoppedId is not null)
        {
            var stopped = _routeState.Waypoints.FirstOrDefault(item =>
                item.Id.Equals(
                    stoppedId,
                    StringComparison.OrdinalIgnoreCase));

            if (stopped is not null)
            {
                _routeStoppedWaypointId = stopped.Id;
                _routeStoppedWaypointIndex = _routeState.Waypoints
                    .Select((item, index) => (item, index))
                    .FirstOrDefault(item => item.item.Id.Equals(
                        stopped.Id,
                        StringComparison.OrdinalIgnoreCase))
                    .index;
            }
        }

        var hasPassedWaypoints = result.PassedWaypointIds.Count > 0;

        if (hasPassedWaypoints)
        {
            var passedIds = new HashSet<string>(
                result.PassedWaypointIds,
                StringComparer.OrdinalIgnoreCase);

            _routeState = _routeState with
            {
                Waypoints = _routeState.Waypoints
                    .Where(item => !passedIds.Contains(item.Id))
                    .ToArray()
            };

            // Автоматическое прохождение никогда не перенумеровывает оставшиеся
            // точки и никогда дополнительно не отсеивает их.
            if (_routeState.Waypoints.Count > 0)
            {
                RebuildRoute(
                    "waypoint(s) passed",
                    publishSnapshot: false,
                    trimToPlayer: false,
                    renumberTrimmed: false);
            }
            else
            {
                _routePlan = RoutePlan.Empty;
                _routeCursor = RouteCursor.Initial;
                _routeTargetWaypointIndex = null;
                _routeTargetWaypointId = null;
            }
        }
        else if (stoppedId is null)
        {
            SetTargetFromRoutePoint(result.Cursor.NextRoutePointIndex);
        }

        if (stoppedId is not null && _routeStoppedWaypointId is not null)
        {
            var stopped = _routeState.Waypoints.FirstOrDefault(item =>
                item.Id.Equals(
                    _routeStoppedWaypointId,
                    StringComparison.OrdinalIgnoreCase));

            if (stopped is not null)
            {
                _routeTargetWaypointId = stopped.Id;
                _routeTargetWaypointIndex = _routeState.Waypoints
                    .Select((item, index) => (item, index))
                    .FirstOrDefault(item => item.item.Id.Equals(
                        stopped.Id,
                        StringComparison.OrdinalIgnoreCase))
                    .index;

                AppendJournal(
                    "RouteMovementStopped",
                    DateTimeOffset.UtcNow,
                    "Движение по маршруту",
                    $"Остановка на точке №{stopped.EffectiveNumber(
                        _routeTargetWaypointIndex.GetValueOrDefault())}. " +
                    "Движение по маршруту выключено.",
                    result.Position);

                _routeEnabled = false;
                _resumeRouteAfterStop = false;
                SetPlayerMovementIdle();
                PersistSession(
                    "автосохранение: движение по маршруту остановлено",
                    force: true);
                RequestSnapshot("route movement stopped");
                return;
            }
        }

        if (result.Completed || _routeState.Waypoints.Count == 0)
        {
            _routeEnabled = false;
            _routeStoppedWaypointIndex = null;
            _routeStoppedWaypointId = null;
            _routeTargetWaypointIndex = null;
            _routeTargetWaypointId = null;

            AppendJournal(
                "RouteMovementCompleted",
                DateTimeOffset.UtcNow,
                "Движение по маршруту",
                "Завершение маршрута.",
                result.Position);

            PostJson(JsonSerializer.Serialize(new
            {
                type = "route_completed",
                message = "Маршрут пройден"
            }, SnapshotJsonOptions));

            SetPlayerMovementIdle();
            PersistSession(
                "автосохранение: маршрут пройден",
                force: true);
            RequestSnapshot("route movement completed");
            return;
        }

        if (hasPassedWaypoints ||
            previousTargetId != _routeTargetWaypointId)
        {
            PushRouteSnapshot();
        }
    }

    private void SetPlayerMovementIdle()
    {
        var current = _hub.Get<PlayerState>("player").Value;
        _routeLastHeading = current.Heading;

        if (Math.Abs(current.SpeedKmh) > 0.001d)
        {
            _hub.Get<PlayerState>("player").Set(
                current with
                {
                    SpeedKmh = 0d,
                    Heading = _routeLastHeading,
                    Paused = false
                },
                "Движение по маршруту: остановка");
        }

        _routeMovementLastTick = null;
        _conditionsLastRealTick = null;
        _conditionsLastGameElapsed = null;
        _conditionsLastPosition = null;
    }

    private void SetRouteAfterSimulationStateChange()
    {
        _routeMovementLastTick = null;

        if (_routeEnabled)
            SetPlayerMovementIdle();
    }

    private void SetRouteStateAfterLoad(RouteState route, RouteRuntimeState? runtime = null)
    {
        _routeState = (route ?? RouteState.Empty).Normalize();
        _selectedRouteWaypointId = null;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _routeTargetWaypointIndex = null;
        _routeTargetWaypointId = null;
        _routeCursor = RouteCursor.Initial;
        _routeLastHeading = 0d;
        _resumeRouteAfterStop = false;
        _routeTravelRealSeconds = runtime is not null &&
            double.IsFinite(runtime.TravelRealSeconds) &&
            runtime.TravelRealSeconds >= 0d
            ? runtime.TravelRealSeconds
            : 0d;
        _routeTravelGameSeconds = runtime is not null &&
            double.IsFinite(runtime.TravelGameSeconds) &&
            runtime.TravelGameSeconds >= 0d
            ? runtime.TravelGameSeconds
            : 0d;

        _routeEnabled = runtime?.Enabled ?? false;
        _routeEditingEnabled = false;

        if (_routeState.Waypoints.Count == 0)
        {
            _routePlan = RoutePlan.Empty;
            _routeMovementLastTick = null;
            return;
        }

        _routePlan = _routePlanner.Build(_routeState);

        // Идентификатор — главный источник истины. Индекс нужен только как
        // совместимость со старыми сохранениями, где ID ещё не записывался.
        var targetId = runtime?.CurrentTargetWaypointId;
        if (string.IsNullOrWhiteSpace(targetId) &&
            runtime?.CurrentTargetWaypointIndex is int targetIndex &&
            targetIndex >= 0 &&
            targetIndex < _routeState.Waypoints.Count)
        {
            targetId = _routeState.Waypoints[targetIndex].Id;
        }

        var stoppedId = runtime?.StoppedWaypointId;
        if (string.IsNullOrWhiteSpace(stoppedId) &&
            runtime?.Cursor.StoppedAtWaypointIndex is int stoppedIndex &&
            stoppedIndex >= 0 &&
            stoppedIndex < _routeState.Waypoints.Count)
        {
            stoppedId = _routeState.Waypoints[stoppedIndex].Id;
        }

        _routeStoppedWaypointId = stoppedId;
        _routeStoppedWaypointIndex = IndexOfWaypoint(stoppedId);

        if (_routePlan.IsUsable)
        {
            var nextPointIndex = -1;

            if (!string.IsNullOrWhiteSpace(_routeStoppedWaypointId))
            {
                var stopIndex = _routeStoppedWaypointIndex;
                if (stopIndex is int stop)
                {
                    var candidates = _routePlan.Points
                        .Select((point, index) => (point, index))
                        .Where(item => item.point.DestinationWaypointIndex == stop)
                        .ToArray();

                    nextPointIndex = NearestCandidateRoutePoint(
                        candidates,
                        _hub.Get<PlayerState>("player").Value.Position);
                }
            }
            else if (!string.IsNullOrWhiteSpace(targetId))
            {
                var targetIndex = IndexOfWaypoint(targetId);
                if (targetIndex is int target)
                {
                    var candidates = _routePlan.Points
                        .Select((point, index) => (point, index))
                        .Where(item => item.point.DestinationWaypointIndex == target)
                        .ToArray();

                    nextPointIndex = NearestCandidateRoutePoint(
                        candidates,
                        _hub.Get<PlayerState>("player").Value.Position);
                }
            }

            if (nextPointIndex < 0)
            {
                nextPointIndex = RouteMovementEngine.NextRoutePointForPlayer(
                    _routePlan,
                    _hub.Get<PlayerState>("player").Value.Position);
            }

            if (nextPointIndex >= 0)
            {
                _routeCursor = RouteCursor.Initial.ForPoint(
                    nextPointIndex,
                    _routeLastHeading);
                SetTargetFromRoutePoint(nextPointIndex);
            }
        }

        // Если сохранение было сделано на остановке, она остаётся текущей целью.
        // Пользовательский play/resume удалит её отдельным явным действием.
        if (_routeStoppedWaypointId is string stopId &&
            IndexOfWaypoint(stopId) is int stop)
        {
            _routeTargetWaypointId = stopId;
            _routeTargetWaypointIndex = stop;
            _routeCursor = RouteMovementEngine.ProjectCursorToWaypoint(
                _routePlan,
                stop,
                _hub.Get<PlayerState>("player").Value.Position,
                _routeCursor);
        }

        _routeMovementLastTick = null;
    }

    private void UpdatePlayerConditions()
    {
        if (!_runtime.SimulationRunning || _runtime.IsPaused)
        {
            _conditionsLastRealTick = null;
            _conditionsLastGameElapsed = null;
            _conditionsLastPosition = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var clock = _hub.Get<WorldClockState>("sim-time").Value;

        if (_conditionsLastRealTick is null || _conditionsLastGameElapsed is null)
        {
            _conditionsLastRealTick = now;
            _conditionsLastGameElapsed = clock.Elapsed;
            _conditionsLastPosition = _hub.Get<PlayerState>("player").Value.Position;
            return;
        }

        var realSeconds = Math.Max(0d, (now - _conditionsLastRealTick.Value).TotalSeconds);
        var gameSeconds = Math.Max(0d, (clock.Elapsed - _conditionsLastGameElapsed.Value).TotalSeconds);

        _conditionsLastRealTick = now;
        _conditionsLastGameElapsed = clock.Elapsed;

        if (realSeconds <= 0d && gameSeconds <= 0d)
            return;

        var currentPlayer = _hub.Get<PlayerState>("player").Value;
        var moving = currentPlayer.SpeedKmh > 0.001d;

        // Усталость начисляется и за пройденную дистанцию, а не только за
        // игровое время. Игровое время идёт 1:1 с реальным, поэтому «100% за 18
        // часов» давало 5.6% в час — в пределах сессии незаметно, и усталость
        // выглядела неработающей. Дистанция за тик берётся из фактического
        // смещения игрока: так она учитывает и движение по маршруту, и любые
        // другие источники перемещения, без второй бухгалтерии скорости.
        var traveledMeters = 0d;

        if (moving &&
            _conditionsLastPosition is { } previousPosition &&
            _conditionsLastGameElapsed is { } previousElapsed &&
            clock.Elapsed != previousElapsed)
        {
            var dx = currentPlayer.Position.X - previousPosition.X;
            var dz = currentPlayer.Position.Z - previousPosition.Z;
            var distance = Math.Sqrt(dx * dx + dz * dz);

            if (double.IsFinite(distance))
                traveledMeters = Math.Max(0d, distance);
        }

        _conditionsLastPosition = currentPlayer.Position;

        var currentVitals = _hub.Get<PlayerVitalsState>("player-vitals").Value;
        var currentConditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        var update = PlayerConditionEngine.Advance(
            currentVitals,
            currentConditions,
            gameSeconds,
            realSeconds,
            moving,
            sleeping: false,
            traveledMeters);

        if (Equals(update.Vitals, currentVitals) && Equals(update.Conditions, currentConditions))
            return;

        _hub.Get<PlayerVitalsState>("player-vitals").Set(update.Vitals, "Состояние игрока");
        _hub.Get<PlayerConditionState>("player-conditions").Set(update.Conditions, "Состояние игрока");

        if (update.Events.Any(item => item.Kind.Equals("BurnoutApplied", StringComparison.OrdinalIgnoreCase)))
        {
            AppendJournal(
                "BurnoutApplied",
                DateTimeOffset.UtcNow,
                "Состояние игрока",
                "Получен дебафф «Выгорание».",
                _hub.Get<PlayerState>("player").Value.Position);
        }

        if (!_runtime.SimulationRunning)
            RequestSnapshot("player conditions updated");
    }

    private void PushLiveState()
    {
        if (Browser.CoreWebView2 is null || IsDisposed || !IsHandleCreated)
            return;

        var payload = JsonSerializer.Serialize(new
        {
            type = "live_state",
            player = _hub.Get<PlayerState>("player").Value,
            playerVitals = _hub.Get<PlayerVitalsState>("player-vitals").Value,
            playerProgress = _hub.Get<PlayerProgressState>("player-progress").Value,
            conditions = _hub.Get<PlayerConditionState>("player-conditions").Value,
            runtime = _runtime.State,
            simulationRunning = _runtime.SimulationRunning,
            simulationPaused = _runtime.IsPaused,
            simulationSpeed = _runtime.SimulationSpeed,
            route = BuildRouteSnapshot()
        }, SnapshotJsonOptions);

        PostJson(payload);
    }

    private void SetFact(JsonElement root)    private void SetFact(JsonElement root)
    {
        var key = Required(root, "key");
        var state = _hub.Get<FactState>("facts").Value;
        var values = new Dictionary<string, string>(state.Values, StringComparer.OrdinalIgnoreCase)
        {
            [key] = String(root, "value")
        };
        _hub.Get<FactState>("facts").Set(new FactState(values), "Редактор фактов");
    }

    private void SetFlag(JsonElement root)
    {
        var key = Required(root, "key");
        var state = _hub.Get<RuntimeStatesState>("states").Value;
        var flags = new Dictionary<string, bool>(state.Flags, StringComparer.OrdinalIgnoreCase)
        {
            [key] = root.GetProperty("value").GetBoolean()
        };
        _hub.Get<RuntimeStatesState>("states").Set(state with { Flags = flags }, "Редактор состояний");
    }

    private void SetVariable(JsonElement root)
    {
        var key = Required(root, "key");
        var state = _hub.Get<RuntimeStatesState>("states").Value;
        var variables = new Dictionary<string, string>(state.Variables, StringComparer.OrdinalIgnoreCase)
        {
            [key] = String(root, "value")
        };
        _hub.Get<RuntimeStatesState>("states").Set(state with { Variables = variables }, "Редактор состояний");
    }

    private void SetQuestEnabled(JsonElement root) =>
        SetQuestEnabled(Required(root, "campaignId"), Required(root, "questId"),
            root.GetProperty("enabled").GetBoolean());

    private void SetQuestEnabled(string campaignId, string questId, bool enabled)
    {
        _campaignStore.SetQuestEnabled(campaignId, questId, enabled);
        var campaign = _campaignStore.BuildSimulatorCatalog()
            .FirstOrDefault(item => item.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
        _runtime.SetQuestEnabled(questId, enabled && campaign?.Active == true);
    }

    private void SetCampaignActive(JsonElement root) =>
        SetCampaignActive(Required(root, "campaignId"),
            root.GetProperty("active").GetBoolean());

    private void SetCampaignActive(string campaignId, bool active)
    {
        _campaignStore.SetCampaignActive(campaignId, active);
        var campaign = _campaignStore.BuildSimulatorCatalog()
            .FirstOrDefault(item => item.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
        if (campaign is null) return;

        foreach (var quest in campaign.Quests)
        {
            var enabled = campaign.Active && quest.Status == CampaignQuestStatus.Enabled;
            _runtime.SetQuestEnabled(quest.QuestId, enabled);
        }
    }

    private void OpenQuestEditor(JsonElement root)
    {
        var path = Required(root, "path");
        if (!File.Exists(path))
            throw new InvalidOperationException("Quest файл не найден: " + path);

        BeginInvoke(() => _openQuestEditor(path));
    }

    private void OpenCampaignFolder(JsonElement root) =>
        OpenCampaignFolder(Required(root, "campaignId"));

    private void OpenCampaignFolder(string campaignId)
    {
        var campaign = _campaignStore.BuildSimulatorCatalog()
            .FirstOrDefault(item => item.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
        if (campaign is null || !Directory.Exists(campaign.FolderPath))
            throw new InvalidOperationException("Папка кампании не найдена: " + campaignId);

        Process.Start(new ProcessStartInfo
        {
            FileName = campaign.FolderPath,
            UseShellExecute = true
        });
    }

    private void OpenCampaignsWindow()
    {
        if (_campaignsForm is not null && !_campaignsForm.IsDisposed)
        {
            _campaignsForm.SetCatalog(_campaignStore.BuildSimulatorCatalog());
            if (_selectedQuestId.Length > 0)
                _campaignsForm.SelectQuest(_selectedCampaignId, _selectedQuestId);
            _campaignsForm.WindowState = FormWindowState.Normal;
            _campaignsForm.BringToFront();
            _campaignsForm.Activate();
            return;
        }

        _campaignsForm = new CampaignsForm();
        _campaignsForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        // Мир ставится ДО каталога: окно строит раздел мира первым, и без него
        // заголовок и кнопка «ПАПКА» ссылались бы в никуда.
        _campaignsForm.SetWorld(_world);
        _campaignsForm.SetCatalog(_campaignStore.BuildSimulatorCatalog());
        _campaignsForm.CampaignActiveChanged += CampaignsForm_CampaignActiveChanged;
        _campaignsForm.QuestEnabledChanged += CampaignsForm_QuestEnabledChanged;
        _campaignsForm.QuestOpenRequested += CampaignsForm_QuestOpenRequested;
        _campaignsForm.CampaignFolderOpenRequested += CampaignsForm_CampaignFolderOpenRequested;
        _campaignsForm.QuestSelected += CampaignsForm_QuestSelected;
        _campaignsForm.WorldFolderOpenRequested += (_, _) => OpenWorldFolder();
        // ℹ️ в дереве: свойства мира и свойства ИМЕННО ТОЙ кампании, у которой
        // нажата иконка. Оба окна открывает главное окно — оно владеет сторами
        // и правкой, и дублировать эту логику в симуляторе значило бы получить
        // две реализации сохранения свойств.
        _campaignsForm.WorldPropertiesRequested += (_, _) => WorldPropertiesRequested?.Invoke(this, EventArgs.Empty);
        _campaignsForm.WorldExportRequested += (_, _) => WorldExportRequested?.Invoke(this, EventArgs.Empty);
        _campaignsForm.CampaignPropertiesRequested += (_, e) =>
            CampaignPropertiesRequested?.Invoke(this, e);
        _campaignsForm.CampaignExportRequested += (_, e) =>
            CampaignExportRequested?.Invoke(this, e);
        _campaignsForm.QuestExportRequested += (_, e) =>
            QuestExportRequested?.Invoke(this, e);
        _campaignsForm.ImportArchiveRequested += (_, _) => ImportArchiveRequested?.Invoke(this, EventArgs.Empty);
        _campaignsForm.FormClosed += (_, _) =>
        {
            _campaignsForm.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _campaignsForm = null;
            // Окно закрыли: выделение остаётся, но подсвечивать больше нечего.
            RequestSnapshot("campaign window closed");
        };
        if (_selectedQuestId.Length > 0)
            _campaignsForm.SelectQuest(_selectedCampaignId, _selectedQuestId);
        _campaignsForm.Show(this);
    }

    /// <summary>
    /// Просьба открыть свойства мира из дерева кампаний.
    ///
    /// Пробрасывается наружу, а не обрабатывается здесь: правка свойств живёт в
    /// главном окне (оно владеет <see cref="WorldStore"/>), и вторая реализация
    /// сохранения в симуляторе неизбежно разошлась бы с первой.
    /// </summary>
    public event EventHandler? WorldPropertiesRequested;
    public event EventHandler? WorldExportRequested;
    /// <summary>
    /// Просьба открыть настройки приложения. Их владеет MainForm, поэтому
    /// Симулятор только просит — иначе настройки пришлось бы открывать из двух
    /// окон, и «где менять имя» имело бы два разных ответа.
    /// </summary>
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler<CampaignExportRequestedEventArgs>? CampaignExportRequested;
    public event EventHandler<QuestExportRequestedEventArgs>? QuestExportRequested;
    public event EventHandler? ImportArchiveRequested;
    public event EventHandler<WorldSelectionRequestedEventArgs>? WorldSwitchRequested;
    public event EventHandler<CampaignSelectionRequestedEventArgs>? CampaignSelectionRequested;

    /// <summary>Просьба открыть свойства конкретной кампании из дерева.</summary>
    public event EventHandler<CampaignPropertiesRequestedEventArgs>? CampaignPropertiesRequested;

    /// <summary>
    /// Открывает папку мира в проводнике.
    ///
    /// Молчаливое бездействие при отсутствии папки выглядело бы как сломанная
    /// кнопка, поэтому об этом говорится прямо.
    /// </summary>
    private void OpenWorldFolder()
    {
        var folder = _world?.FolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(this,
                "Папка мира не найдена: " + (folder ?? "мир не выбран"),
                "Папка мира", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    /// <summary>
    /// Открывает окно кампаний, дополнительно выделив квест из запроса.
    ///
    /// Вызов приходит при ЛКМ по точке или названию квеста на карте: окно
    /// должно открыться и подсветить именно этот квест оранжевой рамкой.
    /// </summary>
    private void OpenCampaignsWindow(JsonElement root)
    {
        var campaignId = root.TryGetProperty("campaignId", out var campaignNode)
            ? campaignNode.GetString() ?? string.Empty
            : string.Empty;
        var questId = root.TryGetProperty("questId", out var questNode)
            ? questNode.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(questId))
            SelectQuest(campaignId, questId);

        OpenCampaignsWindow();
    }

    /// <summary>
    /// Единая точка смены выделенного квеста. Вызывается и картой, и окном
    /// кампаний, поэтому обе стороны и левый сайдбар всегда согласованы.
    /// </summary>
    private void ActivateDynamicEvent(JsonElement root)
    {
        var instanceId = Required(root, "instanceId");
        if (!_dynamicEventDispatcher.Activate(instanceId))
        {
            PostSaveError("Не удалось активировать динамическое событие: " + instanceId);
            return;
        }

        AppLogger.Info(
            "SimulatorForm: динамическое событие активировано.",
            $"instanceId={instanceId}");
    }

    private void SelectQuest(string campaignId, string questId)
    {
        if (string.IsNullOrWhiteSpace(questId))
            return;

        var campaign = _campaignStore.BuildSimulatorCatalog()
            .FirstOrDefault(item => item.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase))
            ?? _campaignStore.BuildSimulatorCatalog().FirstOrDefault(item =>
                item.Quests.Any(quest => quest.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase)));

        var quest = campaign?.Quests.FirstOrDefault(item =>
            item.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase));

        if (campaign is null || quest is null)
            return;

        _selectedCampaignId = campaign.Id;
        _selectedQuestId = quest.QuestId;
        AppLogger.Info("SimulatorForm: выбран квест.",
            $"campaignId={campaign.Id}; questId={quest.QuestId}; title={quest.Title}");

        if (_campaignsForm is not null && !_campaignsForm.IsDisposed)
            _campaignsForm.SelectQuest(campaign.Id, quest.QuestId);

        RequestSnapshot("quest selected");
    }

    private void CampaignsForm_QuestSelected(object? sender, QuestSelectedEventArgs e)
    {
        SelectQuest(e.CampaignId, e.QuestId);
    }

    private void CampaignsForm_CampaignActiveChanged(object? sender, CampaignActiveChangedEventArgs e)
    {
        SetCampaignActive(e.CampaignId, e.Active);
        RefreshCampaignsWindow();
        RequestSnapshot("campaign window: campaign active changed");
    }

    private void CampaignsForm_QuestEnabledChanged(object? sender, QuestEnabledChangedEventArgs e)
    {
        SetQuestEnabled(e.CampaignId, e.QuestId, e.Enabled);
        RefreshCampaignsWindow();
        RequestSnapshot("campaign window: quest enabled changed");
    }

    private void RefreshCampaignsWindow()
    {
        if (_campaignsForm is null || _campaignsForm.IsDisposed || !_campaignsForm.IsHandleCreated)
            return;

        _campaignsForm.BeginInvoke(() =>
        {
            if (_campaignsForm is null || _campaignsForm.IsDisposed)
                return;

            _campaignsForm.SetCatalog(_campaignStore.BuildSimulatorCatalog());
        });
    }

    private void CampaignsForm_QuestOpenRequested(object? sender, QuestOpenRequestedEventArgs e)
    {
        if (File.Exists(e.Path))
            BeginInvoke(() => _openQuestEditor(e.Path));
    }

    private void CampaignsForm_CampaignFolderOpenRequested(object? sender, CampaignFolderOpenRequestedEventArgs e) =>
        OpenCampaignFolder(e.CampaignId);

    /// <summary>
    /// Квестовый слой карты Simulator.
    ///
    /// В отличие от прежних маркеров активации, сюда попадают ВСЕ квесты
    /// установленных кампаний, а не только те, чей Proximity-триггер доступен:
    /// неактивный квест должен оставаться на карте, но выглядеть иначе.
    ///
    /// Поля собираются здесь, а не в JS: состояние квеста (QuestStatus +
    /// активность Runtime) известно только Host.
    /// </summary>
    private object[] BuildQuestCatalog(SimulatorSnapshot snapshot)
    {
        var statuses = snapshot.QuestStatuses.Quests
            .ToDictionary(item => item.QuestId, StringComparer.OrdinalIgnoreCase);
        var pointsById = new Dictionary<string, WorldPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in snapshot.World.Points)
            pointsById[point.Id] = point;

        return _campaignStore.BuildSimulatorCatalog()
            .Select(campaign => new
            {
                campaignId = campaign.Id,
                campaignName = campaign.Name,
                quests = campaign.Quests.Select(quest =>
                {
                    var status = statuses.TryGetValue(quest.QuestId, out var entry)
                        ? entry.Status
                        : QuestStatus.Available;
                    var step = entry?.Step ?? "available";

                    var resolvedPoint = ResolveActivationPoint(quest.Activation, pointsById);

                    return new
                    {
                        questId = quest.QuestId,
                        questTitle = quest.Title,
                        order = quest.Order,
                        // Имя файла — второй ключ сортировки при равных номерах.
                        fileName = Path.GetFileName(quest.RelativePath),
                        worldPointId = resolvedPoint?.Id ?? quest.Activation?.WorldPointId ?? string.Empty,
                        locationId = quest.Activation?.LocationId ?? string.Empty,
                        radius = quest.Activation?.Radius ?? 0,
                        status = status.ToString(),
                        statusLabel = QuestStatusLabels.GetValueOrDefault(status, status.ToString()),
                        step,
                        stepTitle = step,
                        // «Активен» = квест включён в кампании, а не «его выполняет
                        // Runtime прямо сейчас». Раньше здесь стояла проверка на
                        // активный Runtime, поэтому при остановленной симуляции
                        // ВСЕ квесты рисовались серыми, хотя включённые должны
                        // быть акцентно-оранжевыми. Серыми остаются только
                        // отключённые квесты кампании.
                        active = campaign.Active && quest.Status == CampaignQuestStatus.Enabled,
                        // Отдельный признак «Runtime выполняет этот квест»: он
                        // нужен сайдбару, чтобы отличать включённый квест от
                        // фактически запущенного.
                        runtimeActive = IsRuntimeQuest(quest.QuestId)
                    };
                }).ToArray()
            })
            .ToArray();
    }

    /// <summary>
    /// Выполняет ли Runtime этот квест прямо сейчас.
    ///
    /// Проверка нужна отдельно от «включён в кампании»: включённый квест может
    /// просто ждать активации, а панель должна показывать фактическое состояние.
    /// </summary>
    private WorldPoint? ResolveActivationPoint(QuestActivation? activation, IReadOnlyDictionary<string, WorldPoint> points)
    {
        if (activation is null)
            return null;

        if (!string.IsNullOrWhiteSpace(activation.LocationId))
        {
            return _locationResolver.Resolve(activation.LocationId) ??
                   (string.IsNullOrWhiteSpace(activation.WorldPointId)
                       ? null
                       : points.TryGetValue(activation.WorldPointId, out var fallbackPoint)
                           ? fallbackPoint
                           : null);
        }

        return string.IsNullOrWhiteSpace(activation.WorldPointId)
            ? null
            : points.TryGetValue(activation.WorldPointId, out var point)
                ? point
                : null;
    }

    private bool IsRuntimeQuest(string questId)
    {
        var state = _runtime.State;
        if (state.Status is not (QuestRuntimeStatus.Running or QuestRuntimeStatus.Waiting))
        {
            return false;
        }

        return state.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlyDictionary<QuestStatus, string> QuestStatusLabels =
        new Dictionary<QuestStatus, string>
        {
            [QuestStatus.Available] = "доступен",
            [QuestStatus.Active] = "активен",
            [QuestStatus.Completed] = "завершён",
            [QuestStatus.Cancelled] = "отменён",
            [QuestStatus.Failed] = "провалён",
            [QuestStatus.Archived] = "архив"
        };

    private void SetQuestStatus(JsonElement root)
    {
        var questId = Required(root, "questId");
        var statusText = Required(root, "status");
        if (!Enum.TryParse<QuestStatus>(statusText, true, out var status))
        {
            throw new InvalidOperationException("Неизвестный статус квеста: " + statusText);
        }

        var step = String(root, "step", "available");
        var current = _hub.Get<QuestStatusesState>("quest-statuses").Value;
        var list = current.Quests
            .Where(x => !x.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase))
            .Append(new QuestStatusEntry(questId, status, step))
            .ToArray();

        _hub.Get<QuestStatusesState>("quest-statuses").Set(new QuestStatusesState(list), "Редактор статусов");
    }

    private void SetInventory(JsonElement root)
    {
        var key = Required(root, "key");
        var amount = (int)Number(root, "amount", 0);
        var state = _hub.Get<InventoryState>("inventory").Value;
        var items = new Dictionary<string, int>(state.Items, StringComparer.OrdinalIgnoreCase)
        {
            [key] = Math.Max(0, amount)
        };
        var newIds = new HashSet<string>(state.NewItemIds, StringComparer.OrdinalIgnoreCase);
        if (amount <= 0)
        {
            newIds.Remove(key);
        }

        _hub.Get<InventoryState>("inventory").Set(
            new InventoryState(items, newIds.ToArray()),
            "Редактор инвентаря");
    }

    private void MarkInventorySeen(JsonElement root)
    {
        MarkInventoryItemSeen(Required(root, "itemId"));
    }

    /// <summary>
    /// Снимает значок «новый предмет».
    ///
    /// Вынесено отдельным методом, потому что вызывается из ДВУХ мест: панель
    /// инвентаря в Симуляторе и отдельное окно инвентаря. Две копии этой логики
    /// разошлись бы — например, одна снимала бы значок, а другая нет, и значок
    /// возвращался бы после каждого снимка.
    /// </summary>
    private void MarkInventoryItemSeen(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var state = _hub.Get<InventoryState>("inventory").Value;
        var newIds = new HashSet<string>(state.NewItemIds, StringComparer.OrdinalIgnoreCase);
        if (!newIds.Remove(itemId))
        {
            return;
        }

        _hub.Get<InventoryState>("inventory").Set(
            new InventoryState(state.Items, newIds.ToArray()),
            "Simulator UI");
    }

    private void SetPlayerVitals(JsonElement root)
    {
        var current = _hub.Get<PlayerVitalsState>("player-vitals").Value;
        var next = current with
        {
            Health = Math.Clamp(Number(root, "health", current.Health), 0, current.MaxHealth),
            Energy = Math.Clamp(Number(root, "energy", current.Energy), 0, current.MaxEnergy),
            Hydration = Math.Clamp(Number(root, "hydration", current.Hydration), 0, current.MaxHydration),
            Fatigue = Math.Clamp(Number(root, "fatigue", current.Fatigue), 0, current.MaxFatigue)
        };

        // Стресс — часть условий, а не потребностей, но правится из того же блока
        // «Потребности». Поле необязательное: отсутствие ключа означает
        // «оставить как есть», иначе применение здоровья сбрасывало бы стресс.
        var conditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        if (root.TryGetProperty("stress", out var stressElement) &&
            stressElement.ValueKind == JsonValueKind.Number &&
            stressElement.TryGetDouble(out var stress) &&
            double.IsFinite(stress))
        {
            conditions = conditions with
            {
                Stress = Math.Clamp(stress, 0d, Math.Max(0d, 100d - conditions.CumulativeStress))
            };
        }

        _hub.Get<PlayerVitalsState>("player-vitals").Set(next, "Редактор потребностей");
        _hub.Get<PlayerConditionState>("player-conditions").Set(conditions, "Редактор потребностей");
    }

    /// <summary>
    /// Правка стресса из блока «Потребности».
    ///
    /// Кумулятивный стресс НЕ трогается: по правилам он снимается только
    /// отпуском, и кнопка «Снять стресс» не должна его обнулять — иначе
    /// редактор обходил бы единственный задуманный способ избавления.
    /// </summary>
    private void SetStress(JsonElement root)
    {
        var conditions = _hub.Get<PlayerConditionState>("player-conditions").Value;
        var stress = Number(root, "stress", conditions.Stress);

        _hub.Get<PlayerConditionState>("player-conditions").Set(
            conditions with
            {
                Stress = Math.Clamp(stress, 0d, Math.Max(0d, 100d - conditions.CumulativeStress))
            },
            "Редактор потребностей");
    }

    private void SetPlayerProgress(JsonElement root)
    {
        var current = _hub.Get<PlayerProgressState>("player-progress").Value;
        var next = current with
        {
            Money = Math.Max(0, (int)Number(root, "money", current.Money)),
            Experience = Math.Max(0, (int)Number(root, "experience", current.Experience)),
            Reserve = Math.Max(0, (int)Number(root, "reserve", current.Reserve))
        };
        _hub.Get<PlayerProgressState>("player-progress").Set(next, "Редактор прогресса");
    }

    private void SetCharacterStat(JsonElement root)
    {
        var key = Required(root, "stat");
        var current = _hub.Get<CharacterState>("character").Value;
        var stats = new Dictionary<string, int>(current.Stats, StringComparer.OrdinalIgnoreCase)
        {
            [key] = Math.Clamp((int)Number(root, "value", 5), 0, 10)
        };
        _hub.Get<CharacterState>("character").Set(
            current with { Stats = stats },
            "Редактор персонажа");
    }

    private void SetReputation(JsonElement root)
    {
        var key = Required(root, "key");
        var amount = (int)Number(root, "amount", 0);
        var channel = _hub.Get<ReputationState>("reputation");
        channel.Set(channel.Value.WithValue(key, amount), "Редактор репутации");
    }

    private void SetTelemetry(JsonElement root)
    {
        var old = _hub.Get<TelemetryState>("telemetry").Value;
        _hub.Get<TelemetryState>("telemetry").Set(
            old with
            {
                SpeedKmh = Number(root, "speed", old.SpeedKmh),
                EngineRpm = Number(root, "rpm", old.EngineRpm),
                Throttle = Number(root, "throttle", old.Throttle),
                Brake = Number(root, "brake", old.Brake),
                Steering = Number(root, "steering", old.Steering),
                FuelPercent = Number(root, "fuel", old.FuelPercent),
                EngineTemperature = Number(root, "engineTemperature", old.EngineTemperature),
                CabinTemperature = Number(root, "cabinTemperature", old.CabinTemperature),
                DamageCabPercent = Number(root, "damageCab", old.DamageCabPercent),
                DamageEnginePercent = Number(root, "damageEngine", old.DamageEnginePercent),
                DamageTransmissionPercent = Number(root, "damageTransmission", old.DamageTransmissionPercent),
                DamageWheelPercent = Number(root, "damageWheel", old.DamageWheelPercent),
                HornPressed = root.TryGetProperty("horn", out var horn) ? horn.GetBoolean() : old.HornPressed
            },
            "Редактор телеметрии");
    }

    private void SleepPlayer(bool fullSleep)
    {
        if (_routeEnabled)
        {
            PostSaveError("Перед сном выключите «Движение по маршруту».");
            return;
        }

        var hours = fullSleep ? 4 : 6;
        var currentVitals = _hub.Get<PlayerVitalsState>("player-vitals").Value;
        var currentConditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        var update = PlayerConditionEngine.Sleep(
            currentVitals,
            currentConditions,
            hours,
            fullSleep);

        _hub.Get<PlayerVitalsState>("player-vitals").Set(
            update.Vitals,
            fullSleep ? "Полноценный сон" : "Полевой сон");
        _hub.Get<PlayerConditionState>("player-conditions").Set(
            update.Conditions,
            fullSleep ? "Полноценный сон" : "Полевой сон");

        var clockChannel = _hub.Get<WorldClockState>("sim-time");
        var clock = clockChannel.Value;
        var nextElapsed = clock.Elapsed + TimeSpan.FromHours(hours);
        clockChannel.Set(
            clock with { Elapsed = nextElapsed },
            fullSleep ? "Полноценный сон" : "Полевой сон");

        AppendJournal(
            fullSleep ? "FullSleep" : "FieldSleep",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            fullSleep
                ? "Полноценный сон: 4 игровых часа."
                : "Полевой сон: 6 игровых часов.");

        _conditionsLastRealTick = DateTimeOffset.UtcNow;
        _conditionsLastGameElapsed = nextElapsed;
        _conditionsLastPosition = _hub.Get<PlayerState>("player").Value.Position;

        PersistSession("автосохранение: сон", force: true);
        RequestSnapshot("player sleep");
    }

    private void SetEnvironment(JsonElement root)
    {
        var old = _hub.Get<EnvironmentState>("environment").Value;
        _hub.Get<EnvironmentState>("environment").Set(
            old with
            {
                Weather = String(root, "weather", old.Weather),
                RainPercent = Number(root, "rain", old.RainPercent),
                GameTime = String(root, "gameTime", old.GameTime),
                VisibilityMeters = Number(root, "visibility", old.VisibilityMeters)
            },
            "Редактор окружения");
    }

    /// <summary>
    /// Устанавливает игровые дату и время.
    ///
    /// Дата и время в интерфейсе — это одно значение, а канал времени хранит
    /// стартовую дату и прошедшее время. Приводим одно к другому: стартовая дата
    /// остаётся, а прошедшее время пересчитывается так, чтобы «сейчас» совпало
    /// с введённым моментом. Если введённое время раньше стартовой даты, старт
    /// сдвигается — иначе прошедшее время было бы отрицательным.
    ///
    /// Дата передаётся строкой ISO: разбирать «31.12.2026» на стороне Host
    /// значило бы дублировать формат, который уже задан в интерфейсе.
    /// </summary>
    private void SetWorldTime(JsonElement root)
    {
        var text = String(root, "moment", string.Empty);
        if (string.IsNullOrWhiteSpace(text) ||
            !DateTimeOffset.TryParse(text, null, DateTimeStyles.None, out var moment))
        {
            PostSaveError("Не удалось разобрать игровую дату и время: " + text);
            return;
        }

        var channel = _hub.Get<WorldClockState>("sim-time");
        var clock = channel.Value;

        // Момент трактуется как «настенное» время мира без часового пояса:
        // пояс машины к игровому календарю отношения не имеет.
        var target = new DateTimeOffset(moment.DateTime, TimeSpan.Zero);
        var start = clock.StartDate;
        var elapsed = target - start;

        if (elapsed < TimeSpan.Zero)
        {
            start = target;
            elapsed = TimeSpan.Zero;
        }

        channel.Set(clock with { StartDate = start, Elapsed = elapsed }, "Редактор игрового времени");

        AppLogger.Info("SimulatorForm: установлено игровое время.",
            $"moment={target:yyyy-MM-dd HH:mm}; startDate={start:yyyy-MM-dd HH:mm}; elapsed={elapsed}");
    }

    /// <summary>
    /// Сохраняет стартовые условия мира в файл кампании.
    ///
    /// В кампанию попадают только свойства МИРА: геокоордината для астрономии,
    /// дата старта и погода с видимостью. Текущее прохождение (факты, инвентарь,
    /// позиция игрока) в кампанию не пишется: для этого есть сохранения.
    /// </summary>
    private void SaveWorldToCampaign(JsonElement root)
    {
        if (_campaignStore.IsReadOnly)
        {
            PostSaveError("Кампания открыта только для чтения (режим CI test).");
            return;
        }

        var campaign = _campaignStore.ActiveRecord();
        if (campaign is null)
        {
            PostSaveError("Активная кампания не найдена.");
            return;
        }

        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var environment = _hub.Get<EnvironmentState>("environment").Value;

        // Гео-координата — НЕОБЯЗАТЕЛЬНЫЙ параметр: отсутствие означает «не
        // менять». Это защищает от порчи файла, если поле пришло пустым или
        // отсутствует: раньше пустая строка превращалась в 0 и стирала координату.
        GeoCoordinate? geo = campaign.Definition.Geo;
        if (root.TryGetProperty("latitude", out _) || root.TryGetProperty("longitude", out _))
        {
            var latitude = OptionalDouble(root, "latitude");
            var longitude = OptionalDouble(root, "longitude");

            if (latitude is null || longitude is null)
            {
                PostSaveError("Широта и долгота должны быть заданы обе.");
                return;
            }

            var candidate = new GeoCoordinate(latitude.Value, longitude.Value);
            if (!candidate.IsValid)
            {
                PostSaveError(
                    $"Недопустимая гео-координата: широта {latitude}, долгота {longitude}. " +
                    "Широта от -90 до 90, долгота от -180 до 180.");
                return;
            }

            geo = candidate;
        }

        var world = campaign.Definition with
        {
            Geo = geo,
            // Стартовая дата мира, а не текущий момент: кампания описывает, с чего
            // начинается прохождение. Текущее время хранится в сохранениях.
            StartDate = clock.StartDate,
            StartConditions = new WorldStartConditions(
                environment.Weather,
                environment.RainPercent,
                environment.VisibilityMeters)
        };

        _campaignStore.SaveWorldSettings(campaign.Definition.Id, world);
        PostWorldSettings("saved", "Стартовые условия мира сохранены в кампанию «" + campaign.Definition.Name + "».");
    }

    /// <summary>
    /// Загружает стартовые условия мира из кампании в симуляцию.
    ///
    /// Загружаются они БЕЗ старта прохождения: симуляция может быть выключена, и
    /// это нормально — пользователь проверяет настройки мира. Время при этом
    /// ставится на стартовую дату кампании, чтобы увидеть мир «с начала».
    /// </summary>
    private void LoadWorldFromCampaign()
    {
        if (!ApplyWorldFromCampaign("загрузка из кампании"))
            return;

        var campaign = _campaignStore.ActiveRecord();
        PostWorldSettings("loaded",
            "Стартовые условия загружены из кампании «" + campaign!.Definition.Name + "».");
    }

    /// <summary>
    /// Приводит мир симуляции к стартовым условиям кампании.
    ///
    /// Общий метод для двух действий: «Загрузить из кампании» и «Сбросить».
    /// Оба обязаны давать одинаковый результат — мир «как в кампании», и разница
    /// только в том, что сброс ещё и очищает прохождение. Раньше сброс свойства
    /// мира не возвращал, поэтому испорченная правка в Окружении выглядела как
    /// невосстановимая: ни загрузка из кампании, ни сброс её не отменяли.
    ///
    /// Возвращает false, если активной кампании нет.
    /// </summary>
    private bool ApplyWorldFromCampaign(string reason)
    {
        var campaign = _campaignStore.ActiveRecord();
        if (campaign is null)
            return false;

        var definition = campaign.Definition;
        var conditions = definition.StartConditions;

        if (conditions is not null)
        {
            var environment = _hub.Get<EnvironmentState>("environment").Value;
            _hub.Get<EnvironmentState>("environment").Set(
                environment with
                {
                    Weather = conditions.Weather ?? environment.Weather,
                    RainPercent = conditions.RainPercent ?? environment.RainPercent,
                    VisibilityMeters = conditions.VisibilityMeters ?? environment.VisibilityMeters
                },
                "Кампания");
        }

        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var start = definition.StartDate ?? GameCalendar.DefaultStartDate;
        _hub.Get<WorldClockState>("sim-time").Set(
            clock with { StartDate = start, Elapsed = TimeSpan.Zero },
            "Кампания");

        AppLogger.Info("SimulatorForm: мир приведён к стартовым условиям кампании.",
            $"reason={reason}; campaignId={definition.Id}; startDate={start:yyyy-MM-dd HH:mm}; " +
            $"weather={conditions?.Weather}; geo={definition.Geo}");

        return true;
    }

    private void PostWorldSettings(string action, string message)
    {
        var campaign = _campaignStore.ActiveRecord();
        PostJson(JsonSerializer.Serialize(new
        {
            type = "world_settings",
            action,
            message,
            campaignId = campaign?.Definition.Id ?? string.Empty,
            campaignName = campaign?.Definition.Name ?? string.Empty,
            latitude = campaign?.Definition.Geo?.Latitude,
            longitude = campaign?.Definition.Geo?.Longitude,
            readOnly = _campaignStore.IsReadOnly
        }, SnapshotJsonOptions));
    }

    private void InterfaceDialogueContinue(JsonElement root)
    {
        var dialogue = _hub.Get<InterfaceState>("interfaces").Value.ActiveDialogue;
        if (dialogue is null)
            throw new InvalidOperationException("Активный интерфейс диалога отсутствует.");

        var requestId = String(root, "requestId");
        if (!string.Equals(dialogue.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запрос интерфейса диалога уже неактуален.");

        _hub.Events.Publish(new SimulatorEvent(
            "DialogueContinue",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["requestId"] = dialogue.RequestId
            }));
    }

    private void InterfaceChoice(JsonElement root)
    {
        var dialog = _hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;
        if (dialog is null)
        {
            throw new InvalidOperationException("Активный интерфейс выбора отсутствует.");
        }

        var requestId = String(root, "requestId");
        if (!string.Equals(dialog.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Запрос интерфейса уже неактуален.");
        }

        var index = (int)Number(root, "index", 0);
        if (index < 1 || index > dialog.Options.Count)
        {
            throw new InvalidOperationException("Недопустимый номер варианта выбора.");
        }

        var option = dialog.Options[index - 1];
        _hub.Events.Publish(new SimulatorEvent(
            "ChoiceSelected",
            DateTimeOffset.UtcNow,
            "Interface",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["requestId"] = dialog.RequestId,
                ["index"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["optionId"] = option.Id
            }));
    }

    private void EmitEvent(JsonElement root)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("payload", out var payloadNode) && payloadNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in payloadNode.EnumerateObject())
            {
                payload[property.Name] = property.Value.ToString();
            }
        }

        _hub.Events.Publish(new SimulatorEvent(
            Required(root, "eventType"),
            DateTimeOffset.UtcNow,
            String(root, "source", "Simulator"),
            payload));
    }

    /// <summary>
    /// Запоминает раскрытые разделы правого сайдбара.
    ///
    /// Вызывается на КАЖДОЕ переключение, без задержек: щелчок по заголовку —
    /// редкое действие, а отложенная запись могла бы не успеть до закрытия окна,
    /// и раздел «не запомнился». Пишется только изменение: сравнение списков
    /// избавляет от перезаписи файла на каждое открытие сайдбара.
    /// </summary>
    private void SetSidebarSections(JsonElement root)
    {
        if (!root.TryGetProperty("sections", out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var requested = new List<string>();

        foreach (var item in sections.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                requested.Add(value);
        }

        // Повторная запись того же списка — не изменение: файл настроек не
        // трогается, иначе каждая перерисовка сайдбара писала бы на диск.
        if (requested.Count == _sidebarSections.Count &&
            requested.SequenceEqual(_sidebarSections, StringComparer.Ordinal))
        {
            return;
        }

        _sidebarSections = requested;

        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { SidebarSections = _sidebarSections });

        AppLogger.Info(
            "SimulatorForm: раскрытые разделы сайдбара сохранены.",
            $"sections={_sidebarSections.Count}");
    }

    private void DetachJournal()
    {        QuestLogger.Info("Journal: отделение журнала.");
        _journalDetached = true;
        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { JournalDetached = _journalDetached });
        OpenJournalWindow();
        QuestLogger.Info("Journal: настройка сохранена.", QuestLogger.Json(new { journalDetached = _journalDetached }));
        PushSnapshot();
    }

    private void ReturnJournalToSidebar()
    {
        QuestLogger.Info("Journal: возврат журнала в сайдбар.");
        _journalDetached = false;
        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { JournalDetached = _journalDetached });

        if (_journalForm is not null)
        {
            _journalForm.ReturnToSidebarRequested -= JournalForm_ReturnToSidebarRequested;
            _journalForm.Close();
            _journalForm = null;
        }

        QuestLogger.Info("Journal: настройка сохранена.", QuestLogger.Json(new { journalDetached = _journalDetached }));
        PushSnapshot();
    }

    private void OpenJournalWindow()
    {
        QuestLogger.Info("Journal: открытие окна.", QuestLogger.Json(new
        {
            entryCount = _journalEntries.Count,
            detached = _journalDetached
        }));

        if (_journalForm is not null && !_journalForm.IsDisposed)
        {
            _journalForm.WindowState = FormWindowState.Normal;
            _journalForm.BringToFront();
            _journalForm.Activate();
            _journalForm.SetEntries(_journalEntries);
            return;
        }

        _journalForm = new JournalForm();
        _journalForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _journalForm.CoordinateClicked += JournalForm_CoordinateClicked;
        _journalForm.SetEntries(_journalEntries);
        _journalForm.ReturnToSidebarRequested += JournalForm_ReturnToSidebarRequested;
        _journalForm.FormClosed += (_, _) =>
        {
            _journalForm!.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _journalForm.CoordinateClicked -= JournalForm_CoordinateClicked;
            _journalForm = null;
        };
        _journalForm.Show(this);
        QuestLogger.Info("Journal: native окно показано.", QuestLogger.Json(new { entryCount = _journalEntries.Count }));
    }

    private void JournalForm_CoordinateClicked(WorldCoordinate position)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => FocusJournalCoordinate(position)));
            return;
        }

        FocusJournalCoordinate(position);
    }

    private void JournalForm_ReturnToSidebarRequested(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)ReturnJournalToSidebar);
            return;
        }

        ReturnJournalToSidebar();
    }

    private void AppendJournal(
        string eventType,
        DateTimeOffset timestamp,
        string source,
        string message,
        WorldCoordinate? coordinate = null)
    {
        coordinate ??= _hub.Get<PlayerState>("player").Value.Position;
        _journalEntries.Insert(
            0,
            new SimulatorJournalEntry(eventType, timestamp, source, message, coordinate));
        if (_journalEntries.Count > 250)
        {
            _journalEntries.RemoveRange(250, _journalEntries.Count - 250);
        }

        QuestLogger.Info("Journal: событие добавлено.", QuestLogger.Json(new
        {
            eventType,
            timestamp,
            source,
            message,
            coordinate,
            entryCount = _journalEntries.Count,
            detached = _journalDetached
        }));

        if (Browser.CoreWebView2 is not null && IsHandleCreated && !IsDisposed)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "journal_entry",
                entry = _journalEntries[0]
            }, SnapshotJsonOptions));
        }

        RequestJournalRefresh();
    }

    private void QuestGraph_Changed(object? sender, EventArgs e)
    {
        AppLogger.Info("SimulatorForm: Quest Graph изменён.", $"nodes={_questGraph.Value.Nodes.Count}; connections={_questGraph.Value.Connections.Count}");
        LogQuestGraph("graph_changed");

        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        RequestSnapshot("quest graph changed");
    }

    private void Runtime_Published(object? sender, QuestRuntimeEvent e)
    {
        AppendJournal(e.EventType, e.Timestamp, e.Source, e.Message);

        QuestLogger.Info("Quest Runtime: событие опубликовано.", QuestLogger.Json(new
        {
            stage = e.EventType,
            eventType = e.EventType,
            timestamp = e.Timestamp,
            source = e.Source,
            nodeId = e.NodeId,
            runtimeState = _runtime.State,
            message = e.Message
        }));

        if (e.EventType.Equals("RuntimeStarted", StringComparison.OrdinalIgnoreCase))
            LogQuestGraph("runtime_started");

        if (e.NodeId is not null)
        {
            var graph = _runtime.ActiveGraph ?? _questGraph.Value;
            var node = graph.Nodes.FirstOrDefault(item =>
                item.NodeId.Equals(e.NodeId, StringComparison.OrdinalIgnoreCase));
            if (node is not null)
            {
                QuestLogger.Info("Quest Runtime: активная нода.", QuestLogger.Json(new
                {
                    nodeId = node.NodeId,
                    nodeType = node.NodeType,
                    title = node.Title,
                    parameters = node.Parameters,
                    sockets = node.Sockets
                }));
            }
        }

        AppLogger.Info(
            "Quest Runtime: событие.",
            $"event={e.EventType}; source={e.Source}; node={e.NodeId ?? "<none>"}; status={_runtime.State.Status}; waiting={_runtime.State.WaitingFor ?? "<none>"}; transition={_runtime.State.LastTransition}; message={e.Message}");

        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                PostJson(JsonSerializer.Serialize(new
                {
                    type = "runtime_event",
                    @event = e,
                    runtime = _runtime.State
                }, SnapshotJsonOptions));
                RequestSnapshot("runtime event");
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Events_Published(SimulatorEvent e)
    {
        // Журнал получает только настоящие события мира. Фильтр лежит в домене
        // (SimulatorJournalPolicy) и отсекает транспортные ChannelChanged по
        // каналам, которые меняются десятки раз в секунду: позиция игрока,
        // усталость/стресс, телеметрия, игровое время. Раньше здесь стояла
        // частная проверка одного источника, и внутренние процессы всё равно
        // забивали журнал тысячами строк.
        var shouldJournal = SimulatorJournalPolicy.ShouldJournal(e.EventType, e.Payload);
        if (!shouldJournal)
            return;

        AppendJournal(
            e.EventType,
            e.Timestamp,
            e.Source,
            e.Payload.Count == 0 ? string.Empty : QuestLogger.Json(e.Payload));

        QuestLogger.Info("Simulator: событие опубликовано.", QuestLogger.Json(new
        {
            eventType = e.EventType,
            timestamp = e.Timestamp,
            source = e.Source,
            payload = e.Payload,
            runtimeState = _runtime.State
        }));

        AppLogger.Info(
            "Simulator: событие опубликовано.",
            $"event={e.EventType}; source={e.Source}; payload={QuestLogger.Json(e.Payload)}");

        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                PostJson(JsonSerializer.Serialize(new { type = "event", @event = e }));
                RequestSnapshot("simulator event");
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RequestJournalRefresh()
    {
        if (_journalForm is null || _journalForm.IsDisposed ||
            _journalRefreshScheduled || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _journalRefreshScheduled = true;
        try
        {
            BeginInvoke((Action)(() =>
            {
                _journalRefreshScheduled = false;
                _journalForm?.SetEntries(_journalEntries);
            }));
        }
        catch (InvalidOperationException)
        {
            _journalRefreshScheduled = false;
        }
    }

    private void LogQuestGraph(string stage)
    {
        var graph = _questGraph.Value;
        QuestLogger.Info("QuestGraph: сформирована/загружена логика.", QuestLogger.Json(new
        {
            stage,
            questId = graph.Id,
            name = graph.Name,
            nodeCount = graph.Nodes.Count,
            connectionCount = graph.Connections.Count,
            nodes = graph.Nodes.Select(node => new
            {
                nodeId = node.NodeId,
                nodeType = node.NodeType,
                title = node.Title,
                position = new { node.X, node.Y },
                parameters = node.Parameters,
                sockets = node.Sockets
            }),
            connections = graph.Connections
        }));
    }

    public void RequestSnapshot(string reason)
    {
        if (Browser.CoreWebView2 is null || IsDisposed || !IsHandleCreated ||
            _snapshotRequestScheduled)
        {
            return;
        }

        _snapshotRequestScheduled = true;
        try
        {
            BeginInvoke((Action)(() =>
            {
                _snapshotRequestScheduled = false;
                PushSnapshot();
            }));
        }
        catch (InvalidOperationException)
        {
            _snapshotRequestScheduled = false;
        }
    }

    /// <summary>
    /// Перечитывает квесты кампаний с диска и обновляет карту.
    ///
    /// Каталог квестов (и точка активации вместе с ним) кэшируется в памяти
    /// при построении снимка, поэтому правка файла в редакторе сама по себе
    /// ничего не меняет: нужно перечитать файлы. Вызывается автоматически после
    /// сохранения документа и вручную кнопкой «Обновить квесты» — на случай,
    /// если файл правили вне редактора.
    /// </summary>
    public void ReloadCatalog(string reason)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        _campaignStore.Reload();

        // Включённость квестов живёт в Runtime и пересчитывается из нового
        // каталога: после перечитывания файла квест может быть отключён в
        // кампании, и тогда его нельзя оставлять активным в Runtime.
        SyncRuntimeQuestEnabled();

        AppLogger.Info("SimulatorForm: каталог квестов перечитан.", $"reason={reason}");
        RequestSnapshot("reload catalog: " + reason);
    }

    /// <summary>
    /// Включает режим визуализации Location: набор отобранных точек рисуется
    /// поверх основной карты, а все прочие точки приглушаются.
    ///
    /// Зачем на основной карте: собственная карта редактора локаций не содержит
    /// ориентиров (городов, дорог, признаков мира), поэтому по ней нельзя понять,
    /// ГДЕ оказались точки. Здесь они ложатся на знакомую карту мира, а камера
    /// вписывается в отобранный набор.
    /// </summary>
    public void ShowLocationVisualisation(LocationVisualisationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsDisposed || Browser.CoreWebView2 is null)
        {
            return;
        }

        _locationVisualisation = request;

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        BringToFront();
        Activate();

        AppLogger.Info(
            "SimulatorForm: режим визуализации Location включён.",
            $"title={request.Title}; points={request.Points.Count}; " +
            $"diagnostics={request.Diagnostics.Count}");

        BeginInvoke((Action)(() =>
            PostJson(JsonSerializer.Serialize(new
            {
                type = "location_visualisation",
                title = request.Title,
                points = request.Points,
                diagnostics = request.Diagnostics
            }, SnapshotJsonOptions))));
    }

    /// <summary>
    /// Приводит набор включённых квестов Runtime в соответствие каталогу
    /// кампании.
    ///
    /// Нужен после перечитывания файлов: без него Runtime продолжал бы считать
    /// включённым квест, который в кампании уже отключён.
    /// </summary>
    private void SyncRuntimeQuestEnabled()
    {
        foreach (var campaign in _campaignStore.BuildSimulatorCatalog())
        {
            foreach (var quest in campaign.Quests)
            {
                _runtime.SetQuestEnabled(
                    quest.QuestId,
                    quest.CampaignActive && quest.Status == CampaignQuestStatus.Enabled);
            }
        }
    }

    /// <summary>
    /// Отправляет список сохранений в панель.
    ///
    /// Панель открывается по требованию, поэтому список не живёт в снимке:
    /// пересылать его при каждой перерисовке карты было бы лишней работой (чтение
    /// заголовков всех файлов на каждый кадр).
    /// </summary>
    private void PostSaveList()
    {
        var items = _saveStore.List()
            .Select(item => new
            {
                path = item.Path,
                name = item.Name,
                sizeLabel = item.SizeLabel,
                sizeBytes = item.SizeBytes,
                createdLabel = item.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                gameDateLabel = GameCalendar.FormatDate(item.GameDate),
                gameTimeLabel = GameCalendar.FormatTime(item.GameDate),
                playedLabel = item.PlayedLabel,
                campaignId = item.CampaignId
            })
            .ToArray();

        PostJson(JsonSerializer.Serialize(new
        {
            type = "save_list",
            items,
            root = _saveStore.Root,
            simulationRunning = _runtime.SimulationRunning
        }, SnapshotJsonOptions));
    }

    /// <summary>
    /// Создаёт сохранение текущего состояния.
    ///
    /// Ручное сохранение разрешено в ЛЮБОМ режиме — и при идущей симуляции, и при
    /// выключенной. Это осознанно: игрок формирует разные типы снимков, а
    /// запрещать их «потому что симуляция не идёт» значит отнимать у него
    /// возможность зафиксировать подготовленную вручную сцену.
    /// </summary>
    private void SetMapView(JsonElement root)
    {
        var current = _mapView ?? SimulatorMapViewState.Default;
        _mapView = new SimulatorMapViewState(
            Number(root, "cx", current.CenterX),
            Number(root, "cz", current.CenterZ),
            Number(root, "mpp", current.MetersPerPixel)).Normalize();
    }

    private void CreateSave(JsonElement root)
    {
        var requested = root.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
        var createdAt = DateTimeOffset.UtcNow;

        var header = new SimulationSaveHeader(
            SimulationSaveState.CurrentFormatVersion,
            string.IsNullOrWhiteSpace(requested)
                ? SimulationSaveNaming.DefaultName(createdAt.ToLocalTime())
                : requested!,
            VersionInfo.InformationalVersion,
            createdAt,
            null,
            _hub.Get<WorldClockState>("sim-time").Value.Now,
            CurrentCampaignId(),
            _hub.Get<WorldClockState>("sim-time").Value.Elapsed);

        var item = _saveStore.Create(new SimulationSave(header, CaptureState()), createdAt);
        AppLogger.Info("SimulatorForm: создано сохранение.",
            $"path={item.Path}; size={item.SizeBytes}");

        PostSaveResult("created", item.Path, "Сохранение создано: " + item.Name);
    }

    /// <summary>
    /// Загружает сохранение и ставит симуляцию на ПАУЗУ.
    ///
    /// Симуляция гасится намеренно: загрузка одним движением меняет игрока,
    /// факты, инвентарь и статусы квестов, и если бы симуляция продолжала идти,
    /// эти изменения немедленно вызвали бы срабатывания нод (активация квестов,
    /// события, эффекты). Пользователь должен сначала убедиться, что мир в
    /// ожидаемом состоянии, и продолжить сам.
    ///
    /// Это именно ПАУЗА, а не полная остановка: кнопка play после загрузки
    /// ПРОДОЛЖАЕТ загруженное состояние, а не начинает его заново. О паузе
    /// интерфейс сообщает отдельной подписью под кнопкой «Загрузить».
    /// </summary>
    private void LoadSave(JsonElement root)
    {
        var path = Required(root, "path");
        var save = _saveStore.Load(path);

        // Сначала останавливаем Dispatcher: применение сохранения публикует изменения
        // каналов, и работающий Dispatcher мог бы воспринять их как WorldEvent ещё
        // до того, как получил новое состояние.
        _dynamicEventDispatcher.SetSimulationRunning(false);
        SimulationSaveMapper.Apply(_hub, save.State);
        SetRouteStateAfterLoad(save.State.Route, save.State.RouteRuntime);
        _mapView = save.State.MapView?.Normalize();
        _mapViewRestoreToken++;
        _inventoryPausedSimulation = false;
        // Пауза вместо полной остановки: мир сохранён, продолжить можно одним
        // нажатием. Автосохранение при этом НЕ делается — загрузка не является
        // выключением симуляции.
        _runtime.PauseSimulation();

        // Статусы квестов пришли из снимка: пересчёт из каталога кампании
        // обязателен, иначе включённость квестов в Runtime осталась бы прежней.
        SyncRuntimeQuestEnabled();

        AppLogger.Info("SimulatorForm: сохранение загружено.",
            $"path={path}; name={save.Header.Name}; paused=true");

        PostSaveResult("loaded", path,
            "Загружено: " + save.Header.Name + ". Симуляция на паузе — нажмите play, чтобы продолжить.");
        RequestSnapshot("save loaded");
    }

    /// <summary>
    /// Перезаписывает существующее сохранение текущим состоянием.
    ///
    /// Как и создание, доступно в любом режиме: перезапись — это то же ручное
    /// сохранение, только в существующий слот.
    /// </summary>
    private void OverwriteSave(JsonElement root)
    {
        var path = Required(root, "path");
        var clock = _hub.Get<WorldClockState>("sim-time").Value;

        var header = new SimulationSaveHeader(
            SimulationSaveState.CurrentFormatVersion,
            string.Empty,
            VersionInfo.InformationalVersion,
            DateTimeOffset.UtcNow,
            null,
            clock.Now,
            CurrentCampaignId(),
            clock.Elapsed);

        var item = _saveStore.Overwrite(path, new SimulationSave(header, CaptureState()));
        AppLogger.Info("SimulatorForm: сохранение перезаписано.",
            $"path={item.Path}; size={item.SizeBytes}");

        PostSaveResult("overwritten", item.Path, "Перезаписано: " + item.Name);
    }

    private void DeleteSave(JsonElement root)
    {
        var path = Required(root, "path");
        _saveStore.Delete(path);
        PostSaveResult("deleted", path, "Сохранение удалено.");
    }

    private void RenameSave(JsonElement root)
    {
        var path = Required(root, "path");
        var name = Required(root, "name");

        var item = _saveStore.Rename(path, name);
        AppLogger.Info("SimulatorForm: сохранение переименовано.",
            $"path={item.Path}; name={item.Name}");

        PostSaveResult("renamed", item.Path, "Переименовано в «" + item.Name + "».");
    }

    private int IndexOfWaypoint(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return -1;

        for (var index = 0; index < _routeState.Waypoints.Count; index++)
        {
            if (_routeState.Waypoints[index].Id.Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private int? GetTargetWaypointIndex(RouteCursor cursor)
    {
        if (_routeStoppedWaypointId is string stoppedId)
            return IndexOfWaypoint(stoppedId) is var stopped && stopped >= 0
                ? stopped
                : null;

        if (!cursor.Initialized ||
            cursor.NextRoutePointIndex < 0 ||
            cursor.NextRoutePointIndex >= _routePlan.Points.Count)
            return null;

        var targetIndex =
            _routePlan.Points[cursor.NextRoutePointIndex].DestinationWaypointIndex;

        return targetIndex >= 0 && targetIndex < _routeState.Waypoints.Count
            ? targetIndex
            : null;
    }

    private string? GetTargetWaypointId(RouteCursor cursor)
    {
        var index = GetTargetWaypointIndex(cursor);
        return index is int value &&
               value >= 0 &&
               value < _routeState.Waypoints.Count
            ? _routeState.Waypoints[value].Id
            : null;
    }

    private int RoutePointIndexForWaypoint(int waypointIndex)
    {
        if (waypointIndex < 0 || waypointIndex >= _routeState.Waypoints.Count)
            return -1;

        // Конечная фиксированная точка leg имеет точное WaypointIndex. Это
        // лучший якорь для расчёта накопленной дистанции и ETA.
        for (var index = _routePlan.Points.Count - 1; index >= 0; index--)
        {
            if (_routePlan.Points[index].WaypointIndex == waypointIndex)
                return index;
        }

        for (var index = 0; index < _routePlan.Points.Count; index++)
        {
            if (_routePlan.Points[index].DestinationWaypointIndex == waypointIndex)
                return index;
        }

        return -1;
    }

    private double RemainingRouteDistanceMeters()
    {
        if (!_routePlan.IsUsable || _routePlan.Points.Count == 0)
            return 0d;

        var targetPointIndex = _routeCursor.Initialized
            ? Math.Clamp(
                _routeCursor.NextRoutePointIndex,
                0,
                _routePlan.Points.Count - 1)
            : RouteMovementEngine.NextRoutePointForPlayer(
                _routePlan,
                _hub.Get<PlayerState>("player").Value.Position);

        if (targetPointIndex < 0)
            return 0d;

        var player = _hub.Get<PlayerState>("player").Value.Position;
        var target = _routePlan.Points[targetPointIndex].Position;
        var remaining = Distance2D(player, target);

        for (var index = targetPointIndex + 1;
             index < _routePlan.Points.Count;
             index++)
        {
            remaining += Distance2D(
                _routePlan.Points[index - 1].Position,
                _routePlan.Points[index].Position);
        }

        return Math.Max(0d, remaining);
    }

    private double? RemainingRouteSeconds()
    {
        if (!_routePlan.IsUsable || _routePlan.Points.Count == 0)
            return null;

        var targetWaypointIndex = _routeTargetWaypointIndex;
        if (targetWaypointIndex is int target &&
            target >= 0 &&
            target < _routeState.Waypoints.Count)
        {
            for (var index = target; index < _routeState.Waypoints.Count; index++)
            {
                if (_routeState.Waypoints[index].SpeedKmh <= 0.001d)
                    return null;
            }
        }

        var targetPointIndex = _routeCursor.Initialized
            ? Math.Clamp(
                _routeCursor.NextRoutePointIndex,
                0,
                _routePlan.Points.Count - 1)
            : RouteMovementEngine.NextRoutePointForPlayer(
                _routePlan,
                _hub.Get<PlayerState>("player").Value.Position);

        if (targetPointIndex < 0)
            return null;

        var player = _hub.Get<PlayerState>("player").Value.Position;
        var total = 0d;

        for (var index = targetPointIndex;
             index < _routePlan.Points.Count;
             index++)
        {
            var point = _routePlan.Points[index];
            var from = index == targetPointIndex
                ? player
                : _routePlan.Points[index - 1].Position;
            var distance = Distance2D(from, point.Position);
            var speed = point.TravelSpeedKmh;

            if (speed <= 0.001d)
                return null;

            total += distance / (speed / 3.6d);
        }

        return Math.Max(0d, total);
    }

    private static string FormatRouteDuration(double? seconds)
    {
        if (!seconds.HasValue || !double.IsFinite(seconds.Value))
            return "—";

        var whole = Math.Max(0, (long)Math.Round(seconds.Value));
        var hours = whole / 3600;
        var minutes = (whole % 3600) / 60;
        var sec = whole % 60;

        return hours > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{hours} ч {minutes:00} мин")
            : string.Create(CultureInfo.CurrentCulture, $"{minutes} мин {sec:00} с");
    }

    private void LogRouteMovementStart(bool resumed)
    {
        var target = CurrentTargetNumberText() == "нет"
            ? "нет"
            : "точка " + CurrentTargetNumberText();

        var remainingDistance = RemainingRouteDistanceMeters();
        var remainingReal = RemainingRouteSeconds();
        var remainingGame = remainingReal.HasValue
            ? remainingReal.Value * Math.Max(0d, _runtime.SimulationSpeed)
            : (double?)null;

        AppendJournal(
            resumed ? "RouteMovementResumed" : "RouteMovementStarted",
            DateTimeOffset.UtcNow,
            "Движение по маршруту",
            $"{(resumed ? "Движение по маршруту продолжено" : "Движение по маршруту запущено")}. " +
            $"Точек: {_routeState.Waypoints.Count}; общая длина: " +
            $"{GetTotalRouteDistanceMeters():0.#} м; скорость по умолчанию: " +
            $"{_routeState.DefaultSpeedKmh:0.#} км/ч; осталось {remainingDistance:0.#} м; " +
            $"время до конца: {FormatRouteDuration(remainingGame)} игрового / " +
            $"{FormatRouteDuration(remainingReal)} реального; текущая цель — {target}.",
            _hub.Get<PlayerState>("player").Value.Position);
    }

    private void PushRouteSnapshot(bool fitToRoute = false)
    {
        if (Browser.CoreWebView2 is null || IsDisposed || !IsHandleCreated)
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "route_snapshot",
            route = BuildRouteSnapshot(),
            fitToRoute
        }, SnapshotJsonOptions));
    }

    private object BuildRouteSnapshot()
    {
        var pointDistance = new double[_routePlan.Points.Count];
        for (var index = 1; index < pointDistance.Length; index++)
        {
            pointDistance[index] =
                pointDistance[index - 1] +
                Distance2D(
                    _routePlan.Points[index - 1].Position,
                    _routePlan.Points[index].Position);
        }

        var cumulative = new double[_routeState.Waypoints.Count];
        for (var index = 0; index < cumulative.Length; index++)
        {
            var pointIndex = RoutePointIndexForWaypoint(index);
            cumulative[index] =
                pointIndex >= 0 && pointIndex < pointDistance.Length
                    ? pointDistance[pointIndex]
                    : index == 0 ? 0d : cumulative[index - 1];
        }

        var totalDistance = GetTotalRouteDistanceMeters();
        var remainingDistance = RemainingRouteDistanceMeters();
        var currentDistance = Math.Clamp(
            totalDistance - remainingDistance,
            0d,
            totalDistance);

        var clock = _hub.Get<WorldClockState>("sim-time").Value;
        var speedScale = Math.Max(0d, _runtime.SimulationSpeed);

        var waypoints = _routeState.Waypoints.Select((waypoint, index) =>
        {
            var countdown = EstimateRouteSeconds(index);

            return new
            {
                id = waypoint.Id,
                // index остаётся техническим индексом текущего массива и может
                // изменяться при автоматическом удалении. number — постоянный
                // пользовательский номер и именно он отображается на карте.
                index = index + 1,
                number = waypoint.EffectiveNumber(index),
                x = waypoint.Position.X,
                y = waypoint.Position.Y,
                z = waypoint.Position.Z,
                speedKmh = waypoint.SpeedKmh,
                isOffRoad = waypoint.IsOffRoad,
                distanceFromFirstMeters = cumulative[index],
                estimatedArrivalGameTime = countdown.HasValue
                    ? GameCalendar.FormatTime(
                        clock.Now + TimeSpan.FromSeconds(
                            countdown.Value * speedScale))
                    : "—",
                countdownRealSeconds = countdown
            };
        }).ToArray();

        var routePoints = _routePlan.Points.Select(point =>
        {
            var waypointIndex = point.WaypointIndex;
            return new
            {
                id = point.Id,
                x = point.Position.X,
                y = point.Position.Y,
                z = point.Position.Z,
                waypointIndex,
                destinationWaypointIndex = point.DestinationWaypointIndex,
                waypointNumber =
                    waypointIndex is int wi &&
                    wi >= 0 &&
                    wi < _routeState.Waypoints.Count
                        ? _routeState.Waypoints[wi].EffectiveNumber(wi)
                        : (int?)null,
                travelSpeedKmh = point.TravelSpeedKmh,
                cutWaypointCount = point.CutWaypointCount
            };
        }).ToArray();

        var segments = _routePlan.Segments.Select(segment =>
        {
            var start = _routePlan.Points.FirstOrDefault(item =>
                item.Id == segment.StartPointId);
            var end = _routePlan.Points.FirstOrDefault(item =>
                item.Id == segment.EndPointId);

            return new
            {
                startPointId = segment.StartPointId,
                endPointId = segment.EndPointId,
                lengthMeters = segment.LengthMeters,
                start = start is null ? null : new
                {
                    x = start.Position.X,
                    y = start.Position.Y,
                    z = start.Position.Z
                },
                end = end is null ? null : new
                {
                    x = end.Position.X,
                    y = end.Position.Y,
                    z = end.Position.Z
                }
            };
        }).ToArray();

        object? dynamicSegment = null;
        int? nextRoutePointId = null;

        if (_routeCursor.Initialized &&
            _routeCursor.NextRoutePointIndex >= 0 &&
            _routeCursor.NextRoutePointIndex < _routePlan.Points.Count)
        {
            var target = _routePlan.Points[_routeCursor.NextRoutePointIndex];
            var player = _hub.Get<PlayerState>("player").Value.Position;

            dynamicSegment = new
            {
                targetPointId = target.Id,
                lengthMeters = Distance2D(player, target.Position),
                start = new
                {
                    x = player.X,
                    y = player.Y,
                    z = player.Z
                },
                end = new
                {
                    x = target.Position.X,
                    y = target.Position.Y,
                    z = target.Position.Z
                }
            };
            nextRoutePointId = target.Id;
        }

        return new
        {
            enabled = _routeEnabled,
            defaultSpeedKmh = _routeState.DefaultSpeedKmh,
            selectedWaypointId = _selectedRouteWaypointId,
            stoppedWaypointIndex = _routeStoppedWaypointIndex,
            currentTargetWaypointIndex = _routeTargetWaypointIndex,
            currentTargetWaypointId = _routeTargetWaypointId,
            nextRoutePointId,
            travelTimeRealSeconds = _routeTravelRealSeconds,
            travelTimeGameSeconds = _routeTravelGameSeconds,
            totalDistanceMeters = totalDistance,
            distanceFromFirstWaypointMeters = currentDistance,
            editing = _routeEditingEnabled,
            editingAllowed = !_runtime.SimulationRunning && !_runtime.IsPaused,
            waypoints,
            routePoints,
            segments,
            dynamicSegment,
            // legs оставляем для совместимости с существующими инструментами
            // вставки точки на линии и старым сохранённым WebView.
            legs = _routePlan.Legs.Select(leg => new
            {
                startWaypointIndex = leg.StartWaypointIndex,
                endWaypointIndex = leg.EndWaypointIndex,
                lengthMeters = leg.LengthMeters,
                polyline = leg.Polyline.Select(point => new
                {
                    x = point.X,
                    y = point.Y,
                    z = point.Z
                }).ToArray()
            }).ToArray(),
            errors = _routePlan.Errors,
            fovAngleDegrees = RouteMovementEngine.FovAngleDegrees,
            fovMinLengthMeters = RouteMovementEngine.FovMinLengthMeters,
            fovMaxLengthMeters = RouteMovementEngine.FovMaxLengthMeters
        };
    }

    private double? EstimateRouteSeconds(int targetIndex)
    {
        if (targetIndex < 0 ||
            targetIndex >= _routeState.Waypoints.Count ||
            !_routePlan.IsUsable ||
            _routePlan.Points.Count == 0)
            return null;

        if (_routeStoppedWaypointId is string stoppedId)
        {
            var stoppedIndex = IndexOfWaypoint(stoppedId);
            if (stoppedIndex >= 0 && targetIndex <= stoppedIndex)
                return 0d;
        }

        var targetPointIndex = RoutePointIndexForWaypoint(targetIndex);
        if (targetPointIndex < 0)
            return null;

        var startPointIndex = _routeCursor.Initialized
            ? Math.Clamp(
                _routeCursor.NextRoutePointIndex,
                0,
                _routePlan.Points.Count - 1)
            : RouteMovementEngine.NextRoutePointForPlayer(
                _routePlan,
                _hub.Get<PlayerState>("player").Value.Position);

        if (startPointIndex < 0)
            return null;

        if (targetPointIndex < startPointIndex)
            return 0d;

        var player = _hub.Get<PlayerState>("player").Value.Position;
        var total = 0d;

        for (var index = startPointIndex;
             index <= targetPointIndex;
             index++)
        {
            var point = _routePlan.Points[index];
            var from = index == startPointIndex
                ? player
                : _routePlan.Points[index - 1].Position;
            var distance = Distance2D(from, point.Position);
            var speed = point.TravelSpeedKmh;

            if (speed <= 0.001d)
                return null;

            total += distance / (speed / 3.6d);
        }

        return Math.Max(0d, total);
    }

    private double GetTotalRouteDistanceMeters() =>
        _routePlan.TotalDistanceMeters;

    private static double Distance2D(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private SimulationSaveState CaptureState() =>
        SimulationSaveMapper.Capture(
            _hub,
            CurrentCampaignId(),
            _routeState,
            new RouteRuntimeState(
                _routeCursor,
                _routeTargetWaypointIndex,
                _routeTravelRealSeconds,
                _routeTravelGameSeconds,
                Enabled: _routeEnabled,
                CurrentTargetWaypointId: _routeTargetWaypointId,
                StoppedWaypointId: _routeStoppedWaypointId)) with
        {
            MapView = _mapView?.Normalize()
        };

    /// <summary>
    /// Запускает режим прохождения.    /// <summary>
    /// Запускает режим прохождения.
    ///
    /// Автозагрузка выполняется ОДИН раз при открытии Симулятора
    /// (<see cref="AutoLoadWorld"/>), а не здесь: мир должен быть уже
    /// восстановлен к моменту, когда пользователь жмёт play. Поэтому запуск —
    /// это только включение часов и Runtime.
    /// </summary>
    private void StartSimulation()
    {
        if (_runtime.SimulationRunning)
            return;

        // Пауза и «не запущено» различаются действием play: после паузы мир
        // продолжается, без паузы — начинается с текущего состояния (в которое
        // его уже привела автозагрузка).
        if (_runtime.IsPaused)
        {
            _runtime.ResumeSimulation();
            _dynamicEventDispatcher.SetSimulationRunning(true);

            // Пауза не меняет положение игрока. Курсор маршрута поэтому НИКОГДА
            // не перепроецируется на весь маршрут: это могло выбрать физически
            // ближайший старый leg (например, при пересечении дорог) и отправить
            // игрока обратно. Перепроекция выполняется только после ЯВНОГО ручного
            // перемещения игрока в SetPlayerPosition().
            _routeMovementLastTick = null;
            AppLogger.Info(
                "SimulatorForm: симуляция продолжена после паузы.",
                $"target={(_routeTargetWaypointIndex is int target ? target + 1 : 0)}; " +
                $"leg={_routeCursor.LegIndex}; segment={_routeCursor.SegmentIndex}; " +
                $"progress={_routeCursor.SegmentProgressMeters:0.###}");
        }
        else
        {
            _runtime.SetSimulationRunning(true);
            _dynamicEventDispatcher.SetSimulationRunning(true);
            _routeMovementLastTick = null;
            AppLogger.Info("SimulatorForm: симуляция запущена.");
        }

        RequestSnapshot("simulation started");
    }

    /// <summary>
    /// Полностью выключает симуляцию с автосохранением мира.
    ///
    /// Это единственный путь «выключения» — и кнопка stop, и закрытие окна, и
    /// выход из приложения идут через него, поэтому автосохранение невозможно
    /// забыть в одной из веток.
    /// </summary>
    private void StopSimulation(string reason)
    {
        _inventoryPausedSimulation = false;
        SetRouteAfterSimulationStateChange();

        // Если симуляция уже полностью выключена, «Стоп» не должен создавать
        // новое автосохранение. Пауза считается активным прохождением и при Stop
        // фиксируется как обычное выключение.
        var wasActive = _runtime.SimulationRunning || _runtime.IsPaused;
        _runtime.SetSimulationRunning(false);
        _dynamicEventDispatcher.SetSimulationRunning(false);
        if (wasActive)
            AutosaveWorld(reason);
        RequestSnapshot("simulation stopped");
    }

    /// <summary>
    /// Пауза: мир замирает, автосохранение НЕ делается.
    ///
    /// Пауза — это не выключение: игрок вернётся в то же состояние через play, и
    /// лишняя запись на диск здесь только плодила бы одинаковые снимки.
    /// </summary>
    private void PauseSimulation()
    {
        _runtime.PauseSimulation();
        _dynamicEventDispatcher.SetSimulationRunning(false);
        SetRouteAfterSimulationStateChange();
        AppLogger.Info("SimulatorForm: симуляция поставлена на паузу.");
        RequestSnapshot("simulation paused");
    }

    private void ResumeSimulation()
    {
        _runtime.ResumeSimulation();
        _dynamicEventDispatcher.SetSimulationRunning(true);

        // Resume продолжает сохранённый курсор. Смена режима «пауза → игра» не
        // является ручным перемещением и не должна искать ближайший leg заново.
        _routeMovementLastTick = null;
        AppLogger.Info(
            "SimulatorForm: симуляция продолжена.",
            $"target={(_routeTargetWaypointIndex is int target ? target + 1 : 0)}; " +
            $"leg={_routeCursor.LegIndex}; segment={_routeCursor.SegmentIndex}; " +
            $"progress={_routeCursor.SegmentProgressMeters:0.###}");
        RequestSnapshot("simulation resumed");
    }

    /// <summary>
    /// Автозагрузка последнего автосохранения при открытии Симулятора.
    ///
    /// Симуляция при этом НЕ запускается: игрок сначала видит мир в том
    /// состоянии, в котором его оставил, и только потом решает продолжать.
    /// Пустой слот — нормальная ситуация первого запуска.
    /// </summary>
    private void AutoLoadWorld()
    {
        var session = _saveStore.LoadSession();
        if (session is null)
        {
            AppLogger.Info("SimulatorForm: автосохранения нет, мир берётся из кампании.");
            ApplyWorldFromCampaign("первый запуск без автосохранения");
            return;
        }

        SimulationSaveMapper.Apply(_hub, session.State);
        SetRouteStateAfterLoad(session.State.Route, session.State.RouteRuntime);
        _mapView = session.State.MapView?.Normalize();
        _mapViewRestoreToken++;
        _inventoryPausedSimulation = false;
        SyncRuntimeQuestEnabled();
        _autoSaveAt = session.Header.CreatedAt;
        // Мир восстановлен, но часы должны стоять: иначе время пойдёт само,
        // хотя игрок ещё не нажал play.
        if (_hub.Get<WorldClockState>("sim-time").Value.Running)
        {
            var clock = _hub.Get<WorldClockState>("sim-time").Value;
            _hub.Get<WorldClockState>("sim-time").Set(clock with { Running = false }, "Автозагрузка");
        }

        AppLogger.Info("SimulatorForm: мир восстановлен из автосохранения.",
            $"createdAt={session.Header.CreatedAt:yyyy-MM-dd HH:mm:ss}; " +
            $"gameDate={session.Header.GameDate:yyyy-MM-dd HH:mm}; simulationRunning=false");
    }

    private void PersistSession(string reason, bool force = false)
    {
        if (!_runtime.SimulationRunning && !force)
            return;

        try
        {
            var clock = _hub.Get<WorldClockState>("sim-time").Value;
            var createdAt = DateTimeOffset.UtcNow;
            var header = new SimulationSaveHeader(
                SimulationSaveState.CurrentFormatVersion,
                "session",
                VersionInfo.InformationalVersion,
                createdAt,
                null,
                clock.Now,
                CurrentCampaignId(),
                clock.Elapsed);

            _saveStore.SaveSession(new SimulationSave(header, CaptureState()));
            _autoSaveAt = createdAt;
            AppLogger.Info("SimulatorForm: прохождение записано.",
                $"reason={reason}; gameTime={clock.Now:yyyy-MM-dd HH:mm}; elapsed={clock.Elapsed}");
        }
        catch (Exception ex)
        {
            // Ошибка записи не должна ломать работу симуляции.
            AppLogger.Error("SimulatorForm: не удалось записать прохождение.", ex, "reason=" + reason);
        }
    }

    /// <summary>
    /// Записывает автосохранение мира.
    ///
    /// Вызывается ТОЛЬКО при выключении симуляции (кнопка stop, закрытие окна,
    /// выход из приложения). Пока симуляция выключена, автосохранений нет — иначе
    /// пробы пользователя в «визуальном» режиме попадали бы в мир.
    /// </summary>
    private void AutosaveWorld(string reason) => PersistSession("автосохранение: " + reason, force: true);
    /// <summary>
    /// Id активной кампании для подписи сохранения.
    ///
    /// В сохранении важен для того, чтобы понять, к какому миру относится
    /// прохождение: квесты и точки разных кампаний несовместимы.
    /// </summary>
    private string CurrentCampaignId() =>
        _campaignStore.Records.FirstOrDefault(record => record.Definition.Active)?.Definition.Id
        ?? _campaignStore.Records.FirstOrDefault()?.Definition.Id
        ?? string.Empty;

    private void PostSaveResult(string action, string path, string message) =>
        PostJson(JsonSerializer.Serialize(new
        {
            type = "save_result",
            action,
            path,
            message,
            simulationRunning = _runtime.SimulationRunning,
            simulationPaused = _runtime.IsPaused
        }, SnapshotJsonOptions));

    private void PostSaveError(string message) =>
        PostJson(JsonSerializer.Serialize(new
        {
            type = "save_error",
            message
        }, SnapshotJsonOptions));

    /// <summary>
    /// Данные индикатора светового дня.
    ///
    /// Считает домен: астрономия не должна дублироваться в web-слое, иначе
    /// восход и закат в интерфейсе расходились бы с игровым временем.
    /// </summary>
    private object BuildDaylight(SimulatorSnapshot snapshot)
    {
        var clock = snapshot.Clock;
        var location = CurrentGeo();
        var phase = SolarAstronomy.Describe(clock.Now, location);

        return new
        {
            gameDateLabel = GameCalendar.FormatDate(clock.Now),
            // Время без секунд — для поля ввода: там секунды только мешают.
            gameTimeLabel = GameCalendar.FormatTime(clock.Now),
            // Время с секундами — для часов в шапке: по нему видно, что оно идёт.
            // Отдельное поле, а не одно «на все случаи»: поле ввода не должно
            // показывать и требовать секунды.
            gameClockLabel = GameCalendar.FormatClock(clock.Now),
            seasonLabel = GameCalendar.SeasonName(clock.Now),
            running = clock.Running,
            dayFraction = Math.Round(phase.DayFraction, 4),
            isDay = phase.IsDay,
            isPolarDay = phase.IsPolarDay,
            isPolarNight = phase.IsPolarNight,
            sunriseLabel = SolarAstronomy.DescribeSunrise(phase),
            sunsetLabel = SolarAstronomy.DescribeSunset(phase),
            dayLengthLabel = SolarAstronomy.DescribeDayLength(phase),
            sunAltitude = Math.Round(phase.SunAltitudeDegrees, 1)
        };
    }

    /// <summary>
    /// Геокоордината мира из активной кампании.
    ///
    /// Если кампания её не задала, берётся значение по умолчанию: индикатор
    /// светового дня обязан работать и на кампании без астрономических свойств.
    /// </summary>
    private GeoCoordinate CurrentGeo()
    {
        var campaign = _campaignStore.Records
            .FirstOrDefault(record => record.Definition.Active)
            ?? _campaignStore.Records.FirstOrDefault();

        return campaign?.Definition.Geo ?? GeoCoordinate.CreateDefault();
    }

    /// <summary>
    /// Свойства мира из кампании для блока «Окружение».
    ///
    /// Геокоордината и дата старта нужны интерфейсу, чтобы показать, к какому
    /// миру относится астрономия, а не только результат расчёта.
    /// </summary>
    private object BuildWorldSettings()
    {
        var campaign = _campaignStore.ActiveRecord();
        var geo = campaign?.Definition.Geo;

        return new
        {
            campaignId = campaign?.Definition.Id ?? string.Empty,
            campaignName = campaign?.Definition.Name ?? string.Empty,
            readOnly = _campaignStore.IsReadOnly,
            latitude = geo?.Latitude,
            longitude = geo?.Longitude,
            hasGeo = geo is not null,
            startDateLabel = (campaign?.Definition.StartDate ?? GameCalendar.DefaultStartDate)
                .ToString("dd.MM.yyyy"),
            startTimeLabel = (campaign?.Definition.StartDate ?? GameCalendar.DefaultStartDate)
                .ToString("HH:mm")
        };
    }

    private void LogQuestSnapshot(string stage)
    {
        var state = _runtime.State;
        var graph = _runtime.ActiveGraph ?? _questGraph.Value;
        var node = state.CurrentNodeId is null ? null : graph.Nodes.FirstOrDefault(x =>
            x.NodeId.Equals(state.CurrentNodeId, StringComparison.OrdinalIgnoreCase));

        object? target = null;
        if (node is not null)
        {
            var type = node.NodeType.ToLowerInvariant();
            string? pointId = null;
            string? locationId = null;
            double? radius = null;

            if (type == "interaction")
            {
                locationId = GetParameter(node, "locationId");
                pointId = GetParameter(node, "worldPointId");
                radius = TryGetDouble(GetParameter(node, "triggerRadius"));
            }
            else if ((type is "condition" or "waitforcondition") &&
                     GetParameter(node, "operator").Equals("distancecompare", StringComparison.OrdinalIgnoreCase))
            {
                locationId = GetParameter(node, "locationId");
                pointId = GetParameter(node, "worldPointId", GetParameter(node, "right"));
                radius = TryGetDouble(GetParameter(node, "triggerRadius"));
            }

            var point = !string.IsNullOrWhiteSpace(locationId)
                ? _locationResolver.Resolve(locationId)
                : string.IsNullOrWhiteSpace(pointId)
                    ? null
                    : _hub.Get<WorldState>("world").Value.Points.FirstOrDefault(x =>
                        x.Id.Equals(pointId, StringComparison.OrdinalIgnoreCase));

            if (point is not null || !string.IsNullOrWhiteSpace(pointId) || !string.IsNullOrWhiteSpace(locationId))
            {
                var player = _hub.Get<PlayerState>("player").Value.Position;
                var distance = point is null ? (double?)null : Distance(player, point.Position);
                target = new
                {
                    pointId,
                    locationId,
                    found = point is not null,
                    name = point?.Name,
                    category = point?.Category,
                    position = point?.Position,
                    radius,
                    distance,
                    withinRadius = distance.HasValue && radius.HasValue && distance.Value <= radius.Value,
                    player
                };
            }
        }

        var key = QuestLogger.Json(new
        {
            stage,
            status = state.Status,
            currentNodeId = state.CurrentNodeId,
            waitingFor = state.WaitingFor,
            target
        });

        if (string.Equals(key, _lastQuestSnapshotLogKey, StringComparison.Ordinal))
            return;

        _lastQuestSnapshotLogKey = key;
        QuestLogger.Info("Quest UI: snapshot, ожидаемая цель и Runtime.", QuestLogger.Json(new
        {
            stage,
            questId = graph.Id,
            questName = graph.Name,
            graphNodes = graph.Nodes.Count,
            graphConnections = graph.Connections.Count,
            runtimeState = state,
            currentNode = node is null ? null : new
            {
                nodeId = node.NodeId,
                nodeType = node.NodeType,
                title = node.Title,
                parameters = node.Parameters,
                sockets = node.Sockets
            },
            target
        }));
    }

    private static string GetParameter(QuestNode node, string key, string fallback = "") =>
        node.Parameters.TryGetValue(key, out var value) ? value : fallback;

    private static double? TryGetDouble(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static double Distance(WorldCoordinate a, WorldCoordinate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string Required(JsonElement root, string name)
    {
        var value = String(root, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Не задано значение «{name}».")
            : value;
    }

    private static string String(JsonElement root, string name, string fallback = "")
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null
            ? element.ToString()
            : fallback;
    }

    private static double Number(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : double.TryParse(element.ToString(), out var parsed) ? parsed : fallback;
    }

    /// <summary>
    /// Число, которого может не быть: null вместо подстановки значения по умолчанию.
    ///
    /// Нужен для необязательных параметров (например гео-координаты). Метод
    /// <see cref="Number(JsonElement, string, double)"/> здесь не подходит: он
    /// возвращает fallback, и отличить «не задано» от «задан ноль» невозможно —
    /// именно на этом пустое поле превращалось в координату 0.
    /// </summary>
    private static double? OptionalDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        return double.TryParse(element.ToString(), out var parsed) ? parsed : null;
    }
}
