using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class SimulatorForm : WebViewForm
{
    private readonly IDataChannelHub _hub;
    private readonly IQuestRuntimeController _runtime;
    private readonly QuestGraphStore _questGraph;
    private readonly CampaignStore _campaignStore;
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
    private bool _journalDetached;
    private bool _snapshotRequestScheduled;
    private bool _journalRefreshScheduled;
    // Выделение квеста живёт в Host, а не только в Web UI: карта, окно кампаний
    // и левый сайдбар должны показывать ОДИН выбранный квест.
    private string _selectedCampaignId = string.Empty;
    private string _selectedQuestId = string.Empty;

    public SimulatorForm(
        IDataChannelHub hub,
        IQuestRuntimeController runtime,
        QuestGraphStore questGraph,
        CampaignStore campaignStore,
        Action<string> openQuestEditor)
        : base(
            "Симулятор",
            "simulator.html",
            new Size(1440, 900),
            "simulator")
    {
        _hub = hub;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _questGraph = questGraph ?? throw new ArgumentNullException(nameof(questGraph));
        _campaignStore = campaignStore ?? throw new ArgumentNullException(nameof(campaignStore));
        _openQuestEditor = openQuestEditor ?? throw new ArgumentNullException(nameof(openQuestEditor));
        _journalDetached = AppUiPreferencesStore.Load().JournalDetached;
        Opacity = 0;
        _questGraph.Changed += QuestGraph_Changed;
        _runtime.Published += Runtime_Published;
        _runtimeTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _runtimeTimer.Tick += (_, _) => _runtime.Tick();
        _runtimeTimer.Start();

        if (_hub.Events is EventChannel<SimulatorEvent> events)
        {
            events.Published += Events_Published;
        }

        FormClosed += (_, _) =>
        {
            _journalForm?.Close();
            _journalForm = null;

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
        PushSnapshot();
        if (_journalDetached)
        {
            BeginInvoke((Action)OpenJournalWindow);
        }
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
            enabledQuestIds = _runtime.EnabledQuestIds,
            questCatalog = BuildQuestCatalog(snapshot),
            selectedQuest = new
            {
                campaignId = _selectedCampaignId,
                questId = _selectedQuestId
            },
            questGraph = _runtime.ActiveGraph ?? _questGraph.Value,
            journalDetached = _journalDetached
        }, SnapshotJsonOptions);

        AppLogger.Info("SimulatorForm: отправляю snapshot в WebView2.", $"jsonChars={payload.Length}; points={pointCount}");
        PostJson(payload);
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

                case "select_point":
                    SelectPoint(root);
                    break;

                case "clear_selection":
                    _hub.Get<WorldSelectionState>("world-selection").Set(
                        new WorldSelectionState(null, "Карта симулятора"),
                        "Карта симулятора");
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

                case "set_vitals":
                    SetPlayerVitals(root);
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

                case "open_journal":
                    OpenJournalWindow();
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
                    _runtime.SetSimulationRunning(true);
                    break;

                case "simulation_stop":
                    _runtime.SetSimulationRunning(false);
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
                    if (_hub is SimulatorDataChannelHub simulatorHub)
                    {
                        simulatorHub.Reset();
                    }
                    _runtime.Reset();
                    _runtime.SetSimulationRunning(false);
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

    private void SetPlayerPosition(JsonElement root)
    {
        var old = _hub.Get<PlayerState>("player").Value;
        var position = new WorldCoordinate(
            Number(root, "x", old.Position.X),
            Number(root, "y", old.Position.Y),
            Number(root, "z", old.Position.Z));

        _hub.Get<PlayerState>("player").Set(
            old with { Position = position },
            "Редактор игрока");

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
        _campaignsForm.SetCatalog(_campaignStore.BuildSimulatorCatalog());
        _campaignsForm.CampaignActiveChanged += CampaignsForm_CampaignActiveChanged;
        _campaignsForm.QuestEnabledChanged += CampaignsForm_QuestEnabledChanged;
        _campaignsForm.QuestOpenRequested += CampaignsForm_QuestOpenRequested;
        _campaignsForm.CampaignFolderOpenRequested += CampaignsForm_CampaignFolderOpenRequested;
        _campaignsForm.QuestSelected += CampaignsForm_QuestSelected;
        _campaignsForm.FormClosed += (_, _) =>
        {
            _campaignsForm = null;
            // Окно закрыли: выделение остаётся, но подсвечивать больше нечего.
            RequestSnapshot("campaign window closed");
        };
        if (_selectedQuestId.Length > 0)
            _campaignsForm.SelectQuest(_selectedCampaignId, _selectedQuestId);
        _campaignsForm.Show(this);
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

                    return new
                    {
                        questId = quest.QuestId,
                        questTitle = quest.Title,
                        order = quest.Order,
                        // Имя файла — второй ключ сортировки при равных номерах.
                        fileName = Path.GetFileName(quest.RelativePath),
                        worldPointId = quest.Activation?.WorldPointId ?? string.Empty,
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
        var key = Required(root, "itemId");
        var state = _hub.Get<InventoryState>("inventory").Value;
        var newIds = new HashSet<string>(state.NewItemIds, StringComparer.OrdinalIgnoreCase);
        if (!newIds.Remove(key))
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
        _hub.Get<PlayerVitalsState>("player-vitals").Set(next, "Редактор потребностей");
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

    private void DetachJournal()
    {
        QuestLogger.Info("Journal: отделение журнала.");
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
        _journalForm.SetEntries(_journalEntries);
        _journalForm.ReturnToSidebarRequested += JournalForm_ReturnToSidebarRequested;
        _journalForm.FormClosed += (_, _) => _journalForm = null;
        _journalForm.Show(this);
        QuestLogger.Info("Journal: native окно показано.", QuestLogger.Json(new { entryCount = _journalEntries.Count }));
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

    private void AppendJournal(string eventType, DateTimeOffset timestamp, string source, string message)
    {
        _journalEntries.Insert(0, new SimulatorJournalEntry(eventType, timestamp, source, message));
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
            entryCount = _journalEntries.Count,
            detached = _journalDetached
        }));

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
            double? radius = null;

            if (type == "interaction")
            {
                pointId = GetParameter(node, "worldPointId");
                radius = TryGetDouble(GetParameter(node, "triggerRadius"));
            }
            else if ((type is "condition" or "waitforcondition") &&
                     GetParameter(node, "operator").Equals("distancecompare", StringComparison.OrdinalIgnoreCase))
            {
                pointId = GetParameter(node, "worldPointId", GetParameter(node, "right"));
                radius = TryGetDouble(GetParameter(node, "triggerRadius"));
            }

            if (!string.IsNullOrWhiteSpace(pointId))
            {
                var point = _hub.Get<WorldState>("world").Value.Points.FirstOrDefault(x =>
                    x.Id.Equals(pointId, StringComparison.OrdinalIgnoreCase));
                var player = _hub.Get<PlayerState>("player").Value.Position;
                var distance = point is null ? (double?)null : Distance(player, point.Position);
                target = new
                {
                    pointId,
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
}
