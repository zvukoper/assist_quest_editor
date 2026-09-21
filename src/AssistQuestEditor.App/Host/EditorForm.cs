using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class EditorForm : WebViewForm
{
    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDataChannel<PlayerState> _player;
    private readonly IDataChannel<WorldSelectionState> _selection;
    private const int DefinitionSchemaVersion = 1;

    private readonly QuestGraphStore _questGraph;
    private readonly QuestRuntime _runtime;
    private readonly HashSet<string> _executedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _isGraphEditor;
    private string? _currentDefinitionPath;
    private string? _lastDefinitionPath;
    private bool _documentDirty;

    public string WindowKey { get; }

    public EditorForm(string title, string page, IDataChannelHub hub, QuestGraphStore questGraph, QuestRuntime runtime)
        : base($"Assist Quest Editor — {title}", page, new Size(1380, 900), "editor:" + page)
    {
        WindowKey = "editor:" + page;
        _player = hub.Get<PlayerState>("player");
        _selection = hub.Get<WorldSelectionState>("world-selection");
        _questGraph = questGraph;
        _runtime = runtime;
        _lastDefinitionPath = AppUiPreferencesStore.Load().LastQuestDefinitionPath;
        _isGraphEditor = page.EndsWith("#graph", StringComparison.OrdinalIgnoreCase);

        _player.Changed += Player_Changed;
        _selection.Changed += Selection_Changed;
        _questGraph.Changed += QuestGraph_Changed;
        _runtime.Published += Runtime_Published;
        UpdateWindowTitle();

        FormClosed += (_, _) =>
        {
            _player.Changed -= Player_Changed;
            _selection.Changed -= Selection_Changed;
            _questGraph.Changed -= QuestGraph_Changed;
            _runtime.Published -= Runtime_Published;
        };
    }

    protected override void OnBrowserReady()
    {
        PostSimulatorContext();
        if (_isGraphEditor)
        {
            PostQuestGraph();
            PostRuntimeState();
        }
    }

    protected override void OnWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var action = root.TryGetProperty("action", out var actionNode) ? actionNode.GetString() : null;

            if (string.Equals(action, "coordinate_request", StringComparison.OrdinalIgnoreCase))
            {
                HandleCoordinateRequest(root);
                return;
            }

            if (_isGraphEditor)
            {
                HandleGraphAction(root, action);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("EditorForm: ошибка обработки web action.", ex);
            PostJson(JsonSerializer.Serialize(new
            {
                type = "host_error",
                message = "Ошибка команды редактора: " + ex.Message
            }, WebJsonOptions));
        }
    }

    private void HandleCoordinateRequest(JsonElement root)
    {
        var source = root.TryGetProperty("source", out var sourceNode) ? sourceNode.GetString() : null;

        if (string.Equals(source, "selected_point", StringComparison.OrdinalIgnoreCase))
        {
            var point = _selection.Value.Point;
            if (point is null)
            {
                PostJson(JsonSerializer.Serialize(new
                {
                    type = "coordinate_error",
                    source,
                    message = "В симуляторе не выбрана точка."
                }, WebJsonOptions));
                return;
            }

            PostJson(JsonSerializer.Serialize(new
            {
                type = "coordinate",
                source,
                point,
                position = point.Position
            }, WebJsonOptions));
            return;
        }

        if (string.Equals(source, "player", StringComparison.OrdinalIgnoreCase))
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "coordinate",
                source,
                position = _player.Value.Position
            }, WebJsonOptions));
        }
    }

    private void HandleGraphAction(JsonElement root, string? action)
    {
        switch (action)
        {
            case "graph_add_node":
            {
                var node = _questGraph.AddNode(
                    String(root, "nodeType", "Phase"),
                    String(root, "title", "Phase"),
                    Number(root, "x", 420),
                    Number(root, "y", 120));

                var connectFromNodeId = OptionalString(root, "connectFromNodeId");
                var connectFromSocketId = OptionalString(root, "connectFromSocketId");

                if (!string.IsNullOrWhiteSpace(connectFromNodeId) &&
                    !string.IsNullOrWhiteSpace(connectFromSocketId))
                {
                    var targetSocket = node.Sockets.FirstOrDefault(
                        socket => socket.Direction == SocketDirection.Input);

                    if (targetSocket is null)
                    {
                        _questGraph.RemoveNode(node.NodeId);
                        throw new InvalidOperationException(
                            $"Нода «{node.NodeType}» не имеет входного socket.");
                    }

                    var result = _questGraph.Connect(
                        connectFromNodeId,
                        connectFromSocketId,
                        node.NodeId,
                        targetSocket.SocketId);

                    if (!result.Added)
                    {
                        _questGraph.RemoveNode(node.NodeId);
                        throw new InvalidOperationException(
                            result.Error ?? "Не удалось автоматически подключить новую ноду.");
                    }

                    var connection = result.Connection!;
                    AppLogger.Info(
                        "Quest Graph: новая нода автоматически подключена.",
                        $"node={node.NodeId}; from={connection.FromNodeId}:{connection.FromSocketId}; " +
                        $"to={connection.ToNodeId}:{connection.ToSocketId}");
                }

                AppLogger.Info("Quest Graph: добавлена нода.",
                    $"id={node.NodeId}; type={node.NodeType}");
                PostQuestGraph(node.NodeId);
                break;
            }

            case "graph_update_node":
            {
                var nodeId = Required(root, "nodeId");
                var updated = _questGraph.UpdateNode(
                    nodeId,
                    String(root, "title", null),
                    NumberOrNull(root, "x"),
                    NumberOrNull(root, "y"),
                    Parameters(root, "parameters"));

                if (updated is null)
                {
                    throw new InvalidOperationException("Нода не найдена: " + nodeId);
                }

                AppLogger.Info("Quest Graph: обновлена нода.", $"id={updated.NodeId}");
                PostQuestGraph(updated.NodeId);
                break;
            }

            case "graph_remove_node":
            {
                var nodeId = Required(root, "nodeId");
                if (!_questGraph.RemoveNode(nodeId))
                {
                    throw new InvalidOperationException("Нода не найдена: " + nodeId);
                }

                AppLogger.Info("Quest Graph: удалена нода.", $"id={nodeId}");
                PostQuestGraph();
                break;
            }

            case "graph_connect":
            {
                var result = _questGraph.Connect(
                    Required(root, "fromNodeId"),
                    Required(root, "fromSocketId"),
                    Required(root, "toNodeId"),
                    Required(root, "toSocketId"));

                if (!result.Added)
                {
                    throw new InvalidOperationException(result.Error ?? "Связь не создана.");
                }

                var connection = result.Connection!;
                AppLogger.Info(
                    "Quest Graph: создана связь.",
                    $"from={connection.FromNodeId}:{connection.FromSocketId}; to={connection.ToNodeId}:{connection.ToSocketId}");
                PostQuestGraph();
                break;
            }

            case "graph_disconnect":
            {
                var connection = new QuestConnection(
                    Required(root, "fromNodeId"),
                    Required(root, "fromSocketId"),
                    Required(root, "toNodeId"),
                    Required(root, "toSocketId"));

                if (!_questGraph.Disconnect(connection))
                {
                    throw new InvalidOperationException("Связь не найдена.");
                }

                AppLogger.Info(
                    "Quest Graph: удалена связь.",
                    $"from={connection.FromNodeId}:{connection.FromSocketId}; to={connection.ToNodeId}:{connection.ToSocketId}");
                PostQuestGraph();
                break;
            }
            case "graph_new":
            {
                _questGraph.Replace(QuestGraphFactory.CreateStarter());
                _currentDefinitionPath = null;
                _documentDirty = false;
                UpdateWindowTitle();
                AppLogger.Info("Quest Graph: создан новый документ.");
                PostQuestGraph();
                break;
            }

            case "graph_open":
                OpenGraph();
                break;

            case "graph_open_last":
                OpenLastGraph();
                break;

            case "graph_save":
                SaveGraph(saveAs: false);
                break;

            case "graph_save_as":
                SaveGraph(saveAs: true);
                break;

            case "graph_validate":
            {
                AppLogger.Info("Quest Graph: выполнена проверка графа.");
                PostQuestGraph();
                break;
            }

            case "graph_undo":
            {
                if (!_questGraph.Undo())
                {
                    throw new InvalidOperationException("Нет изменений для отмены.");
                }

                AppLogger.Info("Quest Graph: выполнена отмена изменения.");
                PostQuestGraph();
                break;
            }

            case "graph_redo":
            {
                if (!_questGraph.Redo())
                {
                    throw new InvalidOperationException("Нет изменений для повтора.");
                }

                AppLogger.Info("Quest Graph: выполнен повтор изменения.");
                PostQuestGraph();
                break;
            }
        }
    }

    private void OpenGraph()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Открыть Quest Definition",
            Filter = "Quest Definition (*.json)|*.json|JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_lastDefinitionPath) && File.Exists(_lastDefinitionPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_lastDefinitionPath);
            dialog.FileName = Path.GetFileName(_lastDefinitionPath);
        }

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        LoadGraphFromPath(dialog.FileName);
    }

    private void OpenLastGraph()
    {
        var path = _lastDefinitionPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Открыть последний Quest Definition",
                Filter = "Quest Definition (*.json)|*.json|JSON (*.json)|*.json|Все файлы (*.*)|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }

        LoadGraphFromPath(path);
    }

    private void LoadGraphFromPath(string path)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<QuestDefinitionDocument>(json, WebJsonOptions)
            ?? throw new InvalidOperationException("Файл Quest Definition пуст или повреждён.");

        if (document.SchemaVersion != DefinitionSchemaVersion)
            throw new InvalidOperationException($"Неподдерживаемая версия схемы Quest Definition: {document.SchemaVersion}. Поддерживается {DefinitionSchemaVersion}.");

        if (document.Definition?.Graph is null)
            throw new InvalidOperationException("В документе отсутствует Quest Graph.");

        _questGraph.Replace(document.Definition.Graph);
        _currentDefinitionPath = path;
        _lastDefinitionPath = path;
        SaveLastDefinitionPath();
        _documentDirty = false;
        UpdateWindowTitle();
        PostQuestGraph();

        AppLogger.Info("Quest Graph: документ открыт.", $"path={path}; schema={document.SchemaVersion}");
    }

    private void SaveGraph(bool saveAs)
    {
        var path = _currentDefinitionPath;

        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Сохранить Quest Definition",
                Filter = "Quest Definition (*.json)|*.json|JSON (*.json)|*.json",
                DefaultExt = "json",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(path)
                    ? $"{SanitizeFileName(_questGraph.Value.Name)}.json"
                    : Path.GetFileName(path)
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }

        var definition = new QuestDefinition(
            _questGraph.Value.Id,
            _questGraph.Value.Name,
            string.Empty,
            _questGraph.Value,
            Array.Empty<string>());

        var document = new QuestDefinitionDocument(DefinitionSchemaVersion, definition);
        var output = JsonSerializer.Serialize(document, WebJsonOptions);
        File.WriteAllText(path!, output);

        _currentDefinitionPath = path;
        _lastDefinitionPath = path;
        SaveLastDefinitionPath();
        _documentDirty = false;
        UpdateWindowTitle();
        PostQuestGraph();

        AppLogger.Info("Quest Graph: документ сохранён.",
            $"path={_currentDefinitionPath}; schema={DefinitionSchemaVersion}; bytes={output.Length}");
    }

    private void SaveLastDefinitionPath()
    {
        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { LastQuestDefinitionPath = _lastDefinitionPath });
    }

    private void Runtime_Published(object? sender, QuestRuntimeEvent e)
    {
        if (!_isGraphEditor) return;

        if (e.EventType.Equals("RuntimeStarted", StringComparison.OrdinalIgnoreCase))
            _executedNodeIds.Clear();

        if (e.EventType.Equals("NodeEntered", StringComparison.OrdinalIgnoreCase) && e.NodeId is not null)
            _executedNodeIds.Add(e.NodeId);

        PushRuntimeStateOnUiThread();
    }

    private void PostRuntimeState()
    {
        if (!_isGraphEditor || Browser.CoreWebView2 is null) return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "runtime_state",
            runtime = _runtime.State,
            executedNodeIds = _executedNodeIds.ToArray()
        }, WebJsonOptions));
    }

    private void PushRuntimeStateOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke((Action)PostRuntimeState);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateWindowTitle()
    {
        if (!_isGraphEditor) return;
        var fileName = string.IsNullOrWhiteSpace(_currentDefinitionPath)
            ? "Новый документ"
            : Path.GetFileName(_currentDefinitionPath);
        Text = $"Assist Quest Editor — Нодовый редактор — {fileName}" + (_documentDirty ? " *" : string.Empty);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "quest" : clean;
    }

    private void PostQuestGraph(string? selectedNodeId = null)
    {
        if (!_isGraphEditor || Browser.CoreWebView2 is null)
        {
            return;
        }

        PostJson(JsonSerializer.Serialize(new
        {
            type = "quest_graph",
            graph = _questGraph.Value,
            selectedNodeId,
            canUndo = _questGraph.CanUndo,
            canRedo = _questGraph.CanRedo,
            validation = QuestGraphValidator.Validate(_questGraph.Value),
            documentPath = _currentDefinitionPath ?? string.Empty,
            lastDocumentPath = _lastDefinitionPath ?? string.Empty,
            documentDirty = _documentDirty
        }, WebJsonOptions));
    }

    private void QuestGraph_Changed(object? sender, EventArgs e)
    {
        if (_isGraphEditor)
        {
            _documentDirty = true;
            UpdateWindowTitle();
            PushGraphOnUiThread();
        }
    }

    private void PushGraphOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() => PostQuestGraph()));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Player_Changed(object? sender, EventArgs e) => PushContextOnUiThread();
    private void Selection_Changed(object? sender, EventArgs e) => PushContextOnUiThread();

    private void PushContextOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)PostSimulatorContext);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void PostSimulatorContext()
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        PostJson(JsonSerializer.Serialize(new
        {
            type = "simulator_context",
            player = _player.Value,
            selection = _selection.Value
        }, WebJsonOptions));
    }

    private static string Required(JsonElement root, string name)
    {
        var value = String(root, name, null);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Не задано значение «{name}».")
            : value;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string String(JsonElement root, string name, string? fallback)
    {
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind != JsonValueKind.Null &&
               element.ValueKind != JsonValueKind.Undefined
            ? element.ToString()
            : fallback ?? string.Empty;
    }

    private static IReadOnlyDictionary<string, string>? Parameters(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ToString();
        }

        return result;
    }

    private static double Number(JsonElement root, string name, double fallback) =>
        NumberOrNull(root, name) ?? fallback;

    private static double? NumberOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            return number;
        }

        return double.TryParse(
            element.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }
}
