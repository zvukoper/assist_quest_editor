namespace AssistQuestEditor.Domain;

public sealed class SceneRuntime
{
    private const int MaxTransitionsPerPass = 64;

    private readonly ISceneCatalog _catalog;
    private readonly IDataChannelHub _hub;

    public SceneRuntime(ISceneCatalog catalog, IDataChannelHub hub)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _hub.Events.Published += HandleEvent;
        State = new SceneRuntimeState(
            null,
            null,
            SceneRuntimeStatus.Stopped,
            null,
            null,
            string.Empty,
            string.Empty);
    }

    public SceneRuntimeState State { get; private set; }
    public event EventHandler<SceneRuntimeEvent>? Published;

    public bool Start(string sceneId)
    {
        Stop("SceneRuntime подготовлен к новому запуску.");

        if (!_catalog.TryGetScene(sceneId, out var scene))
        {
            Fail(null, $"Scene «{sceneId}» не найдена в canonical Scene catalog.");
            return false;
        }

        var startNodeId = scene.Graph.Nodes
            .FirstOrDefault(node => node.NodeType.Equals("SceneStart", StringComparison.OrdinalIgnoreCase))
            ?.NodeId;

        if (startNodeId is null)
        {
            Fail(sceneId, "В Scene Graph отсутствует SceneStart.");
            return false;
        }

        State = State with
        {
            SceneId = sceneId,
            CurrentNodeId = startNodeId,
            Status = SceneRuntimeStatus.Running,
            WaitingFor = null,
            LastChoiceId = null,
            LastEvent = "SceneStarted",
            LastTransition = "Сцена запущена."
        };

        Publish("SceneStarted", sceneId, startNodeId, null, "SceneRuntime запущен.");
        Advance();
        return State.Status is SceneRuntimeStatus.Running or SceneRuntimeStatus.Waiting or SceneRuntimeStatus.Completed;
    }

    public void Stop(string reason = "SceneRuntime остановлен.")
    {
        var hadState = State.Status is SceneRuntimeStatus.Running or SceneRuntimeStatus.Waiting;
        ClearInterface();

        if (!hadState)
        {
            return;
        }

        var sceneId = State.SceneId ?? string.Empty;
        State = State with
        {
            Status = SceneRuntimeStatus.Stopped,
            WaitingFor = null,
            LastEvent = "SceneStopped",
            LastTransition = reason
        };

        Publish("SceneStopped", sceneId, State.CurrentNodeId, State.LastChoiceId, reason);
    }

    public void HandleEvent(SimulatorEvent value)
    {
        if (State.Status != SceneRuntimeStatus.Waiting)
            return;

        if (string.Equals(State.WaitingFor, "Dialogue", StringComparison.OrdinalIgnoreCase) &&
            value.EventType.Equals("DialogueContinue", StringComparison.OrdinalIgnoreCase))
        {
            ContinueDialogue(value);
            return;
        }

        if (!string.Equals(State.WaitingFor, "Choice", StringComparison.OrdinalIgnoreCase) ||
            !value.EventType.Equals("ChoiceSelected", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (State.SceneId is null)
        {
            Fail(null, "Choice получен без активной сцены.");
            return;
        }

        if (!_catalog.TryGetScene(State.SceneId, out var scene))
        {
            Fail(State.SceneId, "Активная Scene отсутствует в catalog.");
            return;
        }

        var node = FindCurrentNode(scene);
        if (node is null)
        {
            Fail(State.SceneId, "Текущая Choice node отсутствует в Scene Graph.");
            return;
        }

        var choiceId = GetParameter(node, "choiceId");
        var choice = scene.Choices.FirstOrDefault(
            item => item.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase));

        if (choice is null)
        {
            Fail(State.SceneId, $"Choice resource «{choiceId}» не найден.");
            return;
        }

        var requestId = value.Payload.TryGetValue("requestId", out var requestedId)
            ? requestedId
            : string.Empty;
        var activeDialog = _hub.Get<InterfaceState>("interfaces").Value.ActiveDialog;

        if (activeDialog is null ||
            !activeDialog.RequestId.Equals(requestId, StringComparison.OrdinalIgnoreCase))
        {
            Publish(
                "SceneInterfaceIgnored",
                State.SceneId,
                node.NodeId,
                choice.Id,
                $"Устаревший requestId «{requestId}» проигнорирован.");
            return;
        }

        var index = ParseInt(value.Payload, "index", 0);
        var option = value.Payload.TryGetValue("optionId", out var optionId)
            ? choice.Options.FirstOrDefault(item =>
                item.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))
            : null;

        // Индекс приходит от UI и считается по показанному списку, а не по
        // полному: недоступные по репутации варианты в интерфейс не попадают.
        // Искать по choice.Options[index-1] нельзя — сместилось бы на количество
        // скрытых вариантов.
        var visibleOptions = VisibleOptions(choice);

        if (option is null && index > 0 && index <= visibleOptions.Count)
        {
            option = visibleOptions[index - 1];
        }

        if (option is null)
        {
            Fail(State.SceneId, $"Для Choice «{choice.Id}» не найден выбранный option.");
            return;
        }

        // Скрытый вариант недоступен даже при прямом указании optionId: это
        // защищает от обхода требования репутации через событие.
        if (!visibleOptions.Any(item =>
                item.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Fail(
                State.SceneId,
                $"Option «{option.Id}» недоступен: не выполняются требования репутации.");
            return;
        }

        if (value.Payload.TryGetValue("optionId", out var payloadOptionId) &&
            !option.Id.Equals(payloadOptionId, StringComparison.OrdinalIgnoreCase))
        {
            Fail(State.SceneId, "optionId события Choice не совпадает с canonical option.");
            return;
        }

        ClearInterface();
        State = State with
        {
            Status = SceneRuntimeStatus.Running,
            WaitingFor = null,
            LastChoiceId = option.Id,
            LastEvent = "ChoiceSelected",
            LastTransition = $"Выбран option «{option.Id}»."
        };

        MoveThroughOutput(scene, node, option.OutputSocketId);
        Advance();
    }

    private void Advance()
    {
        if (State.SceneId is null)
        {
            Fail(null, "SceneRuntime не имеет SceneId.");
            return;
        }

        if (!_catalog.TryGetScene(State.SceneId, out var scene))
        {
            Fail(State.SceneId, "Scene resource исчез из catalog.");
            return;
        }

        for (var transitions = 0; transitions < MaxTransitionsPerPass; transitions++)
        {
            if (State.Status != SceneRuntimeStatus.Running || State.CurrentNodeId is null)
            {
                return;
            }

            var node = FindCurrentNode(scene);
            if (node is null)
            {
                Fail(State.SceneId, "Текущая Scene node не найдена.");
                return;
            }

            Publish("SceneNodeEntered", State.SceneId, node.NodeId, State.LastChoiceId, $"Вход в {node.NodeType}.");

            switch (node.NodeType.ToLowerInvariant())
            {
                case "scenestart":
                    MoveToFirstOutput(scene, node);
                    break;

                case "dialogue":
                    if (!PresentDialogue(scene, node))
                        return;
                    Wait("Dialogue");
                    return;

                case "choice":
                    if (!OpenChoiceDialog(scene, node))
                    {
                        return;
                    }

                    Wait("Choice");
                    return;

                case "sceneend":
                    Complete();
                    return;

                default:
                    Fail(State.SceneId, $"Нет SceneRuntime handler для NodeType «{node.NodeType}».");
                    return;
            }
        }

        Fail(State.SceneId, $"Слишком много переходов подряд (>{MaxTransitionsPerPass}). Возможен цикл в Scene Graph.");
    }

    private void ContinueDialogue(SimulatorEvent value)
    {
        if (State.SceneId is null)
        {
            Fail(null, "Продолжение диалога получено без активной сцены.");
            return;
        }

        if (!_catalog.TryGetScene(State.SceneId, out var scene))
        {
            Fail(State.SceneId, "Активная Scene отсутствует в catalog.");
            return;
        }

        var node = FindCurrentNode(scene);
        if (node is null)
        {
            Fail(State.SceneId, "Текущая Dialogue node отсутствует в Scene Graph.");
            return;
        }

        var dialogueId = GetParameter(node, "dialogueId");
        var dialogue = scene.Dialogues.FirstOrDefault(
            item => item.Id.Equals(dialogueId, StringComparison.OrdinalIgnoreCase));

        if (dialogue is null)
        {
            Fail(State.SceneId, $"Dialogue resource «{dialogueId}» не найден.");
            return;
        }

        var requestId = value.Payload.TryGetValue("requestId", out var requestedId)
            ? requestedId
            : string.Empty;
        var activeDialogue = _hub.Get<InterfaceState>("interfaces").Value.ActiveDialogue;

        if (activeDialogue is null ||
            !activeDialogue.RequestId.Equals(requestId, StringComparison.OrdinalIgnoreCase))
        {
            Publish(
                "SceneDialogueIgnored",
                State.SceneId,
                node.NodeId,
                null,
                $"Устаревший requestId диалога «{requestId}» проигнорирован.");
            return;
        }

        ClearInterface();
        var states = _hub.Get<RuntimeStatesState>("states").Value;
        _hub.Get<RuntimeStatesState>("states").Set(
            states with
            {
                DialogueId = null,
                DialogueAnchor = null
            },
            "SceneRuntime");

        State = State with
        {
            Status = SceneRuntimeStatus.Running,
            WaitingFor = null,
            LastEvent = "DialogueContinue",
            LastTransition = $"Диалог «{dialogue.Id}» завершён."
        };

        Publish(
            "SceneDialogueCompleted",
            State.SceneId,
            node.NodeId,
            null,
            $"Диалог «{dialogue.Id}» завершён.");

        MoveToFirstOutput(scene, node);
        Advance();
    }

    private bool OpenChoiceDialog(SceneDefinition scene, SceneNode node)
    {
        var choiceId = GetParameter(node, "choiceId");
        var choice = scene.Choices.FirstOrDefault(
            item => item.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase));

        if (choice is null)
        {
            Fail(scene.Id, $"Choice resource «{choiceId}» не найден.");
            return false;
        }

        if (choice.Options.Count == 0)
        {
            Fail(scene.Id, $"Choice «{choice.Id}» не содержит options.");
            return false;
        }

        var dialogRequest = new InterfaceChoiceDialog(
            $"{scene.Id}:{node.NodeId}:{Guid.NewGuid():N}",
            choice.Title,
            choice.Speaker,
            choice.Text,
            VisibleOptions(choice)
                .Select(option => new InterfaceChoiceOption(option.Id, option.Text))
                .ToArray());

        // Conversation content — единственное место, где видно, с кем игрок
        // говорит. Выбор НПЦ тоже считается контактом: он озвучивает реплику.
        NoteNpcContact(choice.Speaker);

        _hub.Get<InterfaceState>("interfaces").Set(
            new InterfaceState(dialogRequest),
            "SceneRuntime");

        Publish(
            "SceneInterfaceRequested",
            scene.Id,
            node.NodeId,
            choice.Id,
            $"Открыт canonical Choice «{choice.Id}».");

        return true;
    }

    private bool PresentDialogue(SceneDefinition scene, SceneNode node)
    {
        var dialogueId = GetParameter(node, "dialogueId");
        var dialogue = scene.Dialogues.FirstOrDefault(
            item => item.Id.Equals(dialogueId, StringComparison.OrdinalIgnoreCase));

        if (dialogue is null)
        {
            Fail(scene.Id, $"Dialogue resource «{dialogueId}» не найден.");
            return false;
        }

        var states = _hub.Get<RuntimeStatesState>("states").Value;
        _hub.Get<RuntimeStatesState>("states").Set(
            states with
            {
                DialogueId = dialogue.Id,
                DialogueAnchor = $"{scene.Id}:{node.NodeId}"
            },
            "SceneRuntime");

        var dialogRequest = new InterfaceDialogue(
            $"{scene.Id}:{node.NodeId}:{Guid.NewGuid():N}",
            node.Title,
            dialogue.Speaker,
            dialogue.Text);

        NoteNpcContact(dialogue.Speaker);

        _hub.Get<InterfaceState>("interfaces").Set(
            new InterfaceState(null, dialogRequest),
            "SceneRuntime");

        Publish(
            "SceneDialoguePresented",
            scene.Id,
            node.NodeId,
            null,
            $"Представлен dialogue «{dialogue.Id}».");

        return true;
    }

    /// <summary>
    /// Фиксирует контакт с НПЦ по его Speaker.
    ///
    /// Speaker — отображаемое имя («Руслан»), а репутация ведётся по стабильному
    /// Id («ruslan»), поэтому сопоставление идёт через каталог НПЦ. Неизвестный
    /// Speaker молча игнорируется: он может быть служебной репликой рассказчика,
    /// и такая реплика не должна создавать «НПЦ» в списке репутации.
    /// </summary>
    private void NoteNpcContact(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return;

        var npc = NpcCatalogFactory.CreateStarter()
            .FirstOrDefault(item => item.MatchesSpeaker(speaker));

        if (npc is null)
            return;

        var channel = _hub.Get<ReputationState>("reputation");
        var next = channel.Value.WithContact(npc.Id);

        if (ReferenceEquals(next, channel.Value))
            return;

        channel.Set(next, "SceneRuntime");
        Publish(
            "NpcContacted",
            State.SceneId ?? string.Empty,
            State.CurrentNodeId,
            null,
            "Состоялся контакт с «" + npc.Name + "».");
    }

    /// <summary>
    /// Варианты выбора, доступные игроку при текущей репутации.
    ///
    /// Если после фильтрации не осталось ни одного варианта, возвращаются все:
    /// Choice без вариантов сделал бы сцену непроходимой, а это хуже, чем
    /// показать вариант вопреки требованию. Причина попадает в журнал.
    /// </summary>
    private IReadOnlyList<SceneChoiceOption> VisibleOptions(SceneChoice choice)
    {
        var visible = choice.Options
            .Where(option => MeetsRequirement(option.Requirement))
            .ToArray();

        if (visible.Length > 0)
            return visible;

        Publish(
            "SceneChoiceRequirementsUnsatisfied",
            State.SceneId ?? string.Empty,
            State.CurrentNodeId,
            choice.Id,
            $"Все варианты Choice «{choice.Id}» скрыты требованиями; показаны все, чтобы сцена осталась проходимой.");

        return choice.Options;
    }

    /// <summary>
    /// Проверяет требование репутации. Отсутствие требования означает «доступно».
    /// </summary>
    private bool MeetsRequirement(ReputationRequirement? requirement)
    {
        if (requirement is null)
            return true;

        if (string.IsNullOrWhiteSpace(requirement.NpcId))
            return true;

        var value = _hub.Get<ReputationState>("reputation").Value.ValueOf(requirement.NpcId);
        return value >= requirement.MinValue;
    }

    private void Complete()
    {
        ClearInterface();
        var sceneId = State.SceneId ?? string.Empty;
        State = State with
        {
            Status = SceneRuntimeStatus.Completed,
            WaitingFor = null,
            LastEvent = "SceneCompleted",
            LastTransition = "SceneEnd достигнут."
        };

        Publish(
            "SceneCompleted",
            sceneId,
            State.CurrentNodeId,
            State.LastChoiceId,
            "Сцена завершена.");
    }

    private void Fail(string? sceneId, string message)
    {
        ClearInterface();
        State = State with
        {
            SceneId = sceneId ?? State.SceneId,
            Status = SceneRuntimeStatus.Failed,
            WaitingFor = null,
            LastEvent = "SceneFailed",
            LastTransition = message
        };

        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            Publish("SceneFailed", sceneId!, State.CurrentNodeId, State.LastChoiceId, message);
        }
    }

    private void MoveToFirstOutput(SceneDefinition scene, SceneNode node)
    {
        var socket = node.Sockets.FirstOrDefault(socket => socket.Direction == SocketDirection.Output);
        if (socket is null)
        {
            Fail(scene.Id, $"У Scene node «{node.NodeId}» нет Output.");
            return;
        }

        MoveThroughOutput(scene, node, socket.SocketId);
    }

    private void MoveThroughOutput(SceneDefinition scene, SceneNode node, string socketId)
    {
        var connection = scene.Graph.Connections.FirstOrDefault(item =>
            item.FromNodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase) &&
            item.FromSocketId.Equals(socketId, StringComparison.OrdinalIgnoreCase));

        if (connection is null)
        {
            Fail(scene.Id, $"Output «{socketId}» у Scene node «{node.NodeId}» не подключён.");
            return;
        }

        State = State with
        {
            CurrentNodeId = connection.ToNodeId,
            Status = SceneRuntimeStatus.Running,
            LastTransition = $"{node.NodeId}:{socketId} → {connection.ToNodeId}:{connection.ToSocketId}"
        };
    }

    private SceneNode? FindCurrentNode(SceneDefinition scene) =>
        State.CurrentNodeId is null
            ? null
            : scene.Graph.Nodes.FirstOrDefault(node =>
                node.NodeId.Equals(State.CurrentNodeId, StringComparison.OrdinalIgnoreCase));

    private void Wait(string waitingFor)
    {
        State = State with
        {
            Status = SceneRuntimeStatus.Waiting,
            WaitingFor = waitingFor,
            LastEvent = "SceneRuntimeWaiting",
            LastTransition = $"Ожидание: {waitingFor}"
        };

        Publish(
            "SceneRuntimeWaiting",
            State.SceneId ?? string.Empty,
            State.CurrentNodeId,
            State.LastChoiceId,
            State.LastTransition);
    }

    private void ClearInterface()
    {
        if (_hub.Get<InterfaceState>("interfaces").Value.ActiveDialog is null)
        {
            return;
        }

        _hub.Get<InterfaceState>("interfaces").Set(
            new InterfaceState(null),
            "SceneRuntime");
    }

    private static string GetParameter(SceneNode node, string key, string fallback = "") =>
        node.Parameters.TryGetValue(key, out var value) ? value : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> payload, string key, int fallback) =>
        payload.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    private void Publish(
        string eventType,
        string sceneId,
        string? nodeId,
        string? choiceId,
        string message)
    {
        State = State with
        {
            LastEvent = eventType,
            LastTransition = message
        };

        Published?.Invoke(
            this,
            new SceneRuntimeEvent(
                eventType,
                DateTimeOffset.UtcNow,
                sceneId,
                nodeId,
                choiceId,
                message));
    }
}
