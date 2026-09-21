using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class SimulatorForm : WebViewForm
{
    private readonly IDataChannelHub _hub;
    private readonly QuestRuntime _runtime;
    private readonly QuestGraphStore _questGraph;
    private readonly System.Windows.Forms.Timer _runtimeTimer;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private string _lastQuestSnapshotLogKey = string.Empty;
    private readonly List<SimulatorJournalEntry> _journalEntries = new();
    private JournalForm? _journalForm;
    private bool _journalDetached;

    public SimulatorForm(IDataChannelHub hub, QuestRuntime runtime, QuestGraphStore questGraph)
        : base(
            "Assist Quest Editor — Симулятор",
            "simulator.html",
            new Size(1440, 900),
            "simulator")
    {
        _hub = hub;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _questGraph = questGraph ?? throw new ArgumentNullException(nameof(questGraph));
        _journalDetached = AppUiPreferencesStore.Load().JournalDetached;
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
            runtime = _runtime.State,
            questGraph = _questGraph.Value,
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
            var action = root.GetProperty("action").GetString();

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

                case "detach_journal":
                    DetachJournal();
                    break;

                case "open_journal":
                    OpenJournalWindow();
                    break;

                case "return_journal_to_sidebar":
                    ReturnJournalToSidebar();
                    break;

                case "runtime_start":
                    _runtime.Start();
                    break;

                case "runtime_stop":
                    _runtime.Stop();
                    break;

                case "reset":
                    _runtime.Stop("Сброс симулятора");
                    if (_hub is SimulatorDataChannelHub simulatorHub)
                    {
                        simulatorHub.Reset();
                    }
                    break;

                default:
                    return;
            }

            PushSnapshot();
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
        _hub.Get<InventoryState>("inventory").Set(new InventoryState(items), "Редактор инвентаря");
    }

    private void SetReputation(JsonElement root)
    {
        var key = Required(root, "key");
        var amount = (int)Number(root, "amount", 0);
        var state = _hub.Get<ReputationState>("reputation").Value;
        var values = new Dictionary<string, int>(state.Values, StringComparer.OrdinalIgnoreCase)
        {
            [key] = amount
        };
        _hub.Get<ReputationState>("reputation").Set(new ReputationState(values), "Редактор репутации");
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

        _journalForm?.SetEntries(_journalEntries);
    }

    private void QuestGraph_Changed(object? sender, EventArgs e)
    {
        AppLogger.Info("SimulatorForm: Quest Graph изменён.", $"nodes={_questGraph.Value.Nodes.Count}; connections={_questGraph.Value.Connections.Count}");
        LogQuestGraph("graph_changed");

        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)PushSnapshot);
        }
        catch (InvalidOperationException)
        {
        }
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
            var node = _questGraph.FindNode(e.NodeId);
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
                PushSnapshot();
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
                PushSnapshot();
            }));
        }
        catch (InvalidOperationException)
        {
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

    private void LogQuestSnapshot(string stage)
    {
        var state = _runtime.State;
        var graph = _questGraph.Value;
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
