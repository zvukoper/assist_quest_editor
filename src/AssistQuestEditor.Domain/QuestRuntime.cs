namespace AssistQuestEditor.Domain;

public enum QuestRuntimeStatus
{
    Stopped,
    Running,
    Waiting,
    Completed,
    Failed
}

public sealed record QuestRuntimeState(
    string QuestId,
    string? CurrentNodeId,
    QuestRuntimeStatus Status,
    string? WaitingFor,
    string LastEvent,
    string LastTransition);

public sealed record QuestRuntimeEvent(
    string EventType,
    DateTimeOffset Timestamp,
    string Source,
    string? NodeId,
    string Message);

public sealed class QuestRuntime
{
    private const int MaxTransitionsPerPass = 64;

    private readonly QuestGraphStore _graphStore;
    private readonly IDataChannelHub _hub;
    private string? _waitingEventType;
    private DateTimeOffset? _waitUntil;

    public QuestRuntime(QuestGraphStore graphStore, IDataChannelHub hub)
    {
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        State = new QuestRuntimeState(
            graphStore.Value.Id,
            null,
            QuestRuntimeStatus.Stopped,
            null,
            string.Empty,
            string.Empty);

        _graphStore.Changed += GraphStore_Changed;
        _hub.Events.Published += Events_Published;

        PublishSystem("Runtime создан");
    }

    public QuestRuntimeState State { get; private set; }
    public event EventHandler<QuestRuntimeEvent>? Published;

    public void Start()
    {
        ResetWaiting();
        State = State with
        {
            QuestId = _graphStore.Value.Id,
            Status = QuestRuntimeStatus.Running,
            CurrentNodeId = FindStartNodeId(),
            WaitingFor = null,
            LastEvent = "RuntimeStarted",
            LastTransition = "Запуск квеста"
        };

        SetQuestStatus(QuestStatus.Active, GetQuestStep());
        Publish("RuntimeStarted", "QuestRuntime", State.CurrentNodeId, "Runtime запущен.");
        PublishSystem("Runtime запущен");
        Advance();
    }

    public void Stop(string reason = "Runtime остановлен")
    {
        ResetWaiting();
        State = State with
        {
            Status = QuestRuntimeStatus.Stopped,
            WaitingFor = null,
            LastEvent = "RuntimeStopped",
            LastTransition = reason
        };
        Publish("RuntimeStopped", "QuestRuntime", State.CurrentNodeId, reason);
        PublishSystem(reason);
    }

    public void Tick()
    {
        if (State.Status != QuestRuntimeStatus.Waiting)
        {
            return;
        }

        if (_waitUntil is not null && DateTimeOffset.UtcNow < _waitUntil.Value)
        {
            return;
        }

        if (string.Equals(State.WaitingFor, "Time", StringComparison.OrdinalIgnoreCase))
        {
            ResetWaiting();
            State = State with
            {
                Status = QuestRuntimeStatus.Running,
                WaitingFor = null,
                LastEvent = "WaitCompleted"
            };
            Advance();
            return;
        }

        if (string.Equals(State.WaitingFor, "Condition", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(State.WaitingFor, "Interaction", StringComparison.OrdinalIgnoreCase))
        {
            EvaluateWaitingCondition();
        }
    }

    public void HandleEvent(SimulatorEvent value)
    {
        if (State.Status != QuestRuntimeStatus.Waiting)
        {
            return;
        }

        if (string.Equals(State.WaitingFor, "Event:" + value.EventType, StringComparison.OrdinalIgnoreCase))
        {
            ResetWaiting();
            State = State with
            {
                Status = QuestRuntimeStatus.Running,
                WaitingFor = null,
                LastEvent = value.EventType
            };
            Publish("RuntimeEventMatched", value.Source, State.CurrentNodeId, $"Событие {value.EventType} принято.");
            Advance();
            return;
        }

        if (string.Equals(State.WaitingFor, "Choice", StringComparison.OrdinalIgnoreCase) &&
            value.EventType.Equals("ChoiceSelected", StringComparison.OrdinalIgnoreCase))
        {
            var index = ParseInt(value.Payload, "index", 1);
            var node = FindCurrentNode();
            var output = node?.Sockets
                .Where(socket => socket.Direction == SocketDirection.Output)
                .ElementAtOrDefault(Math.Max(0, index - 1));

            if (output is null)
            {
                Fail($"Для Choice нет выхода {index}.");
                return;
            }

            ResetWaiting();
            State = State with
            {
                Status = QuestRuntimeStatus.Running,
                WaitingFor = null,
                LastEvent = value.EventType
            };
            MoveThroughOutput(node!, output.SocketId);
        }
    }

    private void GraphStore_Changed(object? sender, EventArgs e)
    {
        if (State.Status is QuestRuntimeStatus.Running or QuestRuntimeStatus.Waiting)
        {
            Stop("Граф изменён: Runtime остановлен.");
        }
        else
        {
            State = State with { QuestId = _graphStore.Value.Id };
        }
    }

    private void Events_Published(SimulatorEvent value) => HandleEvent(value);

    private void Advance()
    {
        for (var transitions = 0; transitions < MaxTransitionsPerPass; transitions++)
        {
            if (State.Status != QuestRuntimeStatus.Running || State.CurrentNodeId is null)
            {
                return;
            }

            var node = FindCurrentNode();
            if (node is null)
            {
                Fail("Текущая нода не найдена.");
                return;
            }

            Publish("NodeEntered", "QuestRuntime", node.NodeId, $"Вход в {node.NodeType}.");

            switch (node.NodeType.ToLowerInvariant())
            {
                case "start":
                    MoveToFirstOutput(node);
                    break;

                case "end":
                    Complete();
                    return;

                case "condition":
                    EvaluateCondition(node);
                    break;

                case "interaction":
                    if (EvaluateInteraction(node))
                    {
                        MoveToFirstOutput(node);
                    }
                    else
                    {
                        Wait("Interaction");
                    }
                    break;

                case "wait":
                    WaitTime(node);
                    return;

                case "waitforcondition":
                    if (EvaluateConditionReference(node))
                    {
                        MoveToFirstOutput(node);
                    }
                    else
                    {
                        Wait("Condition");
                        return;
                    }
                    break;

                case "waitforevent":
                    Wait("Event:" + GetParameter(node, "eventType"));
                    return;

                case "choice":
                    Wait("Choice");
                    return;

                case "setstatus":
                    ApplySetStatus(node);
                    MoveToFirstOutput(node);
                    break;

                case "setstep":
                    ApplySetStep(node);
                    MoveToFirstOutput(node);
                    break;

                case "setflag":
                    ApplySetFlag(node);
                    MoveToFirstOutput(node);
                    break;

                case "setvariable":
                    ApplySetVariable(node);
                    MoveToFirstOutput(node);
                    break;

                case "dialoguescene":
                    ApplyDialogue(node);
                    MoveToFirstOutput(node);
                    break;

                case "giveitem":
                    ApplyGiveItem(node, +1);
                    MoveToFirstOutput(node);
                    break;

                case "removeitem":
                    ApplyGiveItem(node, -1);
                    MoveToFirstOutput(node);
                    break;

                case "addreputation":
                    ApplyReputation(node, +1);
                    MoveToFirstOutput(node);
                    break;

                case "removereputation":
                    ApplyReputation(node, -1);
                    MoveToFirstOutput(node);
                    break;

                case "reward":
                case "phase":
                case "and":
                case "or":
                case "not":
                case "switch":
                case "random":
                    MoveToFirstOutput(node);
                    break;

                default:
                    Fail($"Нет runtime handler для NodeType «{node.NodeType}».");
                    return;
            }
        }

        Fail($"Слишком много переходов подряд (>{MaxTransitionsPerPass}). Возможен цикл без ожидания.");
    }

    private void EvaluateCondition(QuestNode node)
    {
        var result = EvaluateConditionParameters(node.Parameters);
        var socketId = result ? $"{node.NodeId}.true" : $"{node.NodeId}.false";
        MoveThroughOutput(node, socketId);
    }

    private bool EvaluateConditionParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var op = GetParameter(parameters, "operator", "True");
        return op.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            "queststepis" => Compare(
                GetQuestStep(),
                GetParameter(parameters, "right"),
                GetParameter(parameters, "comparison", "==")),
            "itemcountcompare" => Compare(
                GetItemCount(GetParameter(parameters, "left")),
                ParseInt(GetParameter(parameters, "right"), 0),
                GetParameter(parameters, "comparison", "==")),
            "factequals" => Compare(
                GetFact(GetParameter(parameters, "left")),
                GetParameter(parameters, "right"),
                GetParameter(parameters, "comparison", "==")),
            "flagequals" => Compare(
                GetFlag(GetParameter(parameters, "left")).ToString(),
                GetParameter(parameters, "right"),
                GetParameter(parameters, "comparison", "==")),
            "distancecompare" => EvaluateDistance(parameters),
            _ => false
        };
    }

    private bool EvaluateDistance(IReadOnlyDictionary<string, string> parameters)
    {
        var pointId = GetParameter(parameters, "worldPointId", GetParameter(parameters, "right"));
        var radius = ParseDouble(GetParameter(parameters, "triggerRadius"), 35);
        var point = _hub.Get<WorldState>("world").Value.Points
            .FirstOrDefault(x => x.Id.Equals(pointId, StringComparison.OrdinalIgnoreCase));
        if (point is null)
        {
            return false;
        }

        var player = _hub.Get<PlayerState>("player").Value.Position;
        var dx = player.X - point.Position.X;
        var dy = player.Y - point.Position.Y;
        var dz = player.Z - point.Position.Z;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return Compare(distance, radius, GetParameter(parameters, "comparison", "<="));
    }

    private bool EvaluateInteraction(QuestNode node)
    {
        var parameters = node.Parameters;
        var pointId = GetParameter(parameters, "worldPointId");
        var radius = ParseDouble(GetParameter(parameters, "triggerRadius"), 35);
        if (string.IsNullOrWhiteSpace(pointId))
        {
            return false;
        }

        var point = _hub.Get<WorldState>("world").Value.Points
            .FirstOrDefault(x => x.Id.Equals(pointId, StringComparison.OrdinalIgnoreCase));
        if (point is null)
        {
            return false;
        }

        var player = _hub.Get<PlayerState>("player").Value.Position;
        var dx = player.X - point.Position.X;
        var dy = player.Y - point.Position.Y;
        var dz = player.Z - point.Position.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= radius;
    }

    private bool EvaluateConditionReference(QuestNode node)
    {
        return EvaluateConditionParameters(node.Parameters);
    }

    private void EvaluateWaitingCondition()
    {
        var node = FindCurrentNode();
        if (node is null)
        {
            Fail("Ожидающая нода не найдена.");
            return;
        }

        var result = node.NodeType.Equals("Interaction", StringComparison.OrdinalIgnoreCase)
            ? EvaluateInteraction(node)
            : EvaluateConditionReference(node);

        if (!result)
        {
            return;
        }

        ResetWaiting();
        State = State with { Status = QuestRuntimeStatus.Running, WaitingFor = null, LastEvent = "WaitingConditionMet" };
        MoveToFirstOutput(node);
    }

    private void MoveToFirstOutput(QuestNode node)
    {
        var socket = node.Sockets.FirstOrDefault(x => x.Direction == SocketDirection.Output);
        if (socket is null)
        {
            Fail($"У ноды «{node.NodeId}» нет Output.");
            return;
        }

        MoveThroughOutput(node, socket.SocketId);
    }

    private void MoveThroughOutput(QuestNode node, string socketId)
    {
        var connection = _graphStore.Value.Connections.FirstOrDefault(x =>
            x.FromNodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase) &&
            x.FromSocketId.Equals(socketId, StringComparison.OrdinalIgnoreCase));

        if (connection is null)
        {
            State = State with
            {
                Status = QuestRuntimeStatus.Waiting,
                WaitingFor = "Unconnected:" + socketId,
                LastTransition = $"{node.NodeId}:{socketId} без связи"
            };
            Publish("RuntimeWaiting", "QuestRuntime", node.NodeId, State.LastTransition);
            PublishSystem(State.LastTransition);
            return;
        }

        State = State with
        {
            CurrentNodeId = connection.ToNodeId,
            Status = QuestRuntimeStatus.Running,
            LastTransition = $"{node.NodeId}:{socketId} → {connection.ToNodeId}:{connection.ToSocketId}"
        };
        Publish("NodeTransition", "QuestRuntime", connection.ToNodeId, State.LastTransition);
    }

    private void Wait(string waitingFor)
    {
        State = State with
        {
            Status = QuestRuntimeStatus.Waiting,
            WaitingFor = waitingFor,
            LastEvent = "RuntimeWaiting",
            LastTransition = $"Ожидание: {waitingFor}"
        };
        Publish("RuntimeWaiting", "QuestRuntime", State.CurrentNodeId, State.LastTransition);
        PublishSystem(State.LastTransition);
    }

    private void WaitTime(QuestNode node)
    {
        var seconds = ParseDouble(GetParameter(node, "seconds"), 1);
        if (seconds <= 0)
        {
            MoveToFirstOutput(node);
            return;
        }

        _waitUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        Wait("Time");
    }

    private void Complete()
    {
        ResetWaiting();
        SetQuestStatus(QuestStatus.Completed, GetQuestStep());
        State = State with
        {
            Status = QuestRuntimeStatus.Completed,
            WaitingFor = null,
            LastEvent = "QuestCompleted",
            LastTransition = "Достигнута End."
        };
        Publish("QuestCompleted", "QuestRuntime", State.CurrentNodeId, "Квест завершён.");
        PublishSystem("Квест завершён");
    }

    private void Fail(string message)
    {
        ResetWaiting();
        SetQuestStatus(QuestStatus.Failed, GetQuestStep());
        State = State with
        {
            Status = QuestRuntimeStatus.Failed,
            WaitingFor = null,
            LastEvent = "RuntimeFailed",
            LastTransition = message
        };
        Publish("RuntimeFailed", "QuestRuntime", State.CurrentNodeId, message);
        PublishSystem(message);
    }

    private void ApplySetStatus(QuestNode node)
    {
        var text = GetParameter(node, "status", "Active");
        if (!Enum.TryParse<QuestStatus>(text, true, out var status))
        {
            Fail($"Неизвестный QuestStatus «{text}».");
            return;
        }

        SetQuestStatus(status, GetQuestStep());
    }

    private void ApplySetStep(QuestNode node)
    {
        var step = GetParameter(node, "step");
        SetQuestStatus(GetQuestStatus(), string.IsNullOrWhiteSpace(step) ? GetQuestStep() : step);
    }

    private void ApplySetFlag(QuestNode node)
    {
        var key = GetParameter(node, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var current = _hub.Get<RuntimeStatesState>("states").Value;
        var flags = new Dictionary<string, bool>(current.Flags, StringComparer.OrdinalIgnoreCase)
        {
            [key] = bool.TryParse(GetParameter(node, "value", "true"), out var value) && value
        };
        _hub.Get<RuntimeStatesState>("states").Set(
            current with { Flags = flags },
            "QuestRuntime");
    }

    private void ApplySetVariable(QuestNode node)
    {
        var key = GetParameter(node, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var current = _hub.Get<RuntimeStatesState>("states").Value;
        var variables = new Dictionary<string, string>(current.Variables, StringComparer.OrdinalIgnoreCase)
        {
            [key] = GetParameter(node, "value")
        };
        _hub.Get<RuntimeStatesState>("states").Set(
            current with { Variables = variables },
            "QuestRuntime");
    }

    private void ApplyDialogue(QuestNode node)
    {
        var sceneId = GetParameter(node, "sceneId");
        var state = _hub.Get<RuntimeStatesState>("states").Value;
        _hub.Get<RuntimeStatesState>("states").Set(
            state with
            {
                DialogueId = sceneId,
                DialogueAnchor = node.NodeId
            },
            "QuestRuntime");
    }

    private void ApplyGiveItem(QuestNode node, int sign)
    {
        var itemId = GetParameter(node, "itemId");
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var amount = Math.Max(0, ParseInt(GetParameter(node, "count"), 1)) * sign;
        var state = _hub.Get<InventoryState>("inventory").Value;
        var items = new Dictionary<string, int>(state.Items, StringComparer.OrdinalIgnoreCase)
        {
            [itemId] = Math.Max(0, GetItemCount(itemId) + amount)
        };
        _hub.Get<InventoryState>("inventory").Set(
            new InventoryState(items),
            "QuestRuntime");
    }

    private void ApplyReputation(QuestNode node, int sign)
    {
        var faction = GetParameter(node, "faction");
        if (string.IsNullOrWhiteSpace(faction))
        {
            return;
        }

        var amount = ParseInt(GetParameter(node, "amount"), 1) * sign;
        var state = _hub.Get<ReputationState>("reputation").Value;
        var values = new Dictionary<string, int>(state.Values, StringComparer.OrdinalIgnoreCase)
        {
            [faction] = GetReputation(faction) + amount
        };
        _hub.Get<ReputationState>("reputation").Set(
            new ReputationState(values),
            "QuestRuntime");
    }

    private void SetQuestStatus(QuestStatus status, string step)
    {
        var questId = _graphStore.Value.Id;
        var state = _hub.Get<QuestStatusesState>("quest-statuses").Value;
        var list = state.Quests
            .Where(x => !x.QuestId.Equals(questId, StringComparison.OrdinalIgnoreCase))
            .Append(new QuestStatusEntry(questId, status, step))
            .ToArray();

        _hub.Get<QuestStatusesState>("quest-statuses").Set(
            new QuestStatusesState(list),
            "QuestRuntime");
    }

    private QuestStatus GetQuestStatus()
    {
        return _hub.Get<QuestStatusesState>("quest-statuses").Value.Quests
            .FirstOrDefault(x => x.QuestId.Equals(_graphStore.Value.Id, StringComparison.OrdinalIgnoreCase))
            ?.Status ?? QuestStatus.Available;
    }

    private string GetQuestStep()
    {
        return _hub.Get<QuestStatusesState>("quest-statuses").Value.Quests
            .FirstOrDefault(x => x.QuestId.Equals(_graphStore.Value.Id, StringComparison.OrdinalIgnoreCase))
            ?.Step ?? "available";
    }

    private string GetFact(string key) =>
        _hub.Get<FactState>("facts").Value.Values.TryGetValue(key, out var value) ? value : string.Empty;

    private bool GetFlag(string key) =>
        _hub.Get<RuntimeStatesState>("states").Value.Flags.TryGetValue(key, out var value) && value;

    private int GetItemCount(string key) =>
        _hub.Get<InventoryState>("inventory").Value.Items.TryGetValue(key, out var value) ? value : 0;

    private int GetReputation(string key) =>
        _hub.Get<ReputationState>("reputation").Value.Values.TryGetValue(key, out var value) ? value : 0;

    private QuestNode? FindCurrentNode() =>
        State.CurrentNodeId is null ? null : _graphStore.FindNode(State.CurrentNodeId);

    private string? FindStartNodeId() =>
        _graphStore.Value.Nodes.FirstOrDefault(x => x.NodeType.Equals("Start", StringComparison.OrdinalIgnoreCase))?.NodeId;

    private static string GetParameter(QuestNode node, string key, string fallback = "") =>
        GetParameter(node.Parameters, key, fallback);

    private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string key, string fallback = "") =>
        parameters.TryGetValue(key, out var value) ? value : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> payload, string key, int fallback) =>
        payload.TryGetValue(key, out var value) ? ParseInt(value, fallback) : fallback;

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool Compare(string left, string right, string comparison) =>
        comparison switch
        {
            "==" => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool Compare(int left, int right, string comparison) =>
        comparison switch
        {
            "==" => left == right,
            "!=" => left != right,
            ">" => left > right,
            ">=" => left >= right,
            "<" => left < right,
            "<=" => left <= right,
            _ => false
        };

    private static bool Compare(double left, double right, string comparison) =>
        comparison switch
        {
            "==" => Math.Abs(left - right) < 0.001,
            "!=" => Math.Abs(left - right) >= 0.001,
            ">" => left > right,
            ">=" => left >= right,
            "<" => left < right,
            "<=" => left <= right,
            _ => false
        };

    private void ResetWaiting()
    {
        _waitingEventType = null;
        _waitUntil = null;
    }

    private void Publish(string eventType, string source, string? nodeId, string message)
    {
        State = State with { LastEvent = eventType, LastTransition = message };
        Published?.Invoke(
            this,
            new QuestRuntimeEvent(
                eventType,
                DateTimeOffset.UtcNow,
                source,
                nodeId,
                message));
    }

    private void PublishSystem(string transition)
    {
        if (State.Status == QuestRuntimeStatus.Stopped)
        {
            _hub.Get<SystemState>("system").Set(
                _hub.Get<SystemState>("system").Value with
                {
                    RuntimeRunning = false,
                    RuntimeMode = "Квестовый Runtime",
                    LastEvent = State.LastEvent,
                    LastTransition = transition
                },
                "QuestRuntime");
            return;
        }

        _hub.Get<SystemState>("system").Set(
            _hub.Get<SystemState>("system").Value with
            {
                RuntimeRunning = State.Status is QuestRuntimeStatus.Running or QuestRuntimeStatus.Waiting,
                RuntimeMode = "Квестовый Runtime",
                LastEvent = State.LastEvent,
                LastTransition = transition
            },
            "QuestRuntime");
    }
}
