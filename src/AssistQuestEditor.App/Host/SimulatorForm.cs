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
    private readonly CityBoundaryFileSource _cityBoundaries;
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
    /// Показывать ли в мониторе показателей скорости шкал В ПРОЦЕНТАХ («2%/мин»)
    /// вместо физических величин (ккал/мин, мл/мин). Галочка в шапке монитора.
    ///
    /// Состояние принадлежит НАСТРОЙКАМ, а не странице: оно обязано переживать
    /// перезапуск и обновление версии, а localStorage живёт в профиле WebView2,
    /// который меняется вместе с отпечатком сборки. Страница лишь рисует то, что
    /// прислал Хост, — ровно как с раскрытыми разделами сайдбара.
    /// </summary>
    private bool _percentUnits = true;

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

    /// <summary>Окно «Монитор показателей» (кнопка под шкалами в сайдбаре).</summary>
    private IndicatorsForm? _indicatorsForm;

    /// <summary>Окно «Перки, баффы, скиллы».</summary>
    private PerksForm? _perksForm;

    /// <summary>Окно «Предметы» — каталог всех предметов симулятора.</summary>
    private ItemsForm? _itemsForm;

    /// <summary>
    /// Окно «Профили ETS2» — справочник профилей игры: язык интерфейса, моды,
    /// карты, DLC и сохранения.
    ///
    /// Профили принадлежат ИГРЕ, а не миру AQE, поэтому ссылку держит Симулятор:
    /// окно открывается кнопкой в его шапке, а не из меню редактора, и без
    /// хозяина осталось бы висеть после закрытия Симулятора.
    /// </summary>
    private Ets2ProfilesForm? _ets2ProfilesForm;

    /// <summary>
    /// Имя выбранного в окне профиля ETS2 для подписи под авторством.
    ///
    /// Пустая строка означает «профиль не выбран» — и подпись тогда не
    /// показывается вовсе. Подставлять туда профиль по умолчанию нельзя: выбор
    /// профиля ничего не меняет в мире AQE, и постоянная строка «Профиль ETS2: …»
    /// читалась бы как «симулятор работает с этим профилем игры», чего нет.
    /// </summary>
    private string _ets2ProfileName = string.Empty;

    /// <summary>
    /// Read-only монитор связи симулятора с ETS2. Он не записывает ни байта в
    /// профиль игры или Steam Cloud: собственный ledger и чекпоинты живут в AQE.
    /// </summary>
    private readonly Ets2SyncMonitor _ets2SyncMonitor;
    private readonly Ets2TruckTelemetry _ets2TruckTelemetry;
    private bool _telemetryFollowEnabled;
    private DateTimeOffset? _lastAppliedTelemetrySampleAt;

    /// <summary>Мир, которому принадлежит окно. Null — режим без выбранного мира (CI).</summary>
    private readonly WorldRecord? _world;
    private readonly bool _ets2SyncRequired;

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
        _ets2SyncRequired = world is not null;
        _campaignStore = (campaignStore ?? throw new ArgumentNullException(nameof(campaignStore)))
            .ScopedTo(world?.FolderPath);
        // Симулятор при каждом открытии обязан начинаться выключенным:
        // автозагрузка восстанавливает данные мира, но сама симуляция не стартует.
        _runtime.SetSimulationRunning(false);
        _saveStore = new SimulationSaveStore(
            world is null
                ? AppPaths.SimulationSaveRoot
                : WorldPaths.SavesFolderPath(world.FolderPath));

        _ets2TruckTelemetry = new Ets2TruckTelemetry();

        _ets2SyncMonitor = world is null
            ? new Ets2SyncMonitor(
                "ci",
                "CI",
                null,
                null,
                CaptureState,
                CurrentCampaignId,
                ResetWorldForNewEts2Career)
            : new Ets2SyncMonitor(
                world.Definition.Id,
                world.DisplayName,
                null,
                null,
                CaptureState,
                CurrentCampaignId,
                ResetWorldForNewEts2Career);
        _ets2SyncMonitor.Notification += Ets2SyncMonitor_Notification;
        _ets2SyncMonitor.NewCareerDetected += Ets2SyncMonitor_NewCareerDetected;
        _ets2SyncMonitor.CheckpointReady += Ets2SyncMonitor_CheckpointReady;
        _ets2SyncMonitor.StateChanged += Ets2SyncMonitor_StateChanged;

        _openQuestEditor = openQuestEditor ?? throw new ArgumentNullException(nameof(openQuestEditor));
        _locationResolver = locationResolver ?? throw new ArgumentNullException(nameof(locationResolver));
        _dynamicEventDispatcher = dynamicEventDispatcher ?? throw new ArgumentNullException(nameof(dynamicEventDispatcher));
        _roads = roads ?? new RoadIndex(Array.Empty<RoadSegment>());
        _routePlanner = new RoadRoutePlanner(_roads.Segments, junctions?.ToPoints() ?? Array.Empty<JunctionPoint>());
        _cityBoundaries = new CityBoundaryFileSource();
        // Пользовательские настройки читаются ОДНИМ вызовом: их файл — общий, и
        // два независимых Load() подряд читали бы его дважды на каждое открытие
        // Симулятора.
        var preferences = AppUiPreferencesStore.Load();
        _journalDetached = preferences.JournalDetached;
        _sidebarSections = preferences.SidebarSections is { } savedSections
            ? new List<string>(savedSections)
            : new List<string>();
        _percentUnits = preferences.PercentUnits;
        Opacity = 0;
        GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _questGraph.Changed += QuestGraph_Changed;
        _runtime.Published += Runtime_Published;
        _runtimeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _runtimeTimer.Tick += (_, _) =>
        {
            // Состояние ETS2 читается ДО локального тика симуляции: пауза игры
            // должна заморозить AQE в этом же цикле, а возобновление — продолжить
            // его до следующего шага игрового времени.
            if (Browser.CoreWebView2 is not null)
            {
                _ets2SyncMonitor.Poll();
                ApplyExternalTelemetry();
            }

            UpdateRouteMovement();
            _runtime.Tick();
            _dynamicEventDispatcher.Tick();
            UpdatePlayerConditions();

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

            // Монитор показателей живёт вместе с Симулятором: без хозяина он
            // остался бы с устаревшими данными, обновлять которые некому.
            if (_indicatorsForm is not null)
            {
                _indicatorsForm.Close();
                _indicatorsForm = null;
            }

            // Окна перков и предметов — по той же причине: их содержимое
            // собирает Симулятор, и без него они показывали бы застывший мир.
            if (_perksForm is not null)
            {
                _perksForm.Close();
                _perksForm = null;
            }
            if (_itemsForm is not null)
            {
                _itemsForm.Close();
                _itemsForm = null;
            }

            // Окно профилей ETS2 тоже закрывается вместе с Симулятором: кнопка,
            // которая его открывает, живёт в шапке Симулятора, и оставленное окно
            // было бы сиротой без единого способа к нему вернуться.
            if (_ets2ProfilesForm is not null)
            {
                _ets2ProfilesForm.ProfileSelected -= Ets2ProfilesForm_ProfileSelected;
                _ets2ProfilesForm.Close();
                _ets2ProfilesForm = null;
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

            _ets2SyncMonitor.Notification -= Ets2SyncMonitor_Notification;
            _ets2SyncMonitor.NewCareerDetected -= Ets2SyncMonitor_NewCareerDetected;
            _ets2SyncMonitor.CheckpointReady -= Ets2SyncMonitor_CheckpointReady;
            _ets2SyncMonitor.StateChanged -= Ets2SyncMonitor_StateChanged;
            _ets2SyncMonitor.Dispose();
            _ets2TruckTelemetry.Dispose();

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

        // Сначала восстанавливаем обычное состояние мира, затем ETS2 sync может
        // заменить его только при точном совпадении с наблюдаемым game save.
        _ets2SyncMonitor.Poll();

        PushRoads();
        PushSnapshot();
        PostJson(_worldSelectionJson);
        PostJson(_authorJson);

        // Подпись профиля ETS2 пересылается ПОСЛЕ перезагрузки страницы: её
        // владелец — Host, и без повторной отправки выбранный профиль исчезал бы
        // из шапки при каждом обновлении вида.
        if (_ets2ProfileName.Length > 0)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "ets2_profile_selection",
                name = _ets2ProfileName
            }, SnapshotJsonOptions));
        }

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

        // Скорости изменения шкал едут со снимком: подсказка шкалы показывает,
        // сколько единиц прибавляется за игровую минуту, и без этого Web
        // пришлось бы повторять правила усталости и стресса — то есть заводить
        // вторую их версию, которая неизбежно разойдётся с доменом.
        var conditionRates = BuildConditionRates(snapshot);

        var payload = JsonSerializer.Serialize(new
        {
            type = "snapshot",
            version = VersionInfo.InformationalVersion,
            snapshot,
            // Виталы едут ОТДЕЛЬНО: монитор показателей раскладывает скорость
            // энергии и жидкости на вклады «база × метаболизм × сон», а для
            // этого ему нужен процент метаболизма. Он есть внутри `snapshot`,
            // но у монитора снимок приходит и БЕЗ полного payload (live_state),
            // и читать правило из одного места удобнее одним полем.
            playerVitals = snapshot.PlayerVitals,
            conditionRates,
            // Желудок для монитора: порции в литрах/ккал/мл и их сроки.
            // Считает ДОМЕН — время до конца порции выводится из остатка и
            // ставки, и вторая такая же формула в JavaScript разошлась бы с
            // начислением (см. CharacterDigestionReport).
            digestion = BuildDigestionSnapshot(snapshot),
            // Каталог УПОТРЕБИМОГО для меню желудка (ПКМ по пустому месту).
            //
            // Отдельный список, а не общий `itemCatalog`: тот содержит записи
            // `ItemDefinition` без признака съедобности, и страница не может
            // отличить руду от супа — меню оказалось бы либо пустым, либо с
            // камнями. Признак «этим можно питаться» считает домен.
            consumables = BuildConsumablesCatalog(),
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
            ets2Sync = _ets2SyncMonitor.ViewState,
            ets2SyncRequired = _ets2SyncRequired,
            telemetryFollowEnabled = _telemetryFollowEnabled,
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
            // Галочка «%/время» едет со снимком ПО ТОЙ ЖЕ причине: её состояние
            // тоже живёт в настройках, и монитор обязан показать то, что реально
            // сохранено, а не свою догадку.
            percentUnits = _percentUnits,
            // Режим визуализации Location едет вместе со снимком: снимок
            // перерисовывает всю карту, и без этого набор точек исчезал бы
            // через доли секунды после нажатия «Показать в симуляторе».
            locationVisualisation = _locationVisualisation,
            route = BuildRouteSnapshot(),
            lodging = BuildLodgingSnapshot(),
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

        // Монитор показателей получает тот же json: он читает те же шкалы,
        // условия и скорости изменения.
        if (_indicatorsForm is not null && !_indicatorsForm.IsDisposed)
            _indicatorsForm.SetSnapshotJson(payload);

        // Окна перков и предметов обновляются снимком мира: таймеры баффов и
        // остатки инвентаря меняются вместе с ним, и обновлять их отдельным
        // событием значило бы завести второй повод для перерисовки.
        PushPerks();
        PushItems();
    }

    /// <summary>
    /// Скорости изменения шкал состояния для подсказок интерфейса.
    ///
    /// Считает ДОМЕН по тем же константам, что и начисление, а Хост только
    /// передаёт результат в Web. Второй расчёт в JavaScript неизбежно разошёлся
    /// бы с правилами усталости и стресса — и подсказка врала бы ровно в тот
    /// момент, когда игрок по ней принимает решение.
    ///
    /// Покой/движение берутся из скорости игрока, сон — из того, что Симулятор не
    /// выполняет начисление во время сна.
    /// </summary>
    private object BuildLodgingSnapshot()
    {
        var player = _hub.Get<PlayerState>("player").Value;
        var city = _cityBoundaries.Current.FindCity(
            player.Position.X,
            player.Position.Z);
        var money = _hub.Get<PlayerProgressState>("player-progress").Value;

        return new
        {
            visible = city is not null,
            cityName = city?.CityName,
            price = (int)CharacterVitalsEngine.HotelPrice,
            canAfford = money.Money >= CharacterVitalsEngine.HotelPrice,
            blockedByRoute = _routeEnabled
        };
    }

    /// <summary>
    /// Каталог УПОТРЕБИМЫХ предметов: то, что можно положить в желудок, минуя
    /// поиск в инвентаре и выдачу.
    ///
    /// Автор задал это прямо: «ПКМ на пустом месте желудка открывает список не
    /// инвентаря, а каталог существующих объектов, которые съедобны. Это действие
    /// заменяет поиск предмета, выдачу в инвентарь и нажатие в меню использовать;
    /// эти действия сокращаются до "выбрал — попало в желудок"». Поэтому список
    /// строится из КАТАЛОГА, а не из содержимого инвентаря.
    ///
    /// Признак «годится» считает ДОМЕН (<see cref="CharacterVitalsEngine.CanConsume"/>):
    /// у страницы нет ни пищевых профилей, ни таблицы эффектов, и повторять их в
    /// JavaScript значило бы завести вторую версию правил о том, что съедобно.
    ///
    /// Объём в МИЛЛИЛИТРАХ и признак «влезет в свободное место» тоже приходят
    /// отсюда: правило «объём порции больше свободного места — предмет не
    /// употребить» живёт в домене одним экземпляром
    /// (<see cref="CharacterDigestionReport.Fits"/>), и меню желудка лишь гасит
    /// непомещающиеся пункты по готовому признаку.
    /// </summary>
    private object[] BuildConsumablesCatalog()
    {
        var stomach = _hub
            .Get<PlayerConditionState>("player-conditions")
            .Value
            .Stomach;

        return ItemCatalogFactory.CreateStarter()
            .Where(item => CharacterVitalsEngine.CanConsume(item.Id))
            .Select(item =>
            {
                var profile = CharacterConsumableCatalog.GetProfile(item.Id);

                return (object)new
                {
                    id = item.Id,
                    name = item.Name,
                    category = item.Category,
                    color = item.Color,
                    grams = CharacterConsumableCatalog.GetPortionGrams(item.Id),
                    // Объём порции в миллилитрах — то же число, что займёт место в
                    // желудке и что подписано в меню («Колбаса 200 мл»). Это
                    // ЕДИНСТВЕННАЯ величина объёма: прежде меню печатало из
                    // `profile.WaterMilliliters`, то есть содержимое ВОДЫ, и у
                    // банана рядом с «150 мл порцией» стояло бы «110 мл» —
                    // ровно то расхождение, которое автор принял за ошибку
                    // добавления.
                    volumeMilliliters = CharacterConsumableCatalog.GetPortionMilliliters(item.Id),
                    kilocalories = profile.Kilocalories,
                    waterMilliliters = profile.WaterMilliliters,
                    // Помещается ли в СВОБОДНОЕ место прямо сейчас. Считает домен
                    // по текущему желудку: страница не знает ни вместимости, ни
                    // правила о минимуме в 0,01 доли.
                    fits = CharacterDigestionReport.Fits(stomach, item.Id)
                };
            })
            .ToArray();
    }

    /// <summary>
    /// Пищеварение для монитора показателей: порции, объём и влияние на усвоение.
    ///
    /// Считает ДОМЕН (<see cref="CharacterDigestionReport"/>), а не страница:
    /// срок порции выводится из её остатка и ставки, а ставку задаёт ОДНА общая
    /// пропускная способность пищеварения, поделённая между порциями по их
    /// «лёгкости» (вода легче сухой пищи). Вторая такая же формула в JavaScript
    /// разошлась бы с начислением при первой же правке баланса — ровно тот класс
    /// дефектов, что «потолок 10000».
    /// </summary>
    private static object BuildDigestionSnapshot(SimulatorSnapshot snapshot)
    {
        var conditions = snapshot.Conditions;
        var stomach = conditions.Stomach;
        // Метаболизм берётся ТЕКУЩИЙ: пищеварение пересчитывает ставки на каждом
        // шаге под фактическое состояние, поэтому подсказка обязана читать то же
        // число. Раньше здесь подавался текущий метаболизм, а ставки порций были
        // зафиксированы при приёме еды — съел при 80%, упал до 50%, и строка
        // продолжала обещать «×2,5».
        var metabolismPercent = PlayerConditionScale.ToPercent(
            snapshot.PlayerVitals.Metabolism);

        var portions = CharacterDigestionReport
            .Portions(stomach)
            .Select(portion => new
            {
                itemId = portion.ItemId,
                volumeFraction = portion.VolumeFraction,
                volumeLiters = portion.VolumeLiters,
                // Объём в МИЛЛИЛИТРАХ — рабочая величина интерфейса: и подпись
                // «занято / всего», и ширина плитки считаются в них. Литры
                // остаются полем для совместимости и читаются как есть, но
                // страница миллилитры больше не вычисляет умножением.
                volumeMilliliters = portion.VolumeMilliliters,
                // Ккал и мл — то, что игрок узнаёт с этикетки предмета.
                kilocalories = portion.EnergyKilocalories,
                waterMilliliters = portion.WaterMilliliters,
                remainingGameSeconds = portion.RemainingGameSeconds,
                remainingGameMinutes = portion.RemainingGameMinutes,
                // Доля ВОДЫ в порции: по ней игрок понимает, почему две порции
                // одного объёма уходят за разное время.
                waterContent = portion.WaterContent,
                // СКОРОСТЬ ЭТОЙ ПОРЦИИ: без неё строка предмета в шкале не
                // может показать свой вклад и повторяла бы общий итог
                // пищеварения, приписывая апельсину восстановление жидкости,
                // которого он не даёт. В единицах шкалы за игровую минуту — та
                // же размерность, что у energyPerMinute/hydrationPerMinute ниже.
                energyPerMinute = portion.EnergyPerGameSecond * 60d,
                hydrationPerMinute = portion.HydrationPerGameSecond * 60d
            })
            .ToArray();

        var factors = CharacterDigestionReport
            .MetabolismFactors(metabolismPercent)
            .Select(factor => new
            {
                text = factor.Text,
                useful = factor.Useful
            })
            .ToArray();

        return new
        {
            // Доли, а не только литры: страница раскладывает область пищеварения
            // на всю ширину блока, и рисовать приходится ОТНОСИТЕЛЬНЫМИ размерами.
            volumeFraction = stomach.VolumeFraction,
            occupiedLiters = stomach.OccupiedLiters,
            totalLiters = CharacterDigestion.StomachVolumeLiters,
            // МИЛЛИЛИТРЫ — то, чем подписан блок «занято / всего» и чем отмерена
            // область пищеварения. Считает домен: миллилитр и грамм приравнены в
            // одном месте, и повторять это в JavaScript значило бы завести вторую
            // версию правила об объёме.
            occupiedMilliliters = stomach.OccupiedMilliliters,
            totalMilliliters = CharacterDigestionReport.StomachCapacityMilliliters,
            // Пропускная способность в миллилитрах в час — её показывает первый
            // фактор блока: игрок обязан видеть лимит, из которого считаются
            // сроки порций.
            dryThroughputMillilitersPerHour =
                CharacterDigestion.DryThroughputLitersPerHour * 1000d,
            // Скорости усвоения в единицах шкалы за игровую минуту: их показывает
            // строка «общая динамика усвоения» в шапке блока.
            energyPerMinute = stomach.EnergyPerGameSecond * 60d,
            hydrationPerMinute = stomach.HydrationPerGameSecond * 60d,
            portions,
            factors
        };
    }

    private object BuildConditionRates(SimulatorSnapshot snapshot)
    {
        // скорость. Скорость может быть нулевой в кадре между пакетами или на
        // разгоне, но игрок по-прежнему «едет», и показывать ему «покой» в этот
        // момент нельзя: покой — это ВЫКЛЮЧЕННОЕ движение по маршруту.
        var moving = _routeEnabled;
        var rates = CharacterVitalsEngine.RatesFrom(
            snapshot.PlayerVitals,
            snapshot.Conditions,
            moving,
            sleeping: false,
            snapshot.Clock.Now.TimeOfDay.TotalHours);

        return new
        {
            health = rates.HealthPerGameMinute,
            energy = rates.EnergyPerGameMinute,
            hydration = rates.HydrationPerGameMinute,
            fatigue = rates.FatiguePerGameMinute,
            stress = rates.StressPerGameMinute,
            resilience = rates.ResiliencePerGameMinute,
            metabolism = rates.MetabolismPerGameMinute,
            // Расход энергии и жидкости зависит от МЕТАБОЛИЗМА (0,9 при
            // пониженном, 1,25 при повышенном) и от баффа «Бык» (ещё ×0,5).
            // Монитор показателей раскладывает скорость на вклады и обязан
            // применить ТОТ ЖЕ множитель: иначе базовая строка показывала бы
            // норму без поправки и не сходилась бы с итогом. Считает домен,
            // повторять условие «45/75» в JavaScript нельзя.
            metabolismFactor = CharacterVitalsEngine.MetabolismConsumptionFactor(
                PlayerConditionScale.ToPercent(snapshot.PlayerVitals.Metabolism),
                snapshot.Conditions.Effects.Any(effect =>
                    effect.Id.Equals("bull", StringComparison.OrdinalIgnoreCase) &&
                    effect.RemainingRealSeconds > 0d)),
            // Границы «пониженного» и «повышенного» метаболизма: по ним монитор
            // подписывает вклады расхода. Числа заданы калибровкой — Web их не
            // повторяет, иначе подпись разошлась бы с начислением.
            reducedMetabolismPercent = CharacterVitalsEngine.ReducedMetabolismPercent,
            elevatedMetabolismPercent = CharacterVitalsEngine.ElevatedMetabolismPercent,
            // НОМИНАЛЫ: раньше монитор зашивал «60» двумя литералами, и правка
            // калибровки до него не доезжала — ровно тот дефект, ради которого
            // 45 и 75 уже приходили полями. Теперь от домена приходит и номинал,
            // и процент, на котором пропорциональная надбавка к здоровью
            // становится полной (см. ElevatedBonusShare).
            defaultMetabolismPercent = CharacterVitalsEngine.DefaultMetabolismPercent,
            defaultResiliencePercent = CharacterVitalsEngine.DefaultResiliencePercent,
            elevatedBonusFullPercent =
                CharacterVitalsTuning.ElevatedBonusFullPercent,
            // ДЕЛИТЕЛЬ ШАНСА УСТОЙЧИВОСТИ: по нему монитор считает и подписывает
            // «R/2 = 30% шанс не получить порцию истощения». Самый сильный эффект
            // устойчивости раньше не был виден игроку ни в одной строке.
            exhaustionResilienceDivisor =
                CharacterVitalsTuning.ExhaustionResilienceDivisor,
            // Потолок и скорости возврата устойчивости: монитор показывает их
            // вкладом, и числа обязаны приходить от домена — иначе правка
            // калибровки снова разошлась бы с подписью.
            bumResilienceCapPercent =
                CharacterVitalsTuning.BumResilienceCapPercent,
            resilienceReturnPerQuarterHour =
                CharacterVitalsTuning.ResilienceReturnPercentPerQuarterHour,
            resilienceBumReturnPerQuarterHour =
                CharacterVitalsTuning.ResilienceBumReturnPercentPerQuarterHour
        };
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
        _inventoryForm.InventoryItemUseRequested += InventoryForm_ItemUseRequested;
        _inventoryForm.InventoryItemDropRequested += InventoryForm_ItemDropRequested;
        // Клавиша I и Escape внутри окна закрывают его: страница шлёт просьбу, а
        // закрывает Симулятор — он владеет ссылкой и после закрытия отвечает
        // снимком.
        _inventoryForm.CloseRequested += (_, _) => CloseInventoryWindow();
        _inventoryForm.FormClosed += (_, _) =>
        {
            _inventoryForm.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _inventoryForm.InventoryItemUseRequested -= InventoryForm_ItemUseRequested;
            _inventoryForm.InventoryItemDropRequested -= InventoryForm_ItemDropRequested;
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

    /// <summary>
    /// Открывает окно «Монитор показателей».
    ///
    /// Окно ОДНО: повторный запрос поднимает уже открытое. Симуляцию оно НЕ
    /// ставит на паузу (в отличие от инвентаря): монитор нужен именно во время
    /// хода, чтобы наблюдать, как меняются шкалы.
    /// </summary>
    private void OpenIndicatorsWindow()
    {
        if (_indicatorsForm is not null && !_indicatorsForm.IsDisposed)
        {
            _indicatorsForm.WindowState = FormWindowState.Normal;
            _indicatorsForm.BringToFront();
            _indicatorsForm.Activate();
            return;
        }

        _indicatorsForm = new IndicatorsForm();
        _indicatorsForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _indicatorsForm.CloseRequested += (_, _) => CloseIndicatorsWindow();
        _indicatorsForm.NavigationRequested += IndicatorsForm_NavigationRequested;
        _indicatorsForm.StomachActionRequested += IndicatorsForm_StomachActionRequested;
        _indicatorsForm.FormClosed += (_, _) =>
        {
            _indicatorsForm.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _indicatorsForm.NavigationRequested -= IndicatorsForm_NavigationRequested;
            _indicatorsForm.StomachActionRequested -= IndicatorsForm_StomachActionRequested;
            _indicatorsForm = null;
        };

        _indicatorsForm.Show(this);

        // Снимок отправляется сразу: окно, открытое после последнего обновления
        // мира, иначе показывало бы пустой список.
        PushSnapshot();

        AppLogger.Info("SimulatorForm: окно монитора показателей открыто.",
            $"size={_indicatorsForm.Width}x{_indicatorsForm.Height}");
    }

    /// <summary>Закрывает окно монитора показателей, если оно открыто.</summary>
    private void CloseIndicatorsWindow()
    {
        if (_indicatorsForm is null || _indicatorsForm.IsDisposed)
            return;

        _indicatorsForm.Close();
    }

    /// <summary>
    /// Изменение ЖЕЛУДКА из монитора показателей: убрать порцию или употребить
    /// предмет.
    ///
    /// Оба действия выполняет Симулятор, а не страница: желудок — часть
    /// состояния мира, и правка «на стороне Web» была бы потеряна при первом же
    /// обновлении из домена.
    /// </summary>
    private void IndicatorsForm_StomachActionRequested(
        object? sender,
        StomachActionEventArgs e)
    {
        switch (e.Kind)
        {
            case "remove":
                RemoveStomachPortion(e.ItemId);
                break;

            case "use":
                if (!string.IsNullOrWhiteSpace(e.ItemId))
                    ConsumeStomachItem(e.ItemId!);
                break;
        }
    }

    /// <summary>
    /// Убирает порцию из желудка, НЕ возвращая её в инвентарь.
    ///
    /// Почему так, а не «вернуть как было»: удаление здесь — инструмент
    /// наблюдения и отладки баланса (автор просил убирать объект ПКМ), а возврат
    /// в инвентарь сделал бы из него способ обмена съеденного обратно на предмет.
    /// </summary>
    private void RemoveStomachPortion(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var conditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        // Убираем ПЕРВУЮ порцию с этим Id: порций может быть несколько (съел
        // два апельсина), и «удалить один апельсин» — самое понятное поведение
        // для ПКМ по иконке.
        var target = conditions.Stomach.Portions
            .FirstOrDefault(portion =>
                portion.ItemId.Equals(
                    itemId,
                    StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            // Порции уже нет (усвоилась между кликом и обработкой) — это не
            // ошибка игрока, и ругаться на неё нельзя: просто обновляем монитор.
            RequestSnapshot("stomach portion removed");
            return;
        }

        var remaining = conditions.Stomach.Portions
            .Where(portion => !ReferenceEquals(portion, target))
            .ToArray();

        _hub.Get<PlayerConditionState>("player-conditions").Set(
            conditions with
            {
                // Суммы и ставки пересчитываются из оставшихся порций
                // (Normalize → Materialize). Метаболизм берётся У САМОГО
                // содержимого: без него ставки пересчитались бы по «нормальной»
                // пропускной способности, и оставшаяся еда пошла бы в шкалы не с
                // той скоростью, с какой шла до удаления. Дальше его переписывает
                // каждый шаг пищеварения, поэтому «запасённой» скорости нет.
                Stomach = new StomachContents(0d, 0d, 0d, 0d, 0d)
                {
                    Portions = remaining,
                    MetabolismPercent = conditions.Stomach.MetabolismPercent
                }.Normalize()
            },
            "Удаление порции из пищеварения");

        AppendJournal(
            "StomachPortionRemoved",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            "Из пищеварения удалён предмет: [[item:" + itemId + ":" +
            ItemLabel(itemId) + "]] " + ItemLabel(itemId) + ".",
            _hub.Get<PlayerState>("player").Value.Position);

        RequestSnapshot("stomach portion removed");
    }

    /// <summary>
    /// Открывает окно, на пункт которого сослался монитор показателей.
    ///
    /// Клик по чипу в мониторе обязан открыть ТО ЖЕ окно, что и клик по такому же
    /// чипу в сайдбаре или журнале. Иначе получаются две разные «ссылки» на один
    /// и тот же пункт, и одна из них молча ничего не делает — что автор и увидел
    /// на «Воде» и «Отдохнувшем».
    /// </summary>
    private void IndicatorsForm_NavigationRequested(
        object? sender,
        IndicatorNavigationRequestEventArgs e)
    {
        switch (e.Target)
        {
            case "perks":
                OpenPerksWindow(e.Id, e.Kind);
                break;

            case "items":
                OpenItemsWindow(e.Id);
                break;
        }
    }

    /// <summary>
    /// Открывает окно «Перки, баффы, скиллы» и подсвечивает названный пункт.
    ///
    /// Симуляцию окно НЕ ставит на паузу: оно показывает состояние, и наблюдать
    /// за тикающими таймерами баффов — половина его смысла.
    /// </summary>
    private void OpenPerksWindow(string? highlightPerkId = null, string? perkKind = null)
    {
        var target = string.IsNullOrWhiteSpace(highlightPerkId)
            ? null
            : highlightPerkId.Trim();

        // Вид («бафф»/«дебафф») из ссылки не выбирает раздел — Id в каталоге
        // уникальны, — но расхождение вида с разделом означает ошибку в разметке
        // ссылки, и молчать о ней нельзя: игрок в таком случае попадает в чужой
        // раздел и решает, что окно сломано.
        if (target is not null && !string.IsNullOrWhiteSpace(perkKind))
        {
            var definition = CharacterPerksCatalog.Find(target);

            if (definition is not null)
            {
                var expected = CategoryKey(definition.Category);

                if (!expected.Equals(perkKind.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info(
                        "SimulatorForm: ссылка на перк называет другой раздел.",
                        $"id={target}; kind={perkKind}; expected={expected}");
                }
            }
        }

        if (_perksForm is not null && !_perksForm.IsDisposed)
        {
            _perksForm.WindowState = FormWindowState.Normal;
            _perksForm.BringToFront();
            _perksForm.Activate();

            if (target is not null)
                _perksForm.Highlight(target);

            return;
        }

        _perksForm = new PerksForm();
        _perksForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _perksForm.StateChangeRequested += PerksForm_StateChangeRequested;
        _perksForm.CloseRequested += (_, _) => ClosePerksWindow();
        _perksForm.FormClosed += (_, _) =>
        {
            _perksForm!.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _perksForm.StateChangeRequested -= PerksForm_StateChangeRequested;
            _perksForm = null;
        };

        _perksForm.Show(this);

        if (target is not null)
            _perksForm.Highlight(target);

        // Пакет отправляется сразу: окно, открытое после последнего обновления
        // мира, иначе показывало бы пустой список до следующего события.
        PushPerks();

        AppLogger.Info("SimulatorForm: окно перков открыто.",
            $"size={_perksForm.Width}x{_perksForm.Height}; " +
            $"highlight={target ?? "-"}; kind={perkKind ?? "-"}");
    }

    /// <summary>Закрывает окно перков, если оно открыто.</summary>
    private void ClosePerksWindow()
    {
        if (_perksForm is null || _perksForm.IsDisposed)
            return;

        _perksForm.Close();
    }

    /// <summary>
    /// Отправляет в окно перков перечень пунктов и признак «действует сейчас».
    ///
    /// Имена собираются доменом (<see cref="CharacterPerksCatalog.DisplayName"/>):
    /// пометки «(активен)» и «(В разработке)» — это правило показа, и проверяться
    /// оно должно тестом, а не глазами в вёрстке.
    /// </summary>
    private void PushPerks()
    {
        if (_perksForm is null || _perksForm.IsDisposed)
            return;

        var conditions = _hub.Get<PlayerConditionState>("player-conditions").Value;
        var character = _hub.Get<CharacterState>("character").Value;

        var entries = CharacterPerksCatalog.Entries.Select(entry =>        {
            var level = SkillLevel(character, entry.Id);
            var active = entry.Category switch
            {
                CharacterPerkCategory.Perk => IsPerkActive(character, entry.Id),
                CharacterPerkCategory.Skill => level > 0,
                _ => HasActiveEffect(conditions, entry.Id)
            };

            return new
            {
                id = entry.Id,
                category = CategoryKey(entry.Category),
                name = entry.Name,
                displayName = CharacterPerksCatalog.DisplayName(entry, active),
                description = entry.Description,
                affects = entry.Affects,
                implemented = entry.Implemented,
                durationLabel = entry.DurationLabel,
                maxLevel = entry.MaxLevel,
                level,
                active
            };
        }).ToArray();

        _perksForm.SetPayloadJson(JsonSerializer.Serialize(new
        {
            type = "perks",
            entries
        }, SnapshotJsonOptions));
    }

    /// <summary>
    /// Ключ раздела для страницы. Отдельно от C#-enum, потому что строка уходит
    /// в разметку, и «Skill» с большой буквы там пришлось бы нормализовать в
    /// JavaScript — то есть второй раз описывать тот же перечень.
    /// </summary>
    private static string CategoryKey(CharacterPerkCategory category) => category switch
    {
        CharacterPerkCategory.Perk => "perk",
        CharacterPerkCategory.Skill => "skill",
        CharacterPerkCategory.Buff => "buff",
        _ => "debuff"
    };
    /// <summary>Уровень умения из канала персонажа; 0 — умения нет.</summary>
    private static int SkillLevel(CharacterState character, string skillId) =>
        character.Skills
            .Where(skill => skill.Id.Equals(skillId, StringComparison.OrdinalIgnoreCase))
            .Select(skill => Math.Max(0, skill.Level))
            .FirstOrDefault();

    private static bool IsPerkActive(CharacterState character, string perkId) =>
        character.Buffs.Any(id => id.Equals(perkId, StringComparison.OrdinalIgnoreCase)) ||
        character.Debuffs.Any(id => id.Equals(perkId, StringComparison.OrdinalIgnoreCase));

    private static bool HasActiveEffect(PlayerConditionState conditions, string effectId) =>
        conditions.Effects.Any(effect =>
            effect.Id.Equals(effectId, StringComparison.OrdinalIgnoreCase) &&
            effect.RemainingRealSeconds > 0d);

    /// <summary>
    /// Открывает окно «Предметы» — каталог всего, что существует в симуляторе.
    ///
    /// Симуляцию окно НЕ ставит на паузу: это справочник, а не действие.
    /// </summary>
    private void OpenItemsWindow(string? highlightItemId = null)
    {
        var target = string.IsNullOrWhiteSpace(highlightItemId)
            ? null
            : highlightItemId.Trim();

        if (_itemsForm is not null && !_itemsForm.IsDisposed)
        {
            _itemsForm.WindowState = FormWindowState.Normal;
            _itemsForm.BringToFront();
            _itemsForm.Activate();

            if (target is not null)
                _itemsForm.Highlight(target);

            return;
        }

        _itemsForm = new ItemsForm();
        _itemsForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        _itemsForm.StateChangeRequested += ItemsForm_StateChangeRequested;
        _itemsForm.CloseRequested += (_, _) => CloseItemsWindow();
        _itemsForm.FormClosed += (_, _) =>
        {
            _itemsForm!.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _itemsForm.StateChangeRequested -= ItemsForm_StateChangeRequested;
            _itemsForm = null;
        };

        _itemsForm.Show(this);

        if (target is not null)
            _itemsForm.Highlight(target);

        PushItems();

        AppLogger.Info("SimulatorForm: окно предметов открыто.",
            $"size={_itemsForm.Width}x{_itemsForm.Height}; highlight={target ?? "-"}");
    }

    /// <summary>Закрывает окно предметов, если оно открыто.</summary>
    private void CloseItemsWindow()
    {
        if (_itemsForm is null || _itemsForm.IsDisposed)
            return;

        _itemsForm.Close();
    }

    /// <summary>
    /// Открывает окно «Профили ETS2».
    ///
    /// Симуляцию окно НЕ ставит на паузу: это справочник о профилях ИГРЫ, а не
    /// действие над миром AQE. Чтение профилей идёт своим ходом, и мир продолжает
    /// тикать.
    ///
    /// Окно создаётся один раз: повторное нажатие кнопки поднимает уже открытое,
    /// иначе на каждый клик читался бы весь каталог профилей заново.
    /// </summary>
    private void OpenEts2ProfilesWindow(string? hexFolder = null)
    {
        if (_ets2ProfilesForm is not null && !_ets2ProfilesForm.IsDisposed)
        {
            _ets2ProfilesForm.WindowState = FormWindowState.Normal;
            _ets2ProfilesForm.BringToFront();
            _ets2ProfilesForm.Activate();
            if (!string.IsNullOrWhiteSpace(hexFolder))
                _ets2ProfilesForm.OpenProfileByHexFolder(hexFolder);
            return;
        }

        _ets2ProfilesForm = new Ets2ProfilesForm();
        _ets2ProfilesForm.GlobalHotKeyPressed += SimulatorForm_GlobalHotKeyPressed;
        // Окно профилей НЕ подписывается на StateChangeRequested: оно ничего не
        // меняет в симуляции, и общего с ней состояния у него нет — кроме имени
        // профиля, которое идёт в шапку отдельным событием.
        _ets2ProfilesForm.CloseRequested += (_, _) => CloseEts2ProfilesWindow();
        _ets2ProfilesForm.ProfileSelected += Ets2ProfilesForm_ProfileSelected;
        _ets2ProfilesForm.FormClosed += (_, _) =>
        {
            _ets2ProfilesForm!.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _ets2ProfilesForm.ProfileSelected -= Ets2ProfilesForm_ProfileSelected;
            _ets2ProfilesForm = null;

            // Подпись под авторством снимается ВМЕСТЕ с окном: она сообщает, что
            // профиль выбран в открытом окне, а закрытое окно ничего не выбирает.
            SetEts2ProfileName(string.Empty);
        };

        _ets2ProfilesForm.Show(this);
        if (!string.IsNullOrWhiteSpace(hexFolder))
            _ets2ProfilesForm.OpenProfileByHexFolder(hexFolder);

        AppLogger.Info("SimulatorForm: окно профилей ETS2 открыто.",
            $"size={_ets2ProfilesForm.Width}x{_ets2ProfilesForm.Height}; " +
            $"gameRoot={_ets2ProfilesForm.GameRoot ?? "<нет>"}");
    }

    /// <summary>Закрывает окно профилей ETS2, если оно открыто.</summary>
    private void CloseEts2ProfilesWindow()
    {
        if (_ets2ProfilesForm is null || _ets2ProfilesForm.IsDisposed)
            return;

        _ets2ProfilesForm.Close();
    }

    /// <summary>
    /// Запоминает имя профиля, выбранного в окне профилей ETS2, и передаёт его
    /// в шапку Симулятора под подписью авторства.
    /// </summary>
    private void Ets2ProfilesForm_ProfileSelected(object? sender, string profileName)
        => SetEts2ProfileName(profileName);

    private void Ets2SyncMonitor_Notification(Ets2SyncNotification notification)
    {
        switch (notification.Kind)
        {
            case Ets2SyncNotificationKind.InitialSetupRequired:
                MessageBox.Show(
                    this,
                    notification.Message,
                    "Начало игры в AQE",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;

            case Ets2SyncNotificationKind.ProfilesMissing:
                MessageBox.Show(
                    this,
                    notification.Message,
                    "Профиль ETS2 недоступен",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;

            case Ets2SyncNotificationKind.ProfilesReturned:
                MessageBox.Show(
                    this,
                    notification.Message,
                    "Профиль ETS2 снова доступен",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                break;

            case Ets2SyncNotificationKind.OtherWorldCareerBlocked:
                MessageBox.Show(
                    this,
                    notification.Message,
                    "Карьера уже занята другим миром",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;

            case Ets2SyncNotificationKind.CareerBound:
                MessageBox.Show(
                    this,
                    notification.Message,
                    "Синхронизация с ETS2",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                RequestSnapshot("ETS2 career bound");
                break;

            case Ets2SyncNotificationKind.CareerRejected:
                AppLogger.Info("SimulatorForm: новая карьера отклонена.", notification.Message);
                break;

            case Ets2SyncNotificationKind.CheckpointCreated:
            case Ets2SyncNotificationKind.CheckpointLoaded:
                AppLogger.Info("SimulatorForm: ETS2 sync.", notification.Message);
                break;

            case Ets2SyncNotificationKind.SaveSynchronized:
            case Ets2SyncNotificationKind.AutosaveSynchronized:
                AppendJournal(
                    "Ets2SaveSynchronized",
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    notification.Message,
                    coordinate: null,
                    compact: true,
                    textColor: "lime",
                    fontWeight: 500);
                RequestSnapshot("ETS2 save synchronized");
                break;

            case Ets2SyncNotificationKind.CheckpointNotFound:
                AppLogger.Warn("SimulatorForm: ETS2 sync.", notification.Message);
                break;

            case Ets2SyncNotificationKind.GameStarted:
            case Ets2SyncNotificationKind.GameStopped:
                AppLogger.Info("SimulatorForm: ETS2 sync.", notification.Message);
                break;
        }
    }

    private void Ets2SyncMonitor_CheckpointReady(Ets2SyncCheckpointReady ready)
    {
        try
        {
            _dynamicEventDispatcher.SetSimulationRunning(false);
            SimulationSaveMapper.Apply(_hub, ready.Save.State);
            SetRouteStateAfterLoad(ready.Save.State.Route, ready.Save.State.RouteRuntime);
            _mapView = ready.Save.State.MapView?.Normalize();
            _mapViewRestoreToken++;
            _inventoryPausedSimulation = false;
            _runtime.PauseSimulation();
            SyncRuntimeQuestEnabled();

            AppLogger.Info(
                "SimulatorForm: применён чекпоинт ETS2 sync.",
                $"checkpoint={ready.Entry.Id}; slot={ready.Entry.Ets2Save.Slot}; save={ready.Entry.Ets2Save.Name}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SimulatorForm: не удалось применить чекпоинт ETS2 sync.", ex);
        }

        RequestSnapshot("ETS2 checkpoint loaded");
    }

    /// <summary>
    /// Показывает имя профиля ETS2 под авторством (требование автора).
    ///
    /// Пустое имя СКРЫВАЕТ строку, а не печатает «—»: профиль по умолчанию не
    /// выбран, и подпись «Профиль ETS2: —» выглядела бы незаполненным полем.
    /// </summary>
    private void SetEts2ProfileName(string? profileName)
    {
        var name = (profileName ?? string.Empty).Trim();

        // Событие приходит на каждое переключение селекта, включая повторный выбор
        // того же профиля; без этой проверки страница перерисовывалась бы зря.
        if (name.Equals(_ets2ProfileName, StringComparison.Ordinal))
            return;

        _ets2ProfileName = name;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "ets2_profile_selection",
            name = _ets2ProfileName
        }, SnapshotJsonOptions));
    }

    /// <summary>
    /// Отправляет в окно предметов каталог с пищевой ценностью и количеством.
    ///
    /// Пищевую ценность считает домен (<see cref="CharacterConsumableCatalog"/>):
    /// вторая таблица «сколько ккал в пайке» в этом файле разошлась бы с движком,
    /// и окно показывало бы одно, а начислялось другое.
    ///
    /// Изображений 48×48 нет: автор прямо просил НЕ генерировать новые ресурсы, а
    /// показывать цвет с буквой. Поэтому страница получает цвет и первую букву
    /// названия, и подменять их суррогатной иконкой нельзя — её приняли бы за
    /// настоящую картинку предмета.
    /// </summary>
    private void PushItems()
    {
        if (_itemsForm is null || _itemsForm.IsDisposed)
            return;

        var inventory = _hub.Get<InventoryState>("inventory").Value;

        var entries = ItemCatalogFactory.CreateStarter().Select(item =>
        {
            var profile = CharacterConsumableCatalog.GetProfile(item.Id);

            inventory.Items.TryGetValue(item.Id, out var count);

            var edible = CharacterVitalsEngine.CanConsume(item.Id);
            var nutrients = ItemData.Nutrition(item.Id);
            var (quality, condition) = ItemData.Attributes(item.Id, edible);

            return new
            {
                id = item.Id,
                name = item.Name,
                description = item.Description,
                category = item.Category,
                color = item.Color,
                letter = Letter(item.Name),
                // Ноль, если предмета нет: пустое поле читалось бы как «неизвестно»,
                // а неизвестного здесь нет — канал инвентаря знает точное число.
                quantity = Math.Max(0, count),
                // Кормит ли предмет: у неедовых пунктов пищевого блока нет вовсе,
                // и рисовать «0 ккал» значило бы обещать еду там, где её нет.
                feeds = profile.Feeds,
                // Объём порции В МИЛЛИЛИТРАХ — единая величина для окна предметов,
                // меню желудка и списка содержимого. Прежде окно показывало
                // `grams`, и у банана рядом с пищевой ценностью стояло «150 г», а в
                // меню желудка — «110 мл» (вода в банане): два разных числа на один
                // предмет. Теперь всюду одно и то же — объём порции.
                milliliters = CharacterConsumableCatalog.GetPortionMilliliters(item.Id),
                // А масса — отдельное, ФИЗИЧЕСКОЕ число, и путать её с объёмом
                // нельзя: у литра воды это 1000 г и 1000 мл, а у 30 г мёда — 30 г
                // против 5 мл воды в нём. Прежде окно рисовало только объём, и
                // «сколько весит» в рюкзаке нигде не было видно.
                grams = profile.Grams,
                // БЖУ порции, граммы. Пустое у несъедобного: у таблетки нет
                // состава, и рисовать ей «0 г белка» значило бы утверждать
                // диетологический факт о предмете, который не едят.
                proteinGrams = nutrients.HasAny ? nutrients.ProteinGrams : 0d,
                fatGrams = nutrients.HasAny ? nutrients.FatGrams : 0d,
                carbohydrateGrams = nutrients.HasAny ? nutrients.CarbohydrateGrams : 0d,
                // Качество и состояние — задел для механик крафта и порчи. Сейчас
                // у всех предметов значения по умолчанию, но они уже приходят в
                // окно подписями: пустое место показало бы «неизвестно», а
                // неизвестного нет.
                quality = ItemAttributeLabels.Quality(quality),
                condition = ItemAttributeLabels.Condition(condition),
                // Цена — из справочника предметов, с отсечкой квестового внутри
                // PriceRubles. Ноль означает «не продаётся»: у квестовых он
                // по правилу автора, а «0 ₽» рисовать не нужно — бесплатного
                // товара в мире нет. Покупка появится в механиках квестов; пока
                // цена только ПОКАЗЫВАЕТСЯ, как и просил автор.
                priceRubles = ItemCatalogFactory.PriceRubles(item),
                // Квестовость отдаётся ОТДЕЛЬНЫМ флагом, а не выводится в окне из
                // категории: правило «не продаётся» живёт в домене, и окно должно
                // получать его решение, а не повторять строку категории у себя.
                quest = ItemCatalogFactory.IsQuestOnly(item.Category),
                // Съедобность считает ДОМЕН: у таблеток калорий нет, но они
                // занимают желудок и действуют, поэтому «съедобно» — это не
                // «есть калории», а «CanConsume». Галочки «только съедобное» /
                // «только несъедобное» и салатовая плашка обязаны означать ровно
                // то же, что меню желудка.
                edible,
                kilocalories = profile.Kilocalories,
                waterMilliliters = profile.WaterMilliliters,
                energyPercent = profile.EnergyPercent,
                hydrationPercent = profile.HydrationPercent
            };
        }).ToArray();

        _itemsForm.SetPayloadJson(JsonSerializer.Serialize(new
        {
            type = "items",
            entries
        }, SnapshotJsonOptions));
    }

    /// <summary>
    /// Первая буква названия для плитки предмета.
    ///
    /// Суррогатной иконки у нас нет, поэтому плитка — это цвет с буквой. Берём
    /// первую букву, а не инициалы: 48×48 вмещает одну букву, и две читались бы
    /// хуже, чем помогают.
    /// </summary>
    private static string Letter(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Trim()[..1].ToUpperInvariant();

    /// <summary>
    /// Название предмета для журнала: «Вода», а не «water.bottle».
    ///
    /// Идентификатор в записи журнала — это внутреннее имя, и игрок, прочитав
    /// «Использован предмет: food.meal», не узнаёт, что съел паёк. Название берём
    /// из того же каталога, что и окно предметов.
    ///
    /// Незнакомый Id печатается как есть: предмет мог прийти из кампании, которой
    /// каталог ещё не знает, и прятать его за «неизвестный предмет» значило бы
    /// терять единственную зацепку.
    /// </summary>
    private static string ItemLabel(string itemId) =>
        ItemCatalogFactory.CreateStarter()
            .FirstOrDefault(item =>
                item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase))
            ?.Name
        ?? itemId;

    /// <summary>
    /// Правка перка, эффекта или очков умения из окна перков.
    ///
    /// Проверки живут ЗДЕСЬ, а не в домене: «клиент может прислать что угодно» —
    /// это свойство границы между страницей и миром, и отпор обязан стоять ровно
    /// на ней. Домен получает уже проверенные значения.
    /// </summary>
    private void PerksForm_StateChangeRequested(object? sender, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            switch (String(root, "action", string.Empty))
            {
                case "set_perk":
                    SetPerk(root);
                    break;

                case "set_effect":
                    SetEffect(root);
                    break;

                case "set_skill_level":
                    SetSkillLevel(root);
                    break;
            }
        }
        catch (Exception ex)
        {
            PostSaveError("Не удалось изменить перк: " + ex.Message);
        }
    }

    /// <summary>
    /// Включает или выключает перк.
    ///
    /// Пишем в ОБА списка канала персонажа: баффы и дебаффы это разные списки, и
    /// снятие обязано убрать Id из того, в котором он лежит. Иначе «Убрать»
    /// оставлял бы перк в другом списке, и окно снова показывало бы его активным.
    /// </summary>
    private void SetPerk(JsonElement root)
    {
        var perkId = Required(root, "perkId");
        var enabled = Flag(root, "enabled");

        var definition = CharacterPerksCatalog.Find(perkId);
        if (definition is null || definition.Category != CharacterPerkCategory.Perk)
        {
            PostSaveError("Такого перка нет в каталоге.");
            return;
        }

        if (!definition.Implemented)
        {
            PostSaveError(
                "Перк «" + definition.Name + "» " +
                CharacterPerksCatalog.InProgressSuffix.Trim('(', ')').ToLowerInvariant() +
                " и пока не подключён к симулятору.");
            return;
        }

        var channel = _hub.Get<CharacterState>("character");
        var character = channel.Value;

        var buffs = new List<string>(character.Buffs);
        var debuffs = new List<string>(character.Debuffs);
        buffs.RemoveAll(id => id.Equals(perkId, StringComparison.OrdinalIgnoreCase));
        debuffs.RemoveAll(id => id.Equals(perkId, StringComparison.OrdinalIgnoreCase));

        if (enabled)
            buffs.Add(perkId);

        channel.Set(
            character with { Buffs = buffs, Debuffs = debuffs },
            enabled ? "Получен перк" : "Снят перк");

        AppendJournal(
            enabled ? "PerkGained" : "PerkRemoved",
            DateTimeOffset.UtcNow,
            "Персонаж",
            (enabled ? "Получен перк " : "Снят перк ") +
            "[[perk:" + perkId + ":" + (enabled ? "buff" : "debuff") + "]] " +
            definition.Name + ".");
    }

    /// <summary>
    /// Ставит или снимает бафф/дебафф.
    ///
    /// Срок берётся из каталога, а не из сообщения: длительность — правило мира,
    /// и присланное страницей значение позволило бы выдать себе вечный бафф.
    /// Нулевая длительность означает, что эффект держится по УСЛОВИЮ (например,
    /// «Неопрятный» — пока гигиена низкая): такой вручную не выдаётся, потому что
    /// движок снимет его на ближайшем же пересчёте.
    /// </summary>
    private void SetEffect(JsonElement root)
    {
        var effectId = Required(root, "effectId");
        var enabled = Flag(root, "enabled");

        var definition = CharacterPerksCatalog.Find(effectId);
        if (definition is null ||
            (definition.Category != CharacterPerkCategory.Buff &&
             definition.Category != CharacterPerkCategory.Debuff))
        {
            PostSaveError("Такого баффа или дебаффа нет в каталоге.");
            return;
        }

        if (!enabled)
        {
            RemoveEffect(effectId, definition);
            return;
        }

        if (definition.DurationRealSeconds <= 0d && definition.DurationGameSeconds <= 0d)
        {
            PostSaveError(
                "«" + definition.Name + "» держится по условию и не выдаётся вручную: " +
                "движок снимет его на ближайшем пересчёте.");
            return;
        }

        var seconds = definition.DurationRealSeconds > 0d
            ? definition.DurationRealSeconds
            : definition.DurationGameSeconds / Math.Max(0.0001d, _runtime.SimulationSpeed);

        SetEffectInCondition(
            definition,
            seconds,
            definition.Category == CharacterPerkCategory.Debuff,
            enabled: true);

        AppendJournal(
            "EffectActivated",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            "Активирован [[perk:" + definition.Id + ":" +
            (definition.Category == CharacterPerkCategory.Debuff ? "debuff" : "buff") +
            "]] " + definition.Name + ".");
    }

    /// <summary>
    /// Записывает эффект в канал условий.
    ///
    /// Заменяем одноимённый эффект, а не добавляем второй: два «Выгорания» в
    /// списке означали бы два независимых таймера, и монитор показывал бы их как
    /// два разных пункта с одним названием.
    ///
    /// «Отдохнувший» и «Расслабление» взаимоисключающие — это правило движка
    /// (<c>CharacterVitalsEngine</c>), и повторять его здесь приходится: иначе
    /// правка из окна оставляла бы в списке оба, и движок снял бы один из них на
    /// следующем же тике, то есть окно показало бы неправду на секунду.
    /// </summary>
    private void SetEffectInCondition(
        CharacterPerkDefinition definition,
        double remainingRealSeconds,
        bool isDebuff,
        bool enabled)
    {
        var channel = _hub.Get<PlayerConditionState>("player-conditions");
        var conditions = channel.Value;

        var kept = conditions.Effects
            .Where(effect =>
                !effect.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase) &&
                !MutuallyExclusive(definition.Id, effect.Id))
            .ToList();

        if (enabled)
        {
            kept.Add(new ActivePlayerEffectState(
                definition.Id,
                definition.Name,
                remainingRealSeconds,
                isDebuff,
                ExperienceMultiplier: definition.Id.Equals("rested", StringComparison.OrdinalIgnoreCase)
                    ? CharacterVitalsEngine.RestedExperienceMultiplier
                    : definition.Id.Equals("relaxation", StringComparison.OrdinalIgnoreCase)
                        ? CharacterVitalsEngine.RelaxationExperienceMultiplier
                        : 1d,
                StressAccumulationSlowdownPercent:
                    definition.Id.Equals("rested", StringComparison.OrdinalIgnoreCase)
                        ? CharacterVitalsEngine.RestedStressSlowdownPercent
                        : definition.Id.Equals("relaxation", StringComparison.OrdinalIgnoreCase)
                            ? CharacterVitalsEngine.RelaxationStressSlowdownPercent
                            : 0d));
        }

        channel.Set(
            conditions with { Effects = kept.ToArray() },
            enabled ? "Активирован эффект" : "Деактивирован эффект");
    }

    /// <summary>«Отдохнувший» и «Расслабление» заменяют друг друга.</summary>
    private static bool MutuallyExclusive(string first, string second) =>
        (first.Equals("rested", StringComparison.OrdinalIgnoreCase) &&
         second.Equals("relaxation", StringComparison.OrdinalIgnoreCase)) ||
        (first.Equals("relaxation", StringComparison.OrdinalIgnoreCase) &&
         second.Equals("rested", StringComparison.OrdinalIgnoreCase));

    /// <summary>Снимает эффект и сообщает об этом в журнал.</summary>
    private void RemoveEffect(string effectId, CharacterPerkDefinition definition)
    {
        SetEffectInCondition(
            definition,
            remainingRealSeconds: 0d,
            isDebuff: definition.Category == CharacterPerkCategory.Debuff,
            enabled: false);

        AppendJournal(
            "EffectDeactivated",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            "Деактивирован [[perk:" + definition.Id + ":" +
            (definition.Category == CharacterPerkCategory.Debuff ? "debuff" : "buff") +
            "]] " + definition.Name + ".");
    }

    /// <summary>
    /// Ставит уровень умения.
    ///
    /// Ноль означает «умения нет» — так сказано в требовании автора, и окно такой
    /// пункт не показывает. Хранить «нет умения» отдельным флагом не нужно:
    /// уровень уже это выражает, и второй признак мог бы противоречить первому.
    /// </summary>
    private void SetSkillLevel(JsonElement root)
    {
        var skillId = Required(root, "skillId");
        var level = (int)Math.Round(Number(root, "level", 0d));

        var definition = CharacterPerksCatalog.Find(skillId);
        if (definition is null || !definition.IsLevelled)
        {
            PostSaveError("Такого умения нет в каталоге.");
            return;
        }

        if (level < 0 || level > definition.MaxLevel)
        {
            PostSaveError(
                "Умение «" + definition.Name + "» принимает от 0 до " +
                definition.MaxLevel + " очков.");
            return;
        }

        var channel = _hub.Get<CharacterState>("character");
        var character = channel.Value;

        var skills = new List<CharacterSkillState>(character.Skills);
        var index = skills.FindIndex(skill =>
            skill.Id.Equals(skillId, StringComparison.OrdinalIgnoreCase));

        var updated = new CharacterSkillState(
            definition.Id,
            definition.Name,
            definition.Description,
            level > 0,
            level > 0 ? level + " / " + definition.MaxLevel : "нет")
        {
            Kind = SkillKind.Levelled,
            Level = level,
            MaxLevel = definition.MaxLevel
        };

        if (index >= 0)
            skills[index] = updated;
        else
            skills.Add(updated);

        channel.Set(
            character with { Skills = skills },
            "Уровень умения");

        AppendJournal(
            "SkillLevel",
            DateTimeOffset.UtcNow,
            "Персонаж",
            "Умение [[perk:" + definition.Id + ":buff]] " + definition.Name +
            (level > 0 ? ": очков " + level + " из " + definition.MaxLevel + "." : ": снято."));
    }

    /// <summary>
    /// Правка количества предмета из окна «Предметы».
    ///
    /// Количество ЗАДАЁТСЯ, а не прибавляется: поле показывает текущее число, и
    /// микрокнопки рядом меняют его на единицу. «Прибавить» вместо «задать»
    /// значило бы, что повторное чтение того же поля удваивает запас.
    /// </summary>
    private void ItemsForm_StateChangeRequested(object? sender, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!String(root, "action", string.Empty).Equals(
                "set_item_quantity",
                StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var itemId = Required(root, "itemId").Trim();
            var quantity = Math.Clamp(
                (int)Math.Round(Number(root, "quantity", 0d)),
                0,
                999);

            var definition = ItemCatalogFactory.CreateStarter()
                .FirstOrDefault(item =>
                    item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));

            if (definition is null)
            {
                PostSaveError("Предмет отсутствует в каталоге.");
                return;
            }

            var channel = _hub.Get<InventoryState>("inventory");
            var state = channel.Value;
            state.Items.TryGetValue(itemId, out var previousQuantity);
            var items = new Dictionary<string, int>(
                state.Items,
                StringComparer.OrdinalIgnoreCase)
            {
                [itemId] = quantity
            };

            var newIds = new HashSet<string>(
                state.NewItemIds,
                StringComparer.OrdinalIgnoreCase);

            // Нулевое количество — предмета нет, и «новым» он быть не может:
            // иначе инвентарь показывал бы непрочитанную метку у пустой строки.
            if (quantity <= 0)
                newIds.Remove(itemId);

            channel.Set(
                new InventoryState(items, newIds.ToArray()),
                "Правка количества предмета");

            // Окно инвентаря получает ту же правку: два окна с одним состоянием
            // обязаны показывать одно число. Передаётся РАЗНИЦА, а не новое
            // количество: уведомление показывает «выдано/изъято», и абсолютное
            // число читалось бы как «выдано 12 штук» после правки до двенадцати.
            if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
                _inventoryForm.NotifyInventoryChange(itemId, quantity - previousQuantity);

            PushItems();
        }
        catch (Exception ex)
        {
            PostSaveError("Не удалось изменить количество: " + ex.Message);
        }
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

    /// <summary>
    /// Игрок употребил предмет из окна инвентаря (кнопка «Использовать»).
    ///
    /// Идёт ЧЕРЕЗ ТОТ ЖЕ путь, что и употребление из карты
    /// (<see cref="UseInventoryItemCore"/>): механика, автосохранение и снимок
    /// не должны отличаться в зависимости от того, какое окно нажали.
    /// </summary>
    private void InventoryForm_ItemUseRequested(object? sender, InventoryItemUseEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.ItemId))
            return;

        UseInventoryItemCore(e.ItemId);
    }

    private void InventoryForm_ItemDropRequested(object? sender, InventoryItemDropEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.ItemId))
            return;

        DropInventoryItemCore(e.ItemId);
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
                case "use_inventory_item":
                    UseInventoryItem(root);
                    break;

                case "drop_inventory_item":
                    DropInventoryItem(root);
                    break;

                case "grant_inventory_item":
                    GrantInventoryItem(root);
                    break;

                case "toggle_inventory":
                    if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
                        CloseInventoryWindow();
                    else
                        OpenInventoryWindow();
                    break;

                case "find_full_lodging":
                    FindFullLodging();
                    break;

                case "open_indicators":
                    OpenIndicatorsWindow();
                    break;

                // Ссылки из монитора, журнала и окна перков: клик по имени
                // эффекта, показателя или предмета открывает своё окно и
                // подсвечивает названный пункт.
                case "open_perks":
                    OpenPerksWindow(
                        StringOrNull(root, "perkId"),
                        StringOrNull(root, "perkKind"));
                    break;

                case "open_items":
                    OpenItemsWindow(StringOrNull(root, "itemId"));
                    break;

                // Кнопка «Профили ETS2» рядом с «Обновить квесты». Действие без
                // параметров: профиль выбирается уже внутри окна.
                case "open_ets2_profiles":
                    OpenEts2ProfilesWindow(StringOrNull(root, "hexFolder"));
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

                case "set_telemetry_follow":
                    SetTelemetryFollow(root);
                    break;

                case "bind_active_ets2_career":
                    if (!_ets2SyncMonitor.BindActiveCareer())
                        RequestSnapshot("ETS2 active career binding failed");
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

                // Галочка «%/время» в шапке монитора показателей: страница сообщает
                // о переключении, Хост запоминает его в настройках и ОТВЕЧАЕТ
                // свежим снимком. Ответ обязателен: без него страница осталась бы
                // с собственной догадкой о состоянии, а она после перезапуска
                // разошлась бы с файлом настроек.
                case "set_percent_units":
                    SetPercentUnits(root);
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
                    ResetWorldState("сброс симулятора", stopSimulation: false);
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
            Text = $"Вы загружаете маршрут \"{fileName}\""
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
                    trimToPlayer: false,
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

        var playerPosition = _hub.Get<PlayerState>("player").Value.Position;
        var position = new WorldCoordinate(
            Number(root, "x", playerPosition.X),
            Number(root, "y", playerPosition.Y),
            Number(root, "z", playerPosition.Z));

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
        if (enabled && _telemetryFollowEnabled)
        {
            throw new InvalidOperationException(
                "Редактирование маршрута недоступно при включённой телеметрии ETS2.");
        }

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

        if (enabled && (_ets2SyncRequired && !_ets2SyncMonitor.ViewState.CareerConnected))
            throw new InvalidOperationException("Движение по маршруту доступно только при подключённой карьере ETS2.");

        if (enabled && _telemetryFollowEnabled)
            throw new InvalidOperationException("Движение по маршруту недоступно при включённой телеметрии ETS2.");

        if (!enabled)
        {
            _routeEnabled = false;
            SetPlayerMovementIdle();
            AppLogger.Info("SimulatorForm: движение по маршруту выключено.");
            PersistSession("автосохранение: движение по маршруту выключено", force: true);
            RequestSnapshot("route movement disabled");
            return;
        }

        if (_routeState.Waypoints.Count == 0)
            throw new InvalidOperationException("Маршрут пуст — движение по нему невозможно.");

        var resumed = _routeStoppedWaypointId is not null;
        var hasStoredTarget = _routeTargetWaypointId is not null;

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
            trimToPlayer: !resumed && !hasStoredTarget,
            renumberTrimmed: !resumed && !hasStoredTarget,
            anchorWaypointId: resumed ? null : _routeTargetWaypointId);

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

        if (resumed && _routeCursor.Initialized)
        {
            _routeCursor = _routeCursor with
            {
                ResumeAfterStop = true
            };
        }

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
        // никогда не записывается в полилинию: динамический сегмент строится отдельно
        // от текущего положения игрока до следующей фиксированной точки.
        _routePlan = _routePlanner.Build(_routeState, playerPosition);

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

                _routePlan = _routePlanner.Build(_routeState, playerPosition);

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
        if (_telemetryFollowEnabled)
        {
            _routeMovementLastTick = null;
            return;
        }

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

        // Движение по маршруту идёт в ИГРОВОМ времени: маршрут — часть мира,
        // поэтому кратность ускорения времени обязана ускорять и его. Раньше
        // сюда попадали РЕАЛЬНЫЕ секунды, и при ×2/×4/×8 игрок полз с той же
        // скоростью, хотя часы «убегали» вперёд и расчётное время прибытия
        // (оно считается по игровому времени) расходилось с движением.
        // Спидометр при этом остаётся прежним: масштабируется только пройденный
        // путь, а не отображаемая скорость (SpeedKmh задаётся путевой точкой).
        var gameElapsed = elapsed * Math.Max(0d, _runtime.SimulationSpeed);

        if (gameElapsed <= 0d)
            return;

        var current = _hub.Get<PlayerState>("player").Value;
        var previousTargetId = _routeTargetWaypointId;

        var result = RouteMovementEngine.Advance(
            _routeState,
            _routePlan,
            _routeCursor,
            current.Position,
            gameElapsed);

        _routeTravelRealSeconds += elapsed;
        _routeTravelGameSeconds += gameElapsed;

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

    /// <summary>
    /// Реакция маршрута на смену состояния симуляции (пауза/стоп/продолжение).
    ///
    /// Мир при выключении симуляции НЕ сбрасывается: останавливается только
    /// время. Движение по маршруту — часть мира, а не симуляции, поэтому
    /// скорость и позиция игрока, накопленное время пути и включённость маршрута
    /// сохраняются. Прежде здесь вызывался <see cref="SetPlayerMovementIdle"/>,
    /// и пауза обнуляла спидометр, а также накопленное игровое/реальное время
    /// пути — то есть выключение симуляции меняло состояние прохождения.
    ///
    /// Сбрасывается только ЯКОРЬ времени последнего тика: иначе при продолжении
    /// разница с ним включила бы в себя всю паузу и игрок «прыгнул» бы вперёд.
    /// </summary>
    private void SetRouteAfterSimulationStateChange()
    {
        _routeMovementLastTick = null;
    }

    private void SetRouteStateAfterLoad(RouteState route, RouteRuntimeState? runtime = null)
    {
        // RoutePlanner.Build привязывает первую фиксированную точку к дороге
        // ОТ ТЕКУЩЕГО ПОЛОЖЕНИЯ ИГРОКА, поэтому позиция нужна и при загрузке
        // (иначе привязка шла бы от начала координат).
        var playerPosition = _hub.Get<PlayerState>("player").Value.Position;

        _routeState = (route ?? RouteState.Empty).Normalize();
        _selectedRouteWaypointId = null;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _routeTargetWaypointIndex = null;
        _routeTargetWaypointId = null;
        _routeCursor = RouteCursor.Initial;
        _routeLastHeading = 0d;
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

        _routePlan = _routePlanner.Build(_routeState, playerPosition);

        // Идентификатор — главный источник истины. Индекс нужен только как
        // совместимость со старыми сохранениями, где ID ещё не записывался.
        var targetId = runtime?.CurrentTargetWaypointId;
        var targetIndexFromSave = runtime?.CurrentTargetWaypointIndex;
        if (string.IsNullOrWhiteSpace(targetId) &&
            targetIndexFromSave is int savedTargetIndex &&
            savedTargetIndex >= 0 &&
            savedTargetIndex < _routeState.Waypoints.Count)
        {
            targetId = _routeState.Waypoints[savedTargetIndex].Id;
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
                if (stopIndex is int stoppedPoint)
                {
                    var candidates = _routePlan.Points
                        .Select((point, index) => (point, index))
                        .Where(item => item.point.DestinationWaypointIndex == stoppedPoint)
                        .ToArray();

                    nextPointIndex = NearestCandidateRoutePoint(
                        candidates,
                        _hub.Get<PlayerState>("player").Value.Position);
                }
            }
            else if (!string.IsNullOrWhiteSpace(targetId))
            {
                var resolvedTargetIndex = IndexOfWaypoint(targetId);
                if (resolvedTargetIndex is int resolvedTarget)
                {
                    var candidates = _routePlan.Points
                        .Select((point, index) => (point, index))
                        .Where(item => item.point.DestinationWaypointIndex == resolvedTarget)
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
        var now = DateTimeOffset.UtcNow;
        var clock = _hub.Get<WorldClockState>("sim-time").Value;

        // Время идёт только при РАБОТАЮЩЕЙ симуляции. Пауза и полная остановка
        // замораживают игровое время, а значит и прирост шкал: единицы шкал
        // начисляются за игровое время, а оно стоит. Мир при этом НЕ сбрасывается
        // и не обнуляется — снимок состояния остаётся тем же, поэтому и значения
        // шкал, и покой/нагрузка, и ночной коэффициент читаются правильно.
        var clockRunning = _runtime.SimulationRunning;

        // Обработка таймеров реального времени (эффектов, баффов и дебаффов)
        // не зависит от симуляции: системное время не останавливается вместе с
        // игровым. Бафф, выданный на 15 реальных минут, продолжает истекать и
        // при выключенной симуляции, и в интерфейсе это видно.
        if (_conditionsLastRealTick is null || _conditionsLastGameElapsed is null)
        {
            _conditionsLastRealTick = now;
            _conditionsLastGameElapsed = clock.Elapsed;
            _conditionsLastPosition = _hub.Get<PlayerState>("player").Value.Position;
            return;
        }

        var previousElapsed = _conditionsLastGameElapsed.Value;
        var previousPosition = _conditionsLastPosition;
        var realSeconds = Math.Max(0d, (now - _conditionsLastRealTick.Value).TotalSeconds);

        // При остановленных часах игровой прирост равен нулю: шкалы не меняют
        // значений, но домен всё равно обрабатывает таймеры эффектов и строит
        // текущий срез мира.
        var gameSeconds = clockRunning
            ? Math.Max(0d, (clock.Elapsed - previousElapsed).TotalSeconds)
            : 0d;

        _conditionsLastRealTick = now;
        _conditionsLastGameElapsed = clock.Elapsed;

        if (realSeconds <= 0d && gameSeconds <= 0d)
            return;

        var currentPlayer = _hub.Get<PlayerState>("player").Value;
        // «В движении» определяется ВКЛЮЧЁННЫМ движением по маршруту, а не
        // мгновенной скоростью игрока:
        //
        //  • Покой — это состояние, когда движение по маршруту ВЫКЛЮЧЕНО. Тогда
        //    усталость восстанавливается. Пока маршрут включён, игрок считается
        //    движущимся даже в кадре с нулевой скоростью (разгон, пауза между
        //    пакетами), и отдых ему не начисляется.
        //  • Пауза/стоп не меняют это состояние: кнопка движения по маршруту —
        //    часть мира, а не симуляции, поэтому покой и нагрузка считаются по
        //    ней в любом состоянии. Меняться не должны только ЗНАЧЕНИЯ шкал —
        //    они зависят от игрового времени, а оно стоит.
        var moving = _routeEnabled;

        // Усталость начисляется и за пройденную дистанцию, а не только за
        // игровое время. Игровое время идёт 1:1 с реальным, поэтому «100% за 18
        // часов» давало 5.6% в час — в пределах сессии незаметно, и усталость
        // выглядела неработающей. Дистанция за тик берётся из фактического
        // смещения игрока: так она учитывает и движение по маршруту, и любые
        // другие источники перемещения, без второй бухгалтерии скорости.
        //
        // При остановленных часах игрок не перемещается (UpdateRouteMovement
        // не двигает его), поэтому дистанция за тик равна нулю — вторая шкала,
        // зависящая от времени, сама собой замирает.
        var traveledMeters = 0d;

        if (moving &&
            clockRunning &&
            previousPosition is { })
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

        var update = CharacterVitalsEngine.Advance(
            currentVitals,
            currentConditions,
            gameSeconds,
            realSeconds,
            moving,
            sleeping: false,
            traveledMeters,
            clock.Now.TimeOfDay.TotalHours);

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
            // Скорости шкал едут и в live_state: окно «Игрок» обновляется им, а
            // подсказка шкалы показывает скорость — без этого поля подсказка
            // показывала бы ноль до следующего полного снимка.
            conditionRates = BuildConditionRates(_hub.GetSnapshot()),
            // Желудок едет и в live_state: порции усваиваются на ходу, и их сроки
            // меняются каждую секунду — без этого блока таймеры в мониторе
            // обновлялись бы только раз в полный снимок.
            digestion = BuildDigestionSnapshot(_hub.GetSnapshot()),
            // Каталог употребимого — тоже: меню желудка открывается ПКМ, и без
            // него список был бы пуст, если полный снимок ещё не приходил.
            //
            // Окно инвентаря читает ОТСЮДА признаки `fits` и объёмы: у него нет
            // ни пищевых профилей, ни вместимости желудка, и правило «объём
            // больше свободного места — нельзя» обязано приходить готовым.
            // Снимок инвентарю приходит редко (по изменению мира), поэтому из
            // живого обновления он один и узнаёт, что желудок успел наполниться.
            consumables = BuildConsumablesCatalog(),
            itemCatalog = ItemCatalogFactory.CreateStarter(),
            runtime = _runtime.State,
            simulationRunning = _runtime.SimulationRunning,
            simulationPaused = _runtime.IsPaused,
            simulationSpeed = _runtime.SimulationSpeed,
            ets2Sync = _ets2SyncMonitor.ViewState,
            ets2SyncRequired = _ets2SyncRequired,
            telemetryFollowEnabled = _telemetryFollowEnabled,
            route = BuildRouteSnapshot(),
            lodging = BuildLodgingSnapshot()
        }, SnapshotJsonOptions);

        PostJson(payload);

        // Монитор показателей обновляется ТЕМ ЖЕ live_state, что и карта:
        // он показывает текущие значения и скорости, а живое обновление ему
        // нужнее, чем карте — именно за изменениями он и наблюдает.
        if (_indicatorsForm is not null && !_indicatorsForm.IsDisposed)
            _indicatorsForm.PushLiveStateJson(payload);

        // Инвентарь получает live_state ТОЖЕ — из-за одного поля: признака
        // «помещается ли предмет в желудок». Он меняется каждую секунду по мере
        // усвоения, а полный снимок приходит редко (по изменению мира), поэтому
        // без этого пункт «Использовать» оставался бы активным, пока желудок уже
        // полон, и предмет молча не съедался бы. Остальные поля инвентарь из
        // живого обновления не читает.
        if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
            _inventoryForm.PushLiveStateJson(payload);

        // Окна перков и предметов тоже живое состояние: таймеры баффов тикают, а
        // количество предметов меняется от еды и квестов.
        PushPerks();
        PushItems();
    }

    private void SetFact(JsonElement root)
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

    private void GrantInventoryItem(JsonElement root)
    {
        var itemId = Required(root, "itemId").Trim();
        var amount = Math.Clamp(
            (int)Math.Round(Number(root, "amount", 1d)),
            1,
            999);

        var definition = ItemCatalogFactory.CreateStarter()
            .FirstOrDefault(item =>
                item.Id.Equals(
                    itemId,
                    StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            PostSaveError("Предмет отсутствует в каталоге Character Vitals.");
            return;
        }

        var channel = _hub.Get<InventoryState>("inventory");
        var state = channel.Value;
        var items = new Dictionary<string, int>(
            state.Items,
            StringComparer.OrdinalIgnoreCase);
        items.TryGetValue(itemId, out var oldAmount);
        items[itemId] = Math.Max(0, oldAmount) + amount;

        var newIds = new HashSet<string>(
            state.NewItemIds,
            StringComparer.OrdinalIgnoreCase);
        newIds.Add(itemId);

        channel.Set(
            new InventoryState(items, newIds.ToArray()),
            "Выдача предмета");

        if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
            _inventoryForm.NotifyInventoryChange(itemId, amount);

        AppendJournal(
            "ItemGranted",
            DateTimeOffset.UtcNow,
            "Инвентарь",
            $"Выдан предмет [[item:{itemId}:{definition.Name}]] {definition.Name} ×{amount}.",
            _hub.Get<PlayerState>("player").Value.Position);

        PersistSession(
            "автосохранение: выдача предмета",
            force: true);
        RequestSnapshot("inventory item granted");
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

    private void UseInventoryItem(JsonElement root) =>
        UseInventoryItemCore(Required(root, "itemId"));

    private void DropInventoryItem(JsonElement root) =>
        DropInventoryItemCore(Required(root, "itemId"));

    /// <summary>
    /// «Выбросить» из меню ПКМ: количество уменьшается на одну единицу.
    ///
    /// Отдельный путь от употребления: выброс НЕ должен запускать механику
    /// предмета (иначе «Выбросить обезболивающее» лечило бы игрока), а предмет с
    /// нулевым количеством уходит из инвентаря совсем, чтобы пустая ячейка не
    /// висела как «×0».
    /// </summary>
    private void DropInventoryItemCore(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var channel = _hub.Get<InventoryState>("inventory");
        var inventory = channel.Value;
        if (!inventory.Items.TryGetValue(itemId, out var quantity) || quantity <= 0)
        {
            PostSaveError("Предмет закончился.");
            return;
        }

        var items = new Dictionary<string, int>(
            inventory.Items,
            StringComparer.OrdinalIgnoreCase);

        if (quantity <= 1)
            items.Remove(itemId);
        else
            items[itemId] = quantity - 1;

        channel.Set(
            new InventoryState(items, inventory.NewItemIds),
            "Выброс предмета");

        if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
            _inventoryForm.NotifyInventoryChange(itemId, -1);

        AppendJournal(
            "ItemDropped",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            "Выброшен предмет: [[item:" + itemId + ":" + ItemLabel(itemId) + "]] " +
            ItemLabel(itemId) + ".",
            _hub.Get<PlayerState>("player").Value.Position);

        PersistSession("автосохранение: выброс предмета", force: true);
        RequestSnapshot("inventory item dropped");
    }

    private void UseInventoryItemCore(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var channel = _hub.Get<InventoryState>("inventory");
        var inventory = channel.Value;
        if (!inventory.Items.TryGetValue(itemId, out var quantity) || quantity <= 0)
        {
            PostSaveError("Предмет закончился.");
            return;
        }

        if (!TryApplyItemUse(itemId))
            return;

        var items = new Dictionary<string, int>(
            inventory.Items,
            StringComparer.OrdinalIgnoreCase)
        {
            [itemId] = quantity - 1
        };

        channel.Set(
            new InventoryState(items, inventory.NewItemIds),
            "Употребление предмета");

        if (_inventoryForm is not null && !_inventoryForm.IsDisposed)
            _inventoryForm.NotifyInventoryChange(itemId, -1);

        AppendJournal(
            "ItemUsed",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            "Использован предмет: [[item:" + itemId + ":" + ItemLabel(itemId) + "]] " +
            ItemLabel(itemId) + ".",
            _hub.Get<PlayerState>("player").Value.Position);

        PersistSession("автосохранение: употребление предмета", force: true);
        RequestSnapshot("inventory item used");
    }

    /// <summary>
    /// Применяет механику предмета к шкалам и условиям — ОБЩАЯ часть обоих
    /// способов употребления (из инвентаря и из каталога в желудке).
    ///
    /// Здесь только последствия для организма: ни наличия, ни списания. Списывает
    /// инвентарь вызывающий, и делает это лишь тот путь, для которого инвентарь
    /// вообще важен.
    /// </summary>
    /// <returns>
    /// <c>false</c>, если предмет применить нельзя (нет механики) — тогда ничего
    /// не изменено и ошибка уже отправлена в интерфейс.
    /// </returns>
    private bool TryApplyItemUse(string itemId)
    {
        var update = CharacterVitalsEngine.UseItem(
            itemId,
            _hub.Get<PlayerVitalsState>("player-vitals").Value,
            _hub.Get<PlayerConditionState>("player-conditions").Value,
            _hub.Get<WorldClockState>("sim-time").Value.Now.TimeOfDay.TotalHours);

        if (update.Events.Any(item => item.Kind == "UnknownItem"))
        {
            PostSaveError("Для этого предмета нет механики употребления.");
            return false;
        }

        _hub.Get<PlayerVitalsState>("player-vitals").Set(update.Vitals, "Употребление предмета");
        _hub.Get<PlayerConditionState>("player-conditions").Set(update.Conditions, "Употребление предмета");

        return true;
    }

    /// <summary>
    /// Употребляет предмет ИЗ КАТАЛОГА, минуя инвентарь (выбор в меню желудка).
    ///
    /// Автор задал это действие как замену ТРЁХ шагов: «поиск предмета, выдача в
    /// инвентарь и нажатие в меню "использовать"» — «выбрал — попало в желудок».
    /// Поэтому инвентарь здесь не участвует вовсе: ни наличия, ни количества, ни
    /// списания.
    ///
    /// Именно на этом ломалось прежнее поведение. Выбор в меню шёл через
    /// <see cref="UseInventoryItemCore"/>, а тот первым делом требует предмет В
    /// ИНВЕНТАРЕ — и на предмете, которого там нет, МОЛЧА выходил (единственный
    /// отклик, «Предмет закончился.», страница монитора не показывает). Снаружи
    /// это выглядело так, будто клик по пункту не делает ничего: меню
    /// закрывалось, а желудок оставался прежним.
    /// </summary>
    private void ConsumeStomachItem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        if (!TryApplyItemUse(itemId))
            return;

        // Журнал отличается от инвентарного: по нему потом и видно, каким путём
        // предмет попал в организм, — а без различия разбор такого случая снова
        // упёрся бы в «действие пришло, а дальше ничего».
        AppendJournal(
            "ItemUsed",
            DateTimeOffset.UtcNow,
            "Состояние игрока",
            "Употреблён предмет из каталога: [[item:" + itemId + ":" +
            ItemLabel(itemId) + "]] " + ItemLabel(itemId) + ".",
            _hub.Get<PlayerState>("player").Value.Position);

        AppLogger.Info("SimulatorForm: предмет употреблён из каталога (без инвентаря).",
            $"item={itemId}");

        PersistSession("автосохранение: употребление из каталога", force: true);
        RequestSnapshot("stomach item consumed");
    }

    private void FindFullLodging()
    {
        if (_routeEnabled)
        {
            PostSaveError("Перед поиском ночлега выключите «Движение по маршруту».");
            return;
        }

        var player = _hub.Get<PlayerState>("player").Value;
        var city = _cityBoundaries.Current.FindCity(
            player.Position.X,
            player.Position.Z);

        if (city is null)
        {
            PostSaveError("Игрок должен находиться в границах города.");
            return;
        }

        var money = _hub.Get<PlayerProgressState>("player-progress").Value.Money;
        if (money < CharacterVitalsEngine.HotelPrice)
        {
            PostSaveError(
                $"Полноценный ночлег стоит " +
                $"{CharacterVitalsEngine.HotelPrice:0} рублей.");
            return;
        }

        _hub.Get<InterfaceState>("interfaces").Set(
            new InterfaceState(
                null,
                new InterfaceDialogue(
                    "hotel-" + Guid.NewGuid().ToString("N"),
                    "Полноценный ночлег",
                    city.CityName,
                    "Вы получили полноценный сон в гостинице или отеле",
                    "ОК")),
            "Гостиница");

        RequestSnapshot("hotel lodging dialog");
    }

    private void CompleteFullLodging(string requestId)
    {
        var dialogue = _hub.Get<InterfaceState>("interfaces").Value.ActiveDialogue;
        if (dialogue is null ||
            !dialogue.RequestId.Equals(requestId, StringComparison.OrdinalIgnoreCase) ||
            !dialogue.Title.Equals("Полноценный ночлег", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запрос гостиницы уже неактуален.");

        var player = _hub.Get<PlayerState>("player").Value;
        if (_cityBoundaries.Current.FindCity(player.Position.X, player.Position.Z) is null)
            throw new InvalidOperationException("Игрок больше не находится в границах города.");

        var progressChannel = _hub.Get<PlayerProgressState>("player-progress");
        var progress = progressChannel.Value;
        if (progress.Money < CharacterVitalsEngine.HotelPrice)
            throw new InvalidOperationException(
                $"Для полноценного ночлега нужно " +
                $"{CharacterVitalsEngine.HotelPrice:0} рублей.");

        var update = CharacterVitalsEngine.CompleteHotelSleep(
            _hub.Get<PlayerVitalsState>("player-vitals").Value,
            _hub.Get<PlayerConditionState>("player-conditions").Value);

        if (update.Events.Any(item => item.Kind == "SleepBlocked"))
            throw new InvalidOperationException(
                $"Сон невозможен: здоровье, энергия или жидкость должны быть выше " +
                $"{CharacterVitalsTuning.SleepBlockedBelowPercent:0.#}%.");

        var beforeVitals = _hub.Get<PlayerVitalsState>("player-vitals").Value;
        var beforeConditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        _hub.Get<PlayerVitalsState>("player-vitals").Set(update.Vitals, "Полноценный ночлег");
        _hub.Get<PlayerConditionState>("player-conditions").Set(update.Conditions, "Полноценный ночлег");
        progressChannel.Set(
            progress with { Money = progress.Money - (int)CharacterVitalsEngine.HotelPrice },
            "Оплата гостиницы");

        var clockChannel = _hub.Get<WorldClockState>("sim-time");
        var nextElapsed = clockChannel.Value.Elapsed + TimeSpan.FromHours(CharacterVitalsEngine.HotelSleepHours);
        clockChannel.Set(clockChannel.Value with { Elapsed = nextElapsed }, "Полноценный ночлег");

        _hub.Get<InterfaceState>("interfaces").Set(
            new InterfaceState(null, null),
            "Гостиница");

        _conditionsLastRealTick = DateTimeOffset.UtcNow;
        _conditionsLastGameElapsed = nextElapsed;
        _conditionsLastPosition = player.Position;

        AppendJournal(
            "HotelSleep",
            DateTimeOffset.UtcNow,
            "Гостиница",
            $"Полноценный ночлег: {CharacterVitalsEngine.HotelSleepHours} игровых часов, " +
            $"{CharacterVitalsEngine.HotelPrice:0} рублей.",
            player.Position);

        // Отчёт об изменении состояния: автор просил после ночлега, сна и отдыха
        // показывать в журнале, ЧТО именно изменилось и какие перки получены.
        // Считает домен — сравнение двух состояний это правило, а не текст
        // интерфейса, и держать его здесь значило бы дублировать знание о шкалах.
        AppendStateReport(
            "HotelSleepReport",
            "Гостиница",
            beforeVitals,
            beforeConditions,
            update.Vitals,
            update.Conditions);

        PersistSession("автосохранение: полноценный ночлег", force: true);
        RequestSnapshot("hotel lodging completed");
    }

    /// <summary>
    /// Печатает в журнал отчёт об изменении состояния после события.
    ///
    /// Отдельная запись, а не дописанный текст к событию: строки содержат разметку
    /// ссылок (<c>[[metric:energy]]</c>, <c>[[perk:...]]</c>), и журнал рисует их
    /// подчёркнутыми и кликабельными. Слитая запись потеряла бы позиции ссылок —
    /// их считает <see cref="JournalForm"/> по фактически набранному тексту.
    ///
    /// Многострочность — <c>\n</c>: журнал разбирает разметку сам, а перевод строки
    /// печатается как есть, поэтому отчёт читается списком.
    /// </summary>
    private void AppendStateReport(
        string eventType,
        string source,
        PlayerVitalsState beforeVitals,
        PlayerConditionState beforeConditions,
        PlayerVitalsState afterVitals,
        PlayerConditionState afterConditions)
    {
        var lines = CharacterStateReport.Describe(
            beforeVitals,
            beforeConditions,
            afterVitals,
            afterConditions);

        AppendJournal(
            eventType,
            DateTimeOffset.UtcNow,
            source,
            string.Join("\n", lines),
            null);
    }

    private void SetPlayerVitals(JsonElement root)
    {
        var current = _hub.Get<PlayerVitalsState>("player-vitals").Value;

        // Редактор вводит ПРОЦЕНТЫ, а хранилище ведёт единицы шкалы: перевод
        // делается здесь, ровно на границе между интерфейсом и состоянием.
        var next = current with
        {
            Health = PlayerConditionScale.FromPercent(Number(root, "health", PlayerConditionScale.ToPercent(current.Health))),
            Energy = PlayerConditionScale.FromPercent(Number(root, "energy", PlayerConditionScale.ToPercent(current.Energy))),
            Hydration = PlayerConditionScale.FromPercent(Number(root, "hydration", PlayerConditionScale.ToPercent(current.Hydration))),
            Fatigue = PlayerConditionScale.FromPercent(Number(root, "fatigue", PlayerConditionScale.ToPercent(current.Fatigue))),
            Resilience = PlayerConditionScale.FromPercent(Number(root, "resilience", PlayerConditionScale.ToPercent(current.Resilience))),
            Metabolism = PlayerConditionScale.FromPercent(Number(root, "metabolism", PlayerConditionScale.ToPercent(current.Metabolism)))
        };

        // Стресс — часть условий, а не потребностей, но правится из того же блока
        // «Состояние персонажа». Поле необязательное: отсутствие ключа означает
        // «оставить как есть», иначе применение здоровья сбрасывало бы стресс.
        var conditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        if (root.TryGetProperty("stress", out var stressElement) &&
            stressElement.ValueKind == JsonValueKind.Number &&
            stressElement.TryGetDouble(out var stress) &&
            double.IsFinite(stress))
        {
            conditions = conditions with
            {
                Stress = PlayerConditionScale.FromPercent(stress)
            };
        }

        _hub.Get<PlayerVitalsState>("player-vitals").Set(next.Normalize(), "Редактор состояния персонажа");
        _hub.Get<PlayerConditionState>("player-conditions").Set(conditions.Normalize(), "Редактор состояния персонажа");
    }

    /// <summary>
    /// Правка стресса из блока «Состояние персонажа».
    ///
    /// Поле ввода — проценты, а состояние хранит единицы шкалы, поэтому перевод
    /// делается здесь. Кумулятивный стресс НЕ трогается: по правилам он снимается
    /// только отпуском, и кнопка «Снять стресс» не должна его обнулять — иначе
    /// редактор обходил бы единственный задуманный способ избавления.
    /// </summary>
    private void SetStress(JsonElement root)
    {
        var conditions = _hub.Get<PlayerConditionState>("player-conditions").Value;
        var stress = Number(root, "stress", PlayerConditionScale.ToPercent(conditions.Stress));

        _hub.Get<PlayerConditionState>("player-conditions").Set(
            conditions with { Stress = PlayerConditionScale.FromPercent(stress) },
            "Редактор состояния персонажа");
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

    private void SetTelemetryFollow(JsonElement root)
    {
        var enabled = root.GetProperty("enabled").GetBoolean();

        if (enabled && !_ets2SyncMonitor.ViewState.CareerConnected)
        {
            _telemetryFollowEnabled = false;
            _ets2TruckTelemetry.Stop();
            throw new InvalidOperationException("Телеметрия доступна только при подключённой карьере ETS2.");
        }

        if (enabled)
        {
            // Телеметрия полностью забирает управление позицией у маршрута.
            // Поэтому при её включении редактирование выключается, а старый
            // маршрут очищается, чтобы он не мог возобновиться после отключения.
            ResetRouteForTelemetry();

            _telemetryFollowEnabled = true;
            _lastAppliedTelemetrySampleAt = null;
            _ets2TruckTelemetry.Start();

            // Включение телеметрии — это одновременно запуск симуляции. Если
            // симулятор был на паузе, StartSimulation корректно продолжит его;
            // следующее состояние TruckTel тут же вернёт паузу, если сама ETS2
            // уже стоит на паузе.
            StartSimulation();
        }
        else
        {
            _telemetryFollowEnabled = false;
            _lastAppliedTelemetrySampleAt = null;
            _ets2TruckTelemetry.Stop();
        }

        AppLogger.Info(
            "SimulatorForm: следование позиции по телеметрии изменено.",
            $"enabled={enabled}; careerConnected={_ets2SyncMonitor.ViewState.CareerConnected}");
        RequestSnapshot("telemetry follow changed");
    }

    private void ResetRouteForTelemetry()
    {
        var hadRoute =
            _routeState.Waypoints.Count > 0 ||
            _routePlan.Points.Count > 0 ||
            _routeEnabled ||
            _routeEditingEnabled;

        _routeState = RouteState.Empty;
        _routePlan = RoutePlan.Empty;
        _routeCursor = RouteCursor.Initial;
        _selectedRouteWaypointId = null;
        _routeStoppedWaypointIndex = null;
        _routeStoppedWaypointId = null;
        _routeTargetWaypointIndex = null;
        _routeTargetWaypointId = null;
        _routeTravelRealSeconds = 0d;
        _routeTravelGameSeconds = 0d;
        _routeEnabled = false;
        _routeEditingEnabled = false;
        _routeMovementLastTick = null;
        SetPlayerMovementIdle();

        if (hadRoute)
            PersistSession("автосохранение: маршрут сброшен для телеметрии", force: true);
    }

    private void ApplyExternalTelemetry()
    {
        if (_ets2SyncRequired && !_ets2SyncMonitor.ViewState.CareerConnected)
        {
            if (_telemetryFollowEnabled)
            {
                _telemetryFollowEnabled = false;
                _ets2TruckTelemetry.Stop();
                _lastAppliedTelemetrySampleAt = null;
            }

            return;
        }

        if (!_telemetryFollowEnabled ||
            !_ets2TruckTelemetry.TryGetSnapshot(out var telemetry))
        {
            return;
        }

        // TruckTel передаёт отдельный признак состояния игры. Пока поток свежий,
        // он является источником истины и для паузы AQE: ручная пауза внутри AQE
        // не должна расходиться с паузой ETS2.
        if (telemetry.Live)
        {
            if (telemetry.GamePaused && _runtime.SimulationRunning)
                PauseSimulation();
            else if (!telemetry.GamePaused && _runtime.IsPaused)
                ResumeSimulation();
        }

        if (_lastAppliedTelemetrySampleAt == telemetry.SampleAt)
            return;

        _lastAppliedTelemetrySampleAt = telemetry.SampleAt;

        var telemetryChannel = _hub.Get<TelemetryState>("telemetry");
        var oldTelemetry = telemetryChannel.Value;
        telemetryChannel.Set(
            oldTelemetry with
            {
                SpeedKmh = telemetry.SpeedKmh,
                TruckPosition = new WorldCoordinate(telemetry.X, telemetry.Y, telemetry.Z),
                TruckHeading = telemetry.HeadingDegrees,
                ExternalLive = telemetry.Live,
                ExternalSampleAt = telemetry.SampleAt
            },
            "ETS2 TruckTel");

        if (!telemetry.Live)
            return;

        var playerChannel = _hub.Get<PlayerState>("player");
        var player = playerChannel.Value;
        var position = new WorldCoordinate(telemetry.X, telemetry.Y, telemetry.Z);
        var paused = telemetry.GamePaused || _runtime.IsPaused;

        if (Distance2D(player.Position, position) < 0.01d &&
            Math.Abs(player.Heading - telemetry.HeadingDegrees) < 0.05d &&
            Math.Abs(player.SpeedKmh - telemetry.SpeedKmh) < 0.01d &&
            player.Paused == paused)
        {
            return;
        }

        playerChannel.Set(
            player with
            {
                Position = position,
                SpeedKmh = telemetry.SpeedKmh,
                Heading = telemetry.HeadingDegrees,
                Paused = paused
            },
            "ETS2 телеметрия");
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

        var hours = fullSleep
            ? CharacterVitalsTuning.FullSleepHours
            : CharacterVitalsTuning.FieldSleepHours;
        var currentVitals = _hub.Get<PlayerVitalsState>("player-vitals").Value;
        var currentConditions = _hub.Get<PlayerConditionState>("player-conditions").Value;

        var update = CharacterVitalsEngine.Sleep(
            currentVitals,
            currentConditions,
            hours,
            fullSleep);

        if (update.Events.Any(item => item.Kind == "SleepBlocked"))
        {
            PostSaveError(
                $"Сон невозможен: здоровье, энергия или жидкость должны быть выше " +
                $"{CharacterVitalsTuning.SleepBlockedBelowPercent:0.#}%.");
            return;
        }

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
                ? $"Полноценный сон: {hours} игровых часа."
                : $"Полевой сон: {hours} игровых часов.");

        // Отчёт об изменении состояния — как и у ночлега: автор просил видеть
        // после сна, что именно изменилось и какие перки получены.
        AppendStateReport(
            fullSleep ? "FullSleepReport" : "FieldSleepReport",
            "Состояние игрока",
            currentVitals,
            currentConditions,
            update.Vitals,
            update.Conditions);
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

    private void ResetWorldForNewEts2Career(string reason)
    {
        ResetWorldState(reason, stopSimulation: true);
    }

    private void ResetWorldState(string reason, bool stopSimulation)
    {
        if (stopSimulation)
        {
            _inventoryPausedSimulation = false;
            _runtime.SetSimulationRunning(false);
            _dynamicEventDispatcher.SetSimulationRunning(false);
            _ets2TruckTelemetry.Stop();
            _telemetryFollowEnabled = false;
            _lastAppliedTelemetrySampleAt = null;
        }

        if (_locationResolver is ILocationResolutionSession locationSession)
            locationSession.Reset();

        if (_hub is SimulatorDataChannelHub simulatorHub)
            simulatorHub.Reset();

        _runtime.Reset();
        _dynamicEventDispatcher.Reset();
        _saveStore.ClearSession();
        _autoSaveAt = null;
        _mapView = null;
        _mapViewRestoreToken++;
        SetRouteStateAfterLoad(RouteState.Empty);
        ApplyWorldFromCampaign(reason);
        RequestSnapshot("world reset: " + reason);
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

        if (dialogue.Title.Equals("Полноценный ночлег", StringComparison.OrdinalIgnoreCase) &&
            dialogue.ButtonText.Equals("ОК", StringComparison.OrdinalIgnoreCase))
        {
            CompleteFullLodging(dialogue.RequestId);
            return;
        }

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
    /// Запоминает выбор галочки «%/время» и возвращает монитору свежий снимок.
    ///
    /// Пишется только ИЗМЕНЕНИЕ: щелчок по галочке — редкое действие, но
    /// сравнение всё равно нужно — без него повторное сообщение с тем же значением
    /// переписывало бы файл настроек на диск.
    /// </summary>
    private void SetPercentUnits(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var valueNode) ||
            (valueNode.ValueKind != JsonValueKind.True &&
             valueNode.ValueKind != JsonValueKind.False))
        {
            return;
        }

        var requested = valueNode.GetBoolean();

        if (requested == _percentUnits)
            return;

        _percentUnits = requested;

        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { PercentUnits = _percentUnits });

        AppLogger.Info(
            "SimulatorForm: единицы монитора показателей переключены.",
            $"percentUnits={_percentUnits}");

        // Снимок возвращается СРАЗУ: галочка — мгновенное действие, и ожидание
        // следующего тика (до секунды) читалось бы как «не сработало».
        RequestSnapshot("percent units changed");
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
        _journalForm.MetricClicked += JournalForm_MetricClicked;
        _journalForm.PerkClicked += JournalForm_PerkClicked;
        _journalForm.ItemClicked += JournalForm_ItemClicked;
        _journalForm.SetEntries(_journalEntries);
        _journalForm.ReturnToSidebarRequested += JournalForm_ReturnToSidebarRequested;
        _journalForm.FormClosed += (_, _) =>
        {
            _journalForm!.GlobalHotKeyPressed -= SimulatorForm_GlobalHotKeyPressed;
            _journalForm.CoordinateClicked -= JournalForm_CoordinateClicked;
            _journalForm.MetricClicked -= JournalForm_MetricClicked;
            _journalForm.PerkClicked -= JournalForm_PerkClicked;
            _journalForm.ItemClicked -= JournalForm_ItemClicked;
            _journalForm = null;
        };
        _journalForm.Show(this);
        QuestLogger.Info("Journal: native окно показано.", QuestLogger.Json(new { entryCount = _journalEntries.Count }));
    }

    /// <summary>
    /// Клик по имени показателя в журнале: открывает монитор и подсвечивает
    /// блок этой шкалы.
    ///
    /// Подсветка живёт в ОКНЕ монитора, а не в журнале: «какой блок показать»
    /// теряет смысл, когда окно закрыто, и хранить это между окнами значило бы
    /// держать состояние, у которого нет владельца.
    /// </summary>
    private void JournalForm_MetricClicked(string key)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action<string>)JournalForm_MetricClicked, key);
            return;
        }

        OpenIndicatorsWindow();
        _indicatorsForm?.Highlight(key);
    }

    /// <summary>Клик по имени перка, баффа или скилла: окно «Перки, баффы, скиллы».</summary>
    private void JournalForm_PerkClicked(string perkId, string perkKind)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action<string, string>)JournalForm_PerkClicked, perkId, perkKind);
            return;
        }

        OpenPerksWindow(perkId, perkKind);
    }

    /// <summary>Клик по названию предмета в журнале: окно «Предметы».</summary>
    private void JournalForm_ItemClicked(string itemId)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action<string>)JournalForm_ItemClicked, itemId);
            return;
        }

        OpenItemsWindow(itemId);
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
        WorldCoordinate? coordinate = null,
        bool compact = false,
        string? textColor = null,
        int fontWeight = 400)
    {
        if (!compact)
            coordinate ??= _hub.Get<PlayerState>("player").Value.Position;
        _journalEntries.Insert(
            0,
            new SimulatorJournalEntry(
                eventType,
                timestamp,
                source,
                message,
                coordinate,
                compact,
                textColor,
                fontWeight));
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
        // RemainingRouteSeconds возвращает ИГРОВЫЕ секунды пути (движение по
        // маршруту идёт в игровом времени). Реальное время получается делением
        // на кратность ускорения: при ×8 маршрут проезжается в 8 раз быстрее
        // по настенным часам, а игровая длительность не меняется.
        var remainingGame = RemainingRouteSeconds();
        var speedScale = Math.Max(0d, _runtime.SimulationSpeed);
        var remainingReal = remainingGame.HasValue
            ? (speedScale > 0d
                ? remainingGame.Value / speedScale
                : remainingGame.Value)
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
            // EstimateRouteSeconds считает ИГРОВЫЕ секунды пути: движение по
            // маршруту идёт в игровом времени, поэтому и скорость поедания
            // маршрута масштабируется кратностью ускорения времени.
            //   * время прибытия — игровой момент плюс игровая длительность;
            //   * обратный отсчёт на карте — РЕАЛЬНЫЕ секунды, то есть игровые,
            //     делённые на кратность (при ×8 до точки в 8 раз меньше реального
            //     времени, хотя игровая метка та же).
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
                        clock.Now + TimeSpan.FromSeconds(countdown.Value))
                    : "—",
                countdownRealSeconds = countdown.HasValue
                    ? (speedScale > 0d
                        ? countdown.Value / speedScale
                        : countdown.Value)
                    : countdown
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
            stoppedWaypointId = _routeStoppedWaypointId,
            currentTargetWaypointIndex = _routeTargetWaypointIndex,
            currentTargetWaypointId = _routeTargetWaypointId,
            nextRoutePointId,
            travelTimeRealSeconds = _routeTravelRealSeconds,
            travelTimeGameSeconds = _routeTravelGameSeconds,
            totalDistanceMeters = totalDistance,
            distanceFromFirstWaypointMeters = currentDistance,
            editing = _routeEditingEnabled,
            editingAllowed = !_runtime.SimulationRunning && !_runtime.IsPaused && !_telemetryFollowEnabled,
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
    /// Запускает режим прохождения.
    ///
    /// Автозагрузка выполняется ОДИН раз при открытии Симулятора
    /// (<see cref="AutoLoadWorld"/>), а не здесь: мир должен быть уже
    /// восстановлен к моменту, когда пользователь жмёт play. Поэтому запуск —
    /// это только включение часов и Runtime.
    /// </summary>
    private void Ets2SyncMonitor_StateChanged(Ets2SyncViewState state)
    {
        if (_ets2SyncRequired && !state.CareerConnected)
        {
            if (_telemetryFollowEnabled)
            {
                _telemetryFollowEnabled = false;
                _ets2TruckTelemetry.Stop();
                _lastAppliedTelemetrySampleAt = null;
            }

            // Несвязанная активная карьера блокирует симулятор. Не вызываем
            // StopSimulation(), чтобы не записывать состояние мира от старой
            // карьеры как будто это сохранение новой.
            if (_runtime.SimulationRunning || _runtime.IsPaused)
            {
                _runtime.SetSimulationRunning(false);
                _dynamicEventDispatcher.SetSimulationRunning(false);
                SetPlayerMovementIdle();
            }

            // Открытое окно профилей тоже не должно продолжать показывать старую
            // связанную карьеру, когда ETS2 уже переключился на другую.
            if (_ets2ProfilesForm is not null && !_ets2ProfilesForm.IsDisposed)
                _ets2ProfilesForm.ClearProfile();
        }
        else if (state.CareerConnected &&
                 !string.IsNullOrWhiteSpace(state.HexFolder) &&
                 _ets2ProfilesForm is not null &&
                 !_ets2ProfilesForm.IsDisposed)
        {
            // Если справочник профилей открыт, он автоматически переключается на
            // профиль, который сейчас связан с активной карьерой ETS2.
            _ets2ProfilesForm.OpenProfileByHexFolder(state.HexFolder);
        }

        RequestSnapshot("ETS2 sync state changed");
    }

    private void StartSimulation()
    {
        if (_ets2SyncRequired && !_ets2SyncMonitor.ViewState.CareerConnected)
        {
            AppLogger.Warn("SimulatorForm: запуск симуляции заблокирован — карьера ETS2 не подключена.");
            RequestSnapshot("simulation start blocked: no ETS2 career");
            return;
        }

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
                $"target={CurrentTargetNumberText()}");
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
            $"target={CurrentTargetNumberText()}");
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

    /// <summary>
    /// Строка сообщения или <c>null</c>, если она пуста.
    ///
    /// Отличается от <see cref="String"/> намеренно: у ссылки на показатель или
    /// предмет пустое значение — это ОТСУТСТВИЕ подсветки, а не пустое имя, и
    /// склеивать эти два случая значило бы подсвечивать несуществующий пункт.
    /// </summary>
    private static string? StringOrNull(JsonElement root, string name)
    {
        var value = String(root, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Логический флаг сообщения; отсутствие поля считается <c>false</c>.
    ///
    /// Строка «true» принимается наравне с логическим значением: страница может
    /// прислать его атрибутом, а разница между <c>true</c> и <c>"true"</c> — это
    /// подробность формата, а не смысла команды.
    /// </summary>
    private static bool Flag(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var parsed) && parsed,
            _ => false
        };
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
