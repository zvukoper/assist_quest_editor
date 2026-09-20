using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class SimulatorForm : WebViewForm
{
    private readonly IDataChannelHub _hub;

    public SimulatorForm(IDataChannelHub hub)
        : base(
            "Assist Quest Editor — Симулятор",
            "simulator.html",
            new Size(1440, 900))
    {
        _hub = hub;
        if (_hub.Events is EventChannel<SimulatorEvent> events)
        {
            events.Published += Events_Published;
        }

        FormClosed += (_, _) =>
        {
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

        var payload = JsonSerializer.Serialize(new
        {
            type = "snapshot",
            version = VersionInfo.InformationalVersion,
            snapshot
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

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

                case "reset":
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

    private void Events_Published(SimulatorEvent e)
    {
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
