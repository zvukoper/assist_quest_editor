using System.Globalization;

namespace AssistQuestEditor.Domain;

public sealed record SceneGraphConnectionResult(bool Added, SceneConnection? Connection, string? Error)
{
    public static SceneGraphConnectionResult Success(SceneConnection connection) => new(true, connection, null);
    public static SceneGraphConnectionResult Failure(string error) => new(false, null, error);
}

public sealed class SceneGraphStore
{
    private SceneDefinition _value;
    private readonly Stack<SceneDefinition> _undo = new();
    private readonly Stack<SceneDefinition> _redo = new();

    public SceneGraphStore(SceneDefinition initial) =>
        _value = initial ?? throw new ArgumentNullException(nameof(initial));

    public SceneDefinition Value => _value;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;


    public SceneDialogue? FindDialogue(string dialogueId) =>
        _value.Dialogues.FirstOrDefault(dialogue =>
            dialogue.Id.Equals(dialogueId, StringComparison.OrdinalIgnoreCase));

    public SceneChoice? FindChoice(string choiceId) =>
        _value.Choices.FirstOrDefault(choice =>
            choice.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase));

    public SceneDialogue CreateDialogueForNode(string nodeId)
    {
        var node = FindNode(nodeId)
            ?? throw new InvalidOperationException("Нода не найдена: " + nodeId);

        if (!node.NodeType.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нода «" + nodeId + "» не является Dialogue.");

        if (node.Parameters.TryGetValue("dialogueId", out var existingId) &&
            !string.IsNullOrWhiteSpace(existingId))
        {
            var existing = FindDialogue(existingId);
            if (existing is not null)
                return existing;
        }

        var dialogue = new SceneDialogue(
            CreateUniqueId("dialogue"),
            "Персонаж",
            "Новый текст диалога.");

        var parameters = NormalizeParameters(node.Parameters);
        parameters["dialogueId"] = dialogue.Id;

        var nodes = _value.Graph.Nodes
            .Select(item => item.NodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase)
                ? item with { Parameters = parameters }
                : item)
            .ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes },
            Dialogues = _value.Dialogues.Append(dialogue).ToArray()
        });

        return dialogue;
    }

    public bool UpdateDialogue(string dialogueId, string speaker, string text)
    {
        var dialogue = FindDialogue(dialogueId);
        if (dialogue is null)
            return false;

        var updated = dialogue with
        {
            Speaker = speaker ?? string.Empty,
            Text = text ?? string.Empty
        };

        Apply(_value with
        {
            Dialogues = _value.Dialogues
                .Select(item => item.Id.Equals(dialogueId, StringComparison.OrdinalIgnoreCase) ? updated : item)
                .ToArray()
        });

        return true;
    }

    public SceneChoice CreateChoiceForNode(string nodeId)
    {
        var node = FindNode(nodeId)
            ?? throw new InvalidOperationException("Нода не найдена: " + nodeId);

        if (!node.NodeType.Equals("Choice", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нода «" + nodeId + "» не является Choice.");

        if (node.Parameters.TryGetValue("choiceId", out var existingId) &&
            !string.IsNullOrWhiteSpace(existingId))
        {
            var existing = FindChoice(existingId);
            if (existing is not null)
                return existing;
        }

        var outputSockets = node.Sockets
            .Where(socket => socket.Direction == SocketDirection.Output)
            .ToList();

        while (outputSockets.Count < 2)
        {
            var socketId = node.NodeId + ".option." + ShortToken();
            var socket = new SocketDefinition(
                socketId,
                "Вариант " + (outputSockets.Count + 1),
                SocketDirection.Output);
            outputSockets.Add(socket);
        }

        var choiceId = CreateUniqueId("choice");
        var options = outputSockets
            .Select((socket, index) => new SceneChoiceOption(
                choiceId + ".option." + (index + 1),
                "Вариант " + (index + 1),
                socket.SocketId))
            .ToArray();

        var choice = new SceneChoice(
            choiceId,
            "Новый выбор",
            "Персонаж",
            "Текст вопроса выбора.",
            options);

        var parameters = NormalizeParameters(node.Parameters);
        parameters["choiceId"] = choice.Id;
        parameters["outputCount"] = outputSockets.Count.ToString(CultureInfo.InvariantCulture);

        var sockets = node.Sockets
            .Concat(
                outputSockets
                    .Where(expected => !node.Sockets.Any(existing =>
                        existing.SocketId.Equals(expected.SocketId, StringComparison.OrdinalIgnoreCase))))
            .Select(socket =>
            {
                var optionIndex = outputSockets.FindIndex(item =>
                    item.SocketId.Equals(socket.SocketId, StringComparison.OrdinalIgnoreCase));
                return optionIndex >= 0
                    ? socket with { Name = "Вариант " + (optionIndex + 1) }
                    : socket;
            })
            .ToArray();

        var nodes = _value.Graph.Nodes
            .Select(item => item.NodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase)
                ? item with { Parameters = parameters, Sockets = sockets }
                : item)
            .ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes },
            Choices = _value.Choices.Append(choice).ToArray()
        });

        return choice;
    }

    public bool UpdateChoice(
        string choiceId,
        string title,
        string speaker,
        string text,
        IReadOnlyDictionary<string, string> optionTexts,
        out string? error)
    {
        error = null;
        var choice = FindChoice(choiceId);
        if (choice is null)
        {
            error = "Choice resource «" + choiceId + "» не найден.";
            return false;
        }

        var existingIds = choice.Options
            .Select(option => option.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var optionId in optionTexts.Keys)
        {
            if (!existingIds.Contains(optionId))
            {
                error = "В Choice resource «" + choiceId + "» нет варианта «" + optionId + "».";
                return false;
            }
        }

        var updatedOptions = choice.Options
            .Select(option => optionTexts.TryGetValue(option.Id, out var value)
                ? option with { Text = value ?? string.Empty }
                : option)
            .ToArray();

        var updated = choice with
        {
            Title = title ?? string.Empty,
            Speaker = speaker ?? string.Empty,
            Text = text ?? string.Empty,
            Options = updatedOptions
        };

        var nodes = _value.Graph.Nodes.Select(node =>
        {
            if (!node.NodeType.Equals("Choice", StringComparison.OrdinalIgnoreCase) ||
                !node.Parameters.TryGetValue("choiceId", out var nodeChoiceId) ||
                !nodeChoiceId.Equals(choiceId, StringComparison.OrdinalIgnoreCase))
                return node;

            var outputIndex = 0;
            var sockets = node.Sockets.Select(socket =>
            {
                if (socket.Direction != SocketDirection.Output)
                    return socket;

                var option = updatedOptions.FirstOrDefault(item =>
                    item.OutputSocketId.Equals(socket.SocketId, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                    return socket;

                outputIndex++;
                var suffix = string.IsNullOrWhiteSpace(option.Text) ? "Вариант " + outputIndex : option.Text.Trim();
                return socket with { Name = "Вариант " + outputIndex + " · " + TrimSocketLabel(suffix) };
            }).ToArray();

            return node with { Sockets = sockets };
        }).ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes },
            Choices = _value.Choices
                .Select(item => item.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase) ? updated : item)
                .ToArray()
        });

        return true;
    }

    public SceneChoiceOption AddChoiceOption(string nodeId, string choiceId)
    {
        var node = FindNode(nodeId)
            ?? throw new InvalidOperationException("Нода не найдена: " + nodeId);

        if (!node.NodeType.Equals("Choice", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нода «" + nodeId + "» не является Choice.");

        var choice = FindChoice(choiceId)
            ?? throw new InvalidOperationException("Choice resource «" + choiceId + "» не найден.");

        var socketId = node.NodeId + ".option." + ShortToken();
        var optionId = choice.Id + ".option." + ShortToken();
        var optionNumber = choice.Options.Count + 1;
        var option = new SceneChoiceOption(
            optionId,
            "Вариант " + optionNumber,
            socketId);

        var socket = new SocketDefinition(
            socketId,
            "Вариант " + optionNumber,
            SocketDirection.Output);

        var parameters = NormalizeParameters(node.Parameters);
        parameters["choiceId"] = choice.Id;
        parameters["outputCount"] = (choice.Options.Count + 1).ToString(CultureInfo.InvariantCulture);

        var updatedNode = node with
        {
            Parameters = parameters,
            Sockets = node.Sockets.Append(socket).ToArray()
        };

        var updatedChoice = choice with
        {
            Options = choice.Options.Append(option).ToArray()
        };

        Apply(_value with
        {
            Graph = _value.Graph with
            {
                Nodes = _value.Graph.Nodes
                    .Select(item => item.NodeId.Equals(node.NodeId, StringComparison.OrdinalIgnoreCase) ? updatedNode : item)
                    .ToArray()
            },
            Choices = _value.Choices
                .Select(item => item.Id.Equals(choice.Id, StringComparison.OrdinalIgnoreCase) ? updatedChoice : item)
                .ToArray()
        });

        return option;
    }

    public bool RemoveChoiceOption(
        string nodeId,
        string choiceId,
        string optionId,
        out string? error)
    {
        error = null;
        var node = FindNode(nodeId);
        if (node is null)
        {
            error = "Нода не найдена: " + nodeId;
            return false;
        }

        var choice = FindChoice(choiceId);
        if (choice is null)
        {
            error = "Choice resource «" + choiceId + "» не найден.";
            return false;
        }

        if (choice.Options.Count <= 1)
        {
            error = "Choice должен содержать хотя бы один вариант; последний вариант удалить нельзя.";
            return false;
        }

        var option = choice.Options.FirstOrDefault(item =>
            item.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            error = "Вариант «" + optionId + "» не найден.";
            return false;
        }

        if (_value.Graph.Connections.Any(connection =>
            connection.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) &&
            connection.FromSocketId.Equals(option.OutputSocketId, StringComparison.OrdinalIgnoreCase)))
        {
            error = "Нельзя удалить вариант, пока его Output socket подключён. Сначала разорвите связь.";
            return false;
        }

        var updatedChoice = choice with
        {
            Options = choice.Options
                .Where(item => !item.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };

        var updatedNode = node with
        {
            Sockets = node.Sockets
                .Where(socket => !socket.SocketId.Equals(option.OutputSocketId, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            Parameters = NormalizeParameters(node.Parameters)
        };

        if (updatedNode.Parameters.ContainsKey("outputCount"))
            updatedNode.Parameters["outputCount"] = updatedChoice.Options.Count.ToString(CultureInfo.InvariantCulture);

        Apply(_value with
        {
            Graph = _value.Graph with
            {
                Nodes = _value.Graph.Nodes
                    .Select(item => item.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) ? updatedNode : item)
                    .ToArray()
            },
            Choices = _value.Choices
                .Select(item => item.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase) ? updatedChoice : item)
                .ToArray()
        });

        return true;
    }

    private static string CreateUniqueId(string prefix) =>
        prefix + "." + ShortToken();

    private static string ShortToken() =>
        Guid.NewGuid().ToString("N")[..10];

    private static string TrimSocketLabel(string value) =>
        value.Length <= 34 ? value : value[..33] + "…";

    public SceneNode? FindNode(string nodeId) =>
        _value.Graph.Nodes.FirstOrDefault(node =>
            node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));

    public SceneNode AddNode(string nodeType, string title, double x, double y)
    {
        var type = string.IsNullOrWhiteSpace(nodeType) ? "Dialogue" : nodeType.Trim();
        var nodeId = "scene-node-" + Guid.NewGuid().ToString("N")[..8];
        var parameters = SceneNodeCatalog.CreateDefaultParameters(type);
        var node = new SceneNode(
            nodeId,
            type,
            string.IsNullOrWhiteSpace(title) ? type : title.Trim(),
            x,
            y,
            SceneNodeCatalog.CreateSockets(type, nodeId, parameters))
        {
            Parameters = parameters
        };

        Apply(_value with { Graph = _value.Graph with { Nodes = _value.Graph.Nodes.Append(node).ToArray() } });
        return node;
    }

    public SceneNode? UpdateNode(
        string nodeId,
        string? title = null,
        double? x = null,
        double? y = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var nodes = _value.Graph.Nodes.ToArray();
        var index = Array.FindIndex(nodes, node =>
            node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var current = nodes[index];
        var nextParameters = parameters is null ? current.Parameters : NormalizeParameters(parameters);
        var nextSockets = parameters is null || HasContentDefinedSockets(current, nextParameters)
            ? current.Sockets
            : SceneNodeCatalog.CreateSockets(current.NodeType, current.NodeId, nextParameters);

        nodes[index] = current with
        {
            Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
            X = x ?? current.X,
            Y = y ?? current.Y,
            Parameters = nextParameters,
            Sockets = nextSockets
        };

        var socketIds = nodes.ToDictionary(
            node => node.NodeId,
            node => node.Sockets.Select(socket => socket.SocketId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var connections = _value.Graph.Connections
            .Where(connection =>
                socketIds.TryGetValue(connection.FromNodeId, out var fromSockets) &&
                fromSockets.Contains(connection.FromSocketId) &&
                socketIds.TryGetValue(connection.ToNodeId, out var toSockets) &&
                toSockets.Contains(connection.ToSocketId))
            .ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes, Connections = connections }
        });

        return nodes[index];
    }

    public bool RemoveNode(string nodeId)
    {
        var nodes = _value.Graph.Nodes
            .Where(node => !node.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nodes.Length == _value.Graph.Nodes.Count) return false;

        var connections = _value.Graph.Connections
            .Where(connection =>
                !connection.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase) &&
                !connection.ToNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Apply(_value with
        {
            Graph = _value.Graph with { Nodes = nodes, Connections = connections }
        });
        return true;
    }

    public SceneGraphConnectionResult Connect(
        string fromNodeId,
        string fromSocketId,
        string toNodeId,
        string toSocketId)
    {
        var fromNode = FindNode(fromNodeId);
        var toNode = FindNode(toNodeId);
        if (fromNode is null || toNode is null)
            return SceneGraphConnectionResult.Failure("Для связи нужны две существующие ноды.");

        var fromSocket = fromNode.Sockets.FirstOrDefault(socket =>
            socket.SocketId.Equals(fromSocketId, StringComparison.OrdinalIgnoreCase));
        var toSocket = toNode.Sockets.FirstOrDefault(socket =>
            socket.SocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase));

        if (fromSocket is null || toSocket is null)
            return SceneGraphConnectionResult.Failure("Указанный socket не найден.");

        if (fromSocket.Direction != SocketDirection.Output || toSocket.Direction != SocketDirection.Input)
            return SceneGraphConnectionResult.Failure("Связь должна идти из Output в Input.");

        // Входной socket принимает ровно одну связь (SCENE_CONN005), поэтому
        // повторная попытка подключить занятый Input отклоняется именно по этой
        // причине: она информативнее проверки полного дубликата и покрывает её.
        if (_value.Graph.Connections.Any(connection =>
            connection.ToNodeId.Equals(toNodeId, StringComparison.OrdinalIgnoreCase) &&
            connection.ToSocketId.Equals(toSocketId, StringComparison.OrdinalIgnoreCase)))
            return SceneGraphConnectionResult.Failure("Входной socket уже подключён.");

        var connectionResult = new SceneConnection(
            fromNode.NodeId,
            fromSocket.SocketId,
            toNode.NodeId,
            toSocket.SocketId);

        Apply(_value with
        {
            Graph = _value.Graph with
            {
                Connections = _value.Graph.Connections.Append(connectionResult).ToArray()
            }
        });
        return SceneGraphConnectionResult.Success(connectionResult);
    }

    public bool Disconnect(SceneConnection connection)
    {
        var connections = _value.Graph.Connections.Where(item => item != connection).ToArray();
        if (connections.Length == _value.Graph.Connections.Count) return false;

        Apply(_value with { Graph = _value.Graph with { Connections = connections } });
        return true;
    }

    public void Replace(SceneDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _value = definition;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public int ApplyLayout()
    {
        var positions = SceneGraphLayout.Compute(_value.Graph);
        if (positions.Count == 0) return 0;

        var changed = 0;
        var nodes = _value.Graph.Nodes.Select(node =>
        {
            if (!positions.TryGetValue(node.NodeId, out var position) ||
                (Math.Abs(node.X - position.X) < 0.001 && Math.Abs(node.Y - position.Y) < 0.001))
                return node;

            changed++;
            return node with { X = position.X, Y = position.Y };
        }).ToArray();

        if (changed == 0) return 0;

        Apply(_value with { Graph = _value.Graph with { Nodes = nodes } });
        return changed;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(_value);
        _value = _undo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(_value);
        _value = _redo.Pop();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Choice-нода, привязанная к существующему SceneChoice ресурсу, получает Output
    /// sockets из самого ресурса: каждый <c>SceneChoiceOption.OutputSocketId</c> — это
    /// стабильный canonical контент сцены, а не порядковый <c>optionN</c>.
    /// Пересборка таких sockets по схеме каталога потеряла бы стабильные option ID и
    /// оборвала бы уже настроенные связи, поэтому правка параметров их не трогает.
    /// </summary>
    private bool HasContentDefinedSockets(
        SceneNode node,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (!node.NodeType.Equals("Choice", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!parameters.TryGetValue("choiceId", out var choiceId) ||
            string.IsNullOrWhiteSpace(choiceId))
            return false;

        return _value.Choices.Any(choice =>
            choice.Id.Equals(choiceId, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> NormalizeParameters(
        IReadOnlyDictionary<string, string> parameters) =>
        new Dictionary<string, string>(
            parameters
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(
                    pair => pair.Key.Trim(),
                    pair => pair.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private void Apply(SceneDefinition next)
    {
        _undo.Push(_value);
        _value = next;
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}