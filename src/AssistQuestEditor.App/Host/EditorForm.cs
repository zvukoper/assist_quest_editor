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
    private readonly SceneGraphStore _sceneGraph;
    private readonly SceneCatalog _sceneCatalog;
    private readonly SceneDocumentSession _sceneDocument;
    private readonly IQuestRuntimeController _runtime;
    private readonly HashSet<string> _executedNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _isGraphEditor;
    private readonly bool _isSceneEditor;
    private readonly bool _isDialogueWorkspace;
    private string? _currentDefinitionPath;
    private string? _lastDefinitionPath;
    private bool _documentDirty;
    private bool _loadingScene;
    private string? _navigationBackQuestId;
    private string? _navigationBackQuestPath;
    private string? _pendingQuestNodeSelection;
    private string? _navigationBackQuestNodeId;
    private string? _navigationBackQuestNodeTitle;

    /// <summary>
    /// Последние открытые ресурсы «своего» вида (Quest для нодового редактора,
    /// Scene для редактора сцен). Список приходит от Host, который владеет
    /// историей и сохраняет её в настройках.
    /// </summary>
    private IReadOnlyList<string> _recentFiles = Array.Empty<string>();

    public event EventHandler<EditorNavigationRequestEventArgs>? NavigationRequested;

    /// <summary>
    /// Запрос на регистрацию открытого ресурса в истории. Аргумент — путь.
    /// Событие, а не ссылка на MainForm: редактор не должен знать о владельце
    /// истории, ему достаточно сообщить о факте открытия.
    /// </summary>
    public event EventHandler<string>? RecentFileOpened;

    /// <summary>
    /// Ресурс из истории больше недоступен: Host убирает его из списка.
    /// </summary>
    public event EventHandler<string>? RecentFileUnavailable;

    public string WindowKey { get; }

    public string? CurrentQuestPath =>
        _isGraphEditor ? _currentDefinitionPath : null;

    public EditorForm(
        string title,
        string page,
        IDataChannelHub hub,
        QuestGraphStore questGraph,
        SceneGraphStore sceneGraph,
        SceneCatalog sceneCatalog,
        SceneDocumentSession sceneDocument,
        IQuestRuntimeController runtime)
        : base($"{title}", page, new Size(1380, 900), "editor:" + page)
    {
        WindowKey = "editor:" + page;
        _player = hub.Get<PlayerState>("player");
        _selection = hub.Get<WorldSelectionState>("world-selection");
        _questGraph = questGraph;
        _sceneGraph = sceneGraph;
        _sceneCatalog = sceneCatalog;
        _sceneDocument = sceneDocument;
        _runtime = runtime;
        var preferences = AppUiPreferencesStore.Load();
        _lastDefinitionPath = preferences.LastQuestDefinitionPath;
        _isGraphEditor = page.EndsWith("#graph", StringComparison.OrdinalIgnoreCase);
        _isSceneEditor =
            page.EndsWith("#scene", StringComparison.OrdinalIgnoreCase) ||
            page.EndsWith("#dialogue", StringComparison.OrdinalIgnoreCase);
        _isDialogueWorkspace = page.EndsWith("#dialogue", StringComparison.OrdinalIgnoreCase);

        _player.Changed += Player_Changed;
        _selection.Changed += Selection_Changed;
        _questGraph.Changed += QuestGraph_Changed;
        _sceneGraph.Changed += SceneGraph_Changed;
        _sceneDocument.Changed += SceneDocument_Changed;
        _runtime.Published += Runtime_Published;
        UpdateWindowTitle();

        FormClosed += (_, _) =>
        {
            _player.Changed -= Player_Changed;
            _selection.Changed -= Selection_Changed;
            _questGraph.Changed -= QuestGraph_Changed;
            _sceneGraph.Changed -= SceneGraph_Changed;
            _sceneDocument.Changed -= SceneDocument_Changed;
            _runtime.Published -= Runtime_Published;
        };
    }

    public void RefreshSceneCatalog()
    {
        if (_isGraphEditor || _isSceneEditor)
            PostSceneCatalog();
    }

    /// <summary>Вид ресурса, историю которого ведёт этот редактор.</summary>
    public string RecentFileKind => _isGraphEditor ? App.RecentFileKind.Quest : App.RecentFileKind.Scene;

    /// <summary>
    /// Принимает обновлённый список последних файлов от Host и перерисовывает
    /// меню. Рассылка идёт всем редакторам: открытие файла в одном окне должно
    /// сразу отражаться в другом.
    /// </summary>
    public void UpdateRecentFiles(string kind, IReadOnlyList<string> paths)
    {
        if (!string.Equals(kind, RecentFileKind, StringComparison.OrdinalIgnoreCase))
            return;

        _recentFiles = paths ?? Array.Empty<string>();
        PostRecentFiles();
    }

    public bool OpenQuestResourceAndSelectNode(string path, string nodeId)
    {
        if (!_isGraphEditor)
            throw new InvalidOperationException("Возврат из Scene доступен только в Quest Graph editor.");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        if (!ResourceFileTypes.TryGet(Path.GetExtension(path), out var resource) ||
            !resource.Kind.Equals("Quest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Исходный ресурс не является .aqquest: " + path);
        }

        var sameDocument =
            !string.IsNullOrWhiteSpace(_currentDefinitionPath) &&
            string.Equals(
                Path.GetFullPath(_currentDefinitionPath),
                Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase);

        if (!sameDocument && !OpenGraphResource(path))
            return false;

        FocusQuestNode(nodeId);
        return true;
    }

    public void FocusQuestNode(string nodeId)
    {
        if (!_isGraphEditor)
            return;

        if (_questGraph.FindNode(nodeId) is null)
            throw new InvalidOperationException("Quest Graph нода не найдена: " + nodeId);

        _pendingQuestNodeSelection = nodeId;

        if (Browser.CoreWebView2 is not null)
        {
            PostQuestGraph(nodeId);
            _pendingQuestNodeSelection = null;
        }
    }

    public bool OpenSceneFromQuestNode(
        string path,
        string nodeId,
        string nodeTitle,
        string? questId,
        string? questPath)
    {
        if (!_isSceneEditor)
            throw new InvalidOperationException("Переход в Scene доступен только в Scene Editor.");

        if (string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("NodeId обязателен.", nameof(nodeId));

        if (!ConfirmSceneSwitch())
            return false;

        LoadSceneFromPath(path, nodeId, nodeTitle, questId, questPath);
        return true;
    }

    public bool OpenResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        if (!ResourceFileTypes.TryGet(Path.GetExtension(path), out var resource))
            throw new InvalidOperationException("Неизвестный тип ресурса: " + Path.GetExtension(path));

        return resource.Kind switch
        {
            "Quest" when _isGraphEditor => OpenGraphResource(path),
            "Scene" when _isSceneEditor => OpenSceneResource(path),
            _ => throw new InvalidOperationException(
                "Ресурс " + resource.Extension + " пока не поддерживается этим редактором.")
        };
    }

    private bool OpenGraphResource(string path)
    {
        if (!ConfirmGraphSwitch(path))
            return false;

        LoadGraphFromPath(path);
        return true;
    }

    private bool OpenSceneResource(string path)
    {
        if (!ConfirmSceneSwitch(path))
            return false;

        LoadSceneFromPath(path);
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_isGraphEditor && _documentDirty)
        {
            var result = MessageBox.Show(
                this,
                "В Quest Graph есть несохранённые изменения.\r\n\r\n" +
                "Да — сохранить все изменения в файл.\r\n" +
                "Нет — закрыть без сохранения.\r\n" +
                "Отмена — вернуться в редактор.",
                "Несохранённые изменения",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == DialogResult.Yes && !SaveGraph(saveAs: false))
            {
                e.Cancel = true;
                return;
            }
        }

        if (_isSceneEditor && _sceneDocument.IsDirty)
        {
            var result = MessageBox.Show(
                this,
                "В Scene Graph есть несохранённые изменения.\r\n\r\n" +
                "Да — сохранить все изменения в файл.\r\n" +
                "Нет — закрыть без сохранения.\r\n" +
                "Отмена — вернуться в редактор.",
                "Несохранённые изменения",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == DialogResult.Yes && !SaveScene(saveAs: false))
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnFormClosing(e);
    }

    protected override void OnBrowserReady()
    {
        PostSimulatorContext();
        if (_isGraphEditor)
        {
            PostSceneCatalog();
            PostQuestGraph();
            PostRuntimeState();
        }

        if (_isSceneEditor)
        {
            PostSceneCatalog();
            PostSceneGraph();
        }

        // История последних файлов нужна обеим вкладкам: у нодового редактора
        // и у редактора сцен свой список.
        PostRecentFiles();

        if (_isGraphEditor && !string.IsNullOrWhiteSpace(_pendingQuestNodeSelection))
        {
            var nodeId = _pendingQuestNodeSelection;
            _pendingQuestNodeSelection = null;
            PostQuestGraph(nodeId);
        }
    }

    /// <summary>
    /// Отправляет список последних файлов в UI. Отдельным сообщением, а не
    /// внутри payload'а графа: список меняется независимо от документа, и
    /// перерисовывать граф ради него не нужно.
    /// </summary>
    private void PostRecentFiles()
    {
        if (Browser.CoreWebView2 is null)
            return;

        var paths = _recentFiles ?? Array.Empty<string>();

        PostJson(JsonSerializer.Serialize(new
        {
            type = "recent_files",
            kind = RecentFileKind,
            paths,
            currentPath = _isGraphEditor ? _currentDefinitionPath ?? string.Empty : _sceneDocument.CurrentPath ?? string.Empty
        }, WebJsonOptions));
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
            else if (_isSceneEditor)
            {
                HandleSceneAction(root, action);
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

    private void HandleSceneAction(JsonElement root, string? action)
    {
        switch (action)
        {
            case "navigate_to_quest_node":
            {
                var nodeId = Required(root, "nodeId");
                var questId = OptionalString(root, "questId");
                var questPath = OptionalString(root, "questPath");
                NavigationRequested?.Invoke(
                    this,
                    new EditorNavigationRequestEventArgs(
                        "navigate_to_quest_node",
                        nodeId,
                        null,
                        questId,
                        questPath));
                break;
            }

            case "scene_new":
                if (!ConfirmSceneSwitch())
                    break;

                _loadingScene = true;
                try
                {
                    _sceneGraph.Replace(SceneCatalogFactory.CreateStarter().Scenes.First());
                }
                finally
                {
                    _loadingScene = false;
                }

                _sceneDocument.MarkNew(_sceneGraph.Value.Id);
                UpdateWindowTitle();
                PostSceneCatalog();
                PostSceneGraph();
                AppLogger.Info("Scene Editor: создан новый документ.");
                break;

            case "scene_open":
                OpenScene();
                break;

            case "scene_open_last":
                OpenLastScene();
                break;

            case "scene_open_recent":
            {
                var recentScenePath = root.TryGetProperty("path", out var recentSceneNode)
                    ? recentSceneNode.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(recentScenePath) || !File.Exists(recentScenePath))
                {
                    AppLogger.Warn("История файлов: сцена недоступна.", recentScenePath);
                    MessageBox.Show(
                        this,
                        "Файл больше недоступен:\r\n\r\n" + recentScenePath,
                        "Открытие ресурса",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    RecentFileUnavailable?.Invoke(this, recentScenePath ?? string.Empty);
                    break;
                }

                OpenSceneResource(recentScenePath);
                break;
            }

            case "scene_open_resource":
            {
                var sceneId = Required(root, "sceneId");
                if (!_sceneCatalog.TryGetScene(sceneId, out var scene))
                    throw new InvalidOperationException("Scene «" + sceneId + "» не найдена.");

                var scenePath = ResolveScenePath(scene.Id);
                var sameDocument =
                    string.Equals(_sceneDocument.CurrentSceneId, scene.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_sceneDocument.CurrentPath, scenePath, StringComparison.OrdinalIgnoreCase);

                if (sameDocument)
                    break;

                if (!ConfirmSceneSwitch())
                {
                    PostSceneGraph();
                    break;
                }

                _loadingScene = true;
                try
                {
                    _sceneGraph.Replace(scene);
                }
                finally
                {
                    _loadingScene = false;
                }

                if (string.IsNullOrWhiteSpace(scenePath))
                    throw new InvalidOperationException("Для Scene «" + scene.Id + "» не найден исходный .aqscene файл.");

                _sceneDocument.Opened(scene.Id, scenePath);
                UpdateWindowTitle();
                PostSceneGraph();
                break;
            }


            case "scene_add_dialogue":
            {
                var dialogue = _sceneGraph.AddDialogue();
                AppLogger.Info(
                    "Scene Content: создан Dialogue resource.",
                    "dialogue=" + dialogue.Id);
                PostSceneGraph();
                PostJson(JsonSerializer.Serialize(new
                {
                    type = "scene_content_selected",
                    kind = "dialogue",
                    id = dialogue.Id
                }, WebJsonOptions));
                break;
            }

            case "scene_remove_dialogue":
            {
                var dialogueId = Required(root, "dialogueId");
                if (!_sceneGraph.RemoveDialogue(dialogueId, out var error))
                    throw new InvalidOperationException(error ?? "Не удалось удалить Dialogue resource.");

                AppLogger.Info(
                    "Scene Content: удалён Dialogue resource.",
                    "dialogue=" + dialogueId);
                PostSceneGraph();
                break;
            }

            case "scene_create_dialogue_for_node":
            {
                var nodeId = Required(root, "nodeId");
                var dialogue = _sceneGraph.CreateDialogueForNode(nodeId);
                AppLogger.Info(
                    "Scene Content: создан/привязан Dialogue.",
                    "node=" + nodeId + "; dialogue=" + dialogue.Id);
                PostSceneGraph(nodeId);
                break;
            }

            case "scene_update_dialogue":
            {
                var dialogueId = Required(root, "dialogueId");
                var updated = _sceneGraph.UpdateDialogue(
                    dialogueId,
                    String(root, "speaker", string.Empty),
                    String(root, "text", string.Empty));

                if (!updated)
                    throw new InvalidOperationException("Dialogue resource не найден: " + dialogueId);

                AppLogger.Info(
                    "Scene Content: обновлён Dialogue.",
                    "dialogue=" + dialogueId);
                PostSceneGraph();
                break;
            }

            case "scene_create_choice_for_node":
            {
                var nodeId = Required(root, "nodeId");
                var choice = _sceneGraph.CreateChoiceForNode(nodeId);
                AppLogger.Info(
                    "Scene Content: создан/привязан Choice.",
                    "node=" + nodeId + "; choice=" + choice.Id);
                PostSceneGraph(nodeId);
                break;
            }

            case "scene_update_choice":
            {
                var choiceId = Required(root, "choiceId");
                var error = string.Empty;
                var updated = _sceneGraph.UpdateChoice(
                    choiceId,
                    String(root, "title", string.Empty),
                    String(root, "speaker", string.Empty),
                    String(root, "text", string.Empty),
                    ChoiceOptionTexts(root, "options"),
                    out error);

                if (!updated)
                    throw new InvalidOperationException(error ?? "Не удалось обновить Choice resource.");

                AppLogger.Info(
                    "Scene Content: обновлён Choice.",
                    "choice=" + choiceId);
                PostSceneGraph();
                break;
            }

            case "scene_add_choice_option":
            {
                var nodeId = Required(root, "nodeId");
                var choiceId = Required(root, "choiceId");
                var option = _sceneGraph.AddChoiceOption(nodeId, choiceId);
                AppLogger.Info(
                    "Scene Content: добавлен Choice option.",
                    "node=" + nodeId + "; choice=" + choiceId + "; option=" + option.Id +
                    "; socket=" + option.OutputSocketId);
                PostSceneGraph(nodeId);
                break;
            }

            case "scene_remove_choice_option":
            {
                var nodeId = Required(root, "nodeId");
                var choiceId = Required(root, "choiceId");
                var optionId = Required(root, "optionId");
                var removed = _sceneGraph.RemoveChoiceOption(nodeId, choiceId, optionId, out var error);

                if (!removed)
                    throw new InvalidOperationException(error ?? "Не удалось удалить Choice option.");

                AppLogger.Info(
                    "Scene Content: удалён Choice option.",
                    "node=" + nodeId + "; choice=" + choiceId + "; option=" + optionId);
                PostSceneGraph(nodeId);
                break;
            }

            case "scene_save":
                SaveScene(saveAs: false);
                break;

            case "scene_save_as":
                SaveScene(saveAs: true);
                break;

            case "scene_validate":
                PostSceneGraph();
                break;

            case "scene_layout":
            {
                if (_sceneGraph.Value.Graph.Nodes.Count == 0)
                    throw new InvalidOperationException("В Scene Graph нет нод для перестроения.");

                var moved = _sceneGraph.ApplyLayout();
                AppLogger.Info(
                    "Scene Graph: выполнено перестроение раскладки.",
                    "scene=" + _sceneGraph.Value.Id +
                    "; nodes=" + _sceneGraph.Value.Graph.Nodes.Count +
                    "; moved=" + moved);
                PostSceneGraph();
                break;
            }

            case "scene_add_node":
            {
                var node = _sceneGraph.AddNode(
                    String(root, "nodeType", "Dialogue"),
                    String(root, "title", "Dialogue"),
                    Number(root, "x", 420),
                    Number(root, "y", 120));

                AppLogger.Info(
                    "Scene Graph: добавлена нода.",
                    "id=" + node.NodeId + "; type=" + node.NodeType);
                PostSceneGraph(node.NodeId);
                break;
            }

            case "scene_update_node":
            {
                var nodeId = Required(root, "nodeId");
                var updated = _sceneGraph.UpdateNode(
                    nodeId,
                    String(root, "title", null),
                    NumberOrNull(root, "x"),
                    NumberOrNull(root, "y"),
                    Parameters(root, "parameters"));

                if (updated is null)
                    throw new InvalidOperationException("Нода не найдена: " + nodeId);

                PostSceneGraph(updated.NodeId);
                break;
            }

            case "scene_remove_node":
            {
                var nodeId = Required(root, "nodeId");
                if (!_sceneGraph.RemoveNode(nodeId))
                    throw new InvalidOperationException("Нода не найдена: " + nodeId);

                PostSceneGraph();
                break;
            }

            case "scene_connect":
            {
                var result = _sceneGraph.Connect(
                    Required(root, "fromNodeId"),
                    Required(root, "fromSocketId"),
                    Required(root, "toNodeId"),
                    Required(root, "toSocketId"));

                if (!result.Added)
                    throw new InvalidOperationException(result.Error ?? "Связь не создана.");

                PostSceneGraph();
                break;
            }

            case "scene_disconnect":
            {
                var connection = new SceneConnection(
                    Required(root, "fromNodeId"),
                    Required(root, "fromSocketId"),
                    Required(root, "toNodeId"),
                    Required(root, "toSocketId"));

                if (!_sceneGraph.Disconnect(connection))
                    throw new InvalidOperationException("Связь не найдена.");

                PostSceneGraph();
                break;
            }

            case "scene_undo":
                if (!_sceneGraph.Undo())
                    throw new InvalidOperationException("Нет изменений для отмены.");
                PostSceneGraph();
                break;

            case "scene_redo":
                if (!_sceneGraph.Redo())
                    throw new InvalidOperationException("Нет изменений для повтора.");
                PostSceneGraph();
                break;
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

            case "graph_open_scene":
            {
                var nodeId = Required(root, "nodeId");
                var sceneId = OptionalString(root, "sceneId");
                NavigationRequested?.Invoke(
                    this,
                    new EditorNavigationRequestEventArgs(
                        "graph_open_scene",
                        nodeId,
                        sceneId,
                        _questGraph.Value.Id,
                        _currentDefinitionPath));
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
                // Replace(QuestGraph) сбрасывает метаданные документа, поэтому
                // новый квест не унаследует Description/SceneIds предыдущего.
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

            case "graph_open_recent":
            {
                // Клик по элементу истории: путь уже проверен Host'ом при
                // формировании списка, но файл мог исчезнуть после этого.
                var recentPath = root.TryGetProperty("path", out var recentPathNode)
                    ? recentPathNode.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(recentPath) || !File.Exists(recentPath))
                {
                    AppLogger.Warn("История файлов: файл недоступен.", recentPath);
                    MessageBox.Show(
                        this,
                        "Файл больше недоступен:\r\n\r\n" + recentPath,
                        "Открытие ресурса",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    // Убираем битую запись из истории, чтобы она не всплывала снова.
                    RecentFileUnavailable?.Invoke(this, recentPath ?? string.Empty);
                    break;
                }

                OpenGraphResource(recentPath);
                break;
            }

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

            case "graph_layout":
            {
                if (_questGraph.Value.Nodes.Count == 0)
                {
                    throw new InvalidOperationException("В графе нет нод для перестроения.");
                }

                var moved = _questGraph.ApplyLayout();
                AppLogger.Info(
                    "Quest Graph: выполнено перестроение раскладки.",
                    $"nodes={_questGraph.Value.Nodes.Count}; moved={moved}");
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
            Filter = ResourceFileTypes.Filter(ResourceFileTypes.Get(".aqquest")),
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
                Filter = ResourceFileTypes.Filter(ResourceFileTypes.Get(".aqquest")),
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
        var document = JsonSerializer.Deserialize<QuestDefinitionDocument>(json, ResourceJsonFormat.Options)
            ?? throw new InvalidOperationException("Файл Quest Definition пуст или повреждён.");

        if (document.SchemaVersion != DefinitionSchemaVersion ||
            !string.Equals(document.Format, "aqquest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Файл не является поддерживаемым Quest resource. Format={document.Format}; schema={document.SchemaVersion}.");

        if (document.Definition?.Graph is null)
            throw new InvalidOperationException("В документе отсутствует Quest Graph.");

        // Replace(QuestDefinition), а не Replace(graph): иначе Description и
        // SceneIds из файла потеряются ещё при открытии и уйдут в следующее сохранение.
        _questGraph.Replace(document.Definition);
        _currentDefinitionPath = path;
        _lastDefinitionPath = path;
        SaveLastDefinitionPath();
        _documentDirty = false;
        UpdateWindowTitle();
        PostQuestGraph();
        RecentFileOpened?.Invoke(this, path);

        AppLogger.Info("Quest Graph: документ открыт.", $"path={path}; schema={document.SchemaVersion}");
    }

    private bool SaveGraph(bool saveAs)
    {
        var path = _currentDefinitionPath;

        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Сохранить Quest Definition",
                Filter = ResourceFileTypes.Filter(ResourceFileTypes.Get(".aqquest")),
                DefaultExt = "aqquest",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(path)
                    ? $"{SanitizeFileName(_questGraph.Value.Name)}.aqquest"
                    : Path.GetFileName(path)
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = dialog.FileName;
        }

        // Метаданные документа хранит стор, а не форма: Description и SceneIds
        // восстанавливаются из загруженного файла. Раньше здесь стояли
        // string.Empty и Array.Empty<string>(), поэтому каждое сохранение
        // затирало метаданные квеста.
        var definition = _questGraph.Definition;

        var document = new QuestDefinitionDocument(DefinitionSchemaVersion, "aqquest", definition);
        var output = ResourceJsonFormat.Serialize(document);
        File.WriteAllText(path!, output);

        _currentDefinitionPath = path;
        _lastDefinitionPath = path;
        SaveLastDefinitionPath();
        _documentDirty = false;
        UpdateWindowTitle();
        PostQuestGraph();
        RecentFileOpened?.Invoke(this, path!);

        AppLogger.Info("Quest Graph: документ сохранён.",
            $"path={_currentDefinitionPath}; schema={DefinitionSchemaVersion}; bytes={output.Length}");
        return true;
    }

    private void SaveLastDefinitionPath()
    {
        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { LastQuestDefinitionPath = _lastDefinitionPath });
    }

    private void OpenScene()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Открыть Scene Definition",
            Filter = ResourceFileTypes.Filter(ResourceFileTypes.Get(".aqscene")),
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_sceneDocument.LastPath) && File.Exists(_sceneDocument.LastPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_sceneDocument.LastPath);
            dialog.FileName = Path.GetFileName(_sceneDocument.LastPath);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK && ConfirmSceneSwitch())
            LoadSceneFromPath(dialog.FileName);
    }

    private void OpenLastScene()
    {
        if (string.IsNullOrWhiteSpace(_sceneDocument.LastPath) || !File.Exists(_sceneDocument.LastPath))
        {
            OpenScene();
            return;
        }

        if (ConfirmSceneSwitch())
            LoadSceneFromPath(_sceneDocument.LastPath);
    }

    private void LoadSceneFromPath(
        string path,
        string? navigationBackQuestNodeId = null,
        string? navigationBackQuestNodeTitle = null,
        string? navigationBackQuestId = null,
        string? navigationBackQuestPath = null)
    {
        var document = JsonSerializer.Deserialize<SceneDefinitionDocument>(
            File.ReadAllText(path),
            ResourceJsonFormat.Options) ?? throw new InvalidOperationException("Файл Scene Definition пуст или повреждён.");

        if (document.SchemaVersion != DefinitionSchemaVersion ||
            !string.Equals(document.Format, "aqscene", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Файл не является поддерживаемым Scene resource. Format=" +
                document.Format + "; schema=" + document.SchemaVersion + ".");

        _sceneCatalog.Upsert(document.Definition);
        _loadingScene = true;
        try
        {
            _sceneGraph.Replace(document.Definition);
        }
        finally
        {
            _loadingScene = false;
        }

        _sceneDocument.Opened(document.Definition.Id, path);
        _navigationBackQuestNodeId = navigationBackQuestNodeId;
        _navigationBackQuestNodeTitle = navigationBackQuestNodeTitle;
        _navigationBackQuestId = navigationBackQuestId;
        _navigationBackQuestPath = navigationBackQuestPath;
        SaveLastScenePath();
        UpdateWindowTitle();
        PostSceneCatalog();
        PostSceneGraph();
        AppLogger.Info(
            "Scene Editor: документ открыт.",
            "path=" + path + "; scene=" + document.Definition.Id + "; schema=" + document.SchemaVersion);
    }

    private bool SaveScene(bool saveAs)
    {
        var path = _sceneDocument.CurrentPath;

        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Сохранить Scene Definition",
                Filter = ResourceFileTypes.Filter(ResourceFileTypes.Get(".aqscene")),
                DefaultExt = "aqscene",
                AddExtension = true,
                FileName = string.IsNullOrWhiteSpace(path)
                    ? SanitizeFileName(_sceneGraph.Value.Title) + ".aqscene"
                    : Path.GetFileName(path)
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return false;

            path = dialog.FileName;
        }

        _sceneCatalog.Upsert(_sceneGraph.Value);
        var document = new SceneDefinitionDocument(DefinitionSchemaVersion, "aqscene", _sceneGraph.Value);
        var output = ResourceJsonFormat.Serialize(document);
        File.WriteAllText(path!, output);

        _sceneDocument.Saved(_sceneGraph.Value.Id, path!);
        SaveLastScenePath();
        UpdateWindowTitle();
        PostSceneCatalog();
        PostSceneGraph();
        RecentFileOpened?.Invoke(this, path!);
        AppLogger.Info(
            "Scene Editor: документ сохранён.",
            "path=" + _sceneDocument.CurrentPath + "; scene=" + _sceneGraph.Value.Id + "; bytes=" + output.Length);
        return true;
    }

    private void SaveLastScenePath()
    {
        if (string.IsNullOrWhiteSpace(_sceneDocument.LastPath))
            return;

        var preferences = AppUiPreferencesStore.Load();
        AppUiPreferencesStore.Save(preferences with { LastSceneDefinitionPath = _sceneDocument.LastPath });
    }

    private string? ResolveScenePath(string sceneId)
    {
        // Каталог ресурсов, а не BaseDirectory: см. AppPaths о single-file публикации.
        var path = Path.Combine(AppPaths.ResourceRoot, "scenes", sceneId + ".aqscene");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Подтверждение переключения на другой Quest-документ.
    ///
    /// Повторное открытие уже текущего файла не считается переключением: иначе
    /// двойной клик по открытому квесту показывал бы диалог о несохранённых
    /// изменениях и перезагружал документ, теряя выделение и позицию.
    /// </summary>
    private bool ConfirmGraphSwitch(string targetPath)
    {
        if (IsSameDocument(_currentDefinitionPath, targetPath))
            return false;

        if (!_documentDirty)
            return true;

        var result = MessageBox.Show(
            this,
            "В текущем Quest Graph есть несохранённые изменения.\r\n\r\n" +
            "Да — сохранить изменения в файл.\r\n" +
            "Нет — отбросить изменения и открыть другой документ.\r\n" +
            "Отмена — остаться в текущем документе.",
            "Несохранённые изменения Quest Graph",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        if (result == DialogResult.Cancel)
            return false;

        if (result == DialogResult.Yes)
            return SaveGraph(saveAs: false);

        return true;
    }

    /// <summary>
    /// Подтверждение переключения на другую Scene.
    /// Повторное открытие текущего файла не является переключением.
    /// </summary>
    private bool ConfirmSceneSwitch(string? targetPath = null)
    {
        if (targetPath is not null && IsSameDocument(_sceneDocument.CurrentPath, targetPath))
            return false;

        if (!_sceneDocument.IsDirty)
            return true;

        var result = MessageBox.Show(
            this,
            "В текущей Scene есть несохранённые изменения.\r\n\r\n" +
            "Да — сохранить текущую Scene.\r\n" +
            "Нет — отбросить изменения и продолжить.\r\n" +
            "Отмена — остаться в текущей Scene.",
            "Несохранённые изменения Scene",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        if (result == DialogResult.Cancel)
            return false;

        if (result == DialogResult.Yes)
            return SaveScene(saveAs: false);

        _sceneDocument.DiscardChanges();
        return true;
    }

    /// <summary>
    /// Сравнивает пути документов как файловую систему: Windows не различает
    /// регистр, а «относительный» и «полный» путь указывают на один файл.
    /// </summary>
    private static bool IsSameDocument(string? currentPath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || string.IsNullOrWhiteSpace(targetPath))
            return false;

        return string.Equals(
            Path.GetFullPath(currentPath),
            Path.GetFullPath(targetPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private void PostSceneCatalog()
    {
        if (!_isSceneEditor || Browser.CoreWebView2 is null)
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "scene_catalog",
            scenes = _sceneCatalog.Scenes.Select(scene => new
            {
                id = scene.Id,
                title = scene.Title
            }).ToArray()
        }, WebJsonOptions));
    }

    private void PostSceneGraph(string? selectedNodeId = null)
    {
        if (!_isSceneEditor || Browser.CoreWebView2 is null)
            return;

        PostJson(JsonSerializer.Serialize(new
        {
            type = "scene_definition",
            definition = _sceneGraph.Value,
            selectedNodeId,
            canUndo = _sceneGraph.CanUndo,
            canRedo = _sceneGraph.CanRedo,
            validation = SceneGraphValidator.Validate(_sceneGraph.Value),
            documentPath = _sceneDocument.CurrentPath ?? string.Empty,
            lastDocumentPath = _sceneDocument.LastPath ?? string.Empty,
            documentDirty = _sceneDocument.IsDirty,
            navigationBack = string.IsNullOrWhiteSpace(_navigationBackQuestNodeId)
                ? null
                : new
                {
                    nodeId = _navigationBackQuestNodeId,
                    nodeTitle = _navigationBackQuestNodeTitle,
                    questId = _navigationBackQuestId,
                    questPath = _navigationBackQuestPath
                }
        }, WebJsonOptions));
    }

    private void SceneGraph_Changed(object? sender, EventArgs e)
    {
        if (!_isSceneEditor)
            return;

        if (!_loadingScene)
        {
            _sceneDocument.ObserveScene(
                _sceneGraph.Value.Id,
                ResolveScenePath(_sceneGraph.Value.Id));
        }

        UpdateWindowTitle();
        PushSceneGraphOnUiThread();
    }

    private void SceneDocument_Changed(object? sender, EventArgs e)
    {
        if (!_isSceneEditor)
            return;

        UpdateWindowTitle();
        PushSceneGraphOnUiThread();
    }

    private void PushSceneGraphOnUiThread()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke((Action)(() => PostSceneGraph()));
        }
        catch (InvalidOperationException)
        {
        }
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
        if (_isGraphEditor)
        {
            var fileName = string.IsNullOrWhiteSpace(_currentDefinitionPath)
                ? "Новый документ"
                : Path.GetFileName(_currentDefinitionPath);
            Text = $"Нодовый редактор — {fileName}" + (_documentDirty ? " *" : string.Empty);
            return;
        }

        if (_isSceneEditor)
        {
            var fileName = string.IsNullOrWhiteSpace(_sceneDocument.CurrentPath)
                ? "Новый документ"
                : Path.GetFileName(_sceneDocument.CurrentPath);
            var prefix = _isDialogueWorkspace
                ? "Рабочее пространство диалогов"
                : "Редактор сцен";
            Text = $"{prefix} — {fileName}" + (_sceneDocument.IsDirty ? " *" : string.Empty);
        }
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
            sceneCatalog = _sceneCatalog.Scenes.Select(scene => new
            {
                id = scene.Id,
                title = scene.Title
            }).ToArray(),
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


    private static IReadOnlyDictionary<string, string> ChoiceOptionTexts(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = item.TryGetProperty("id", out var idElement)
                ? idElement.ToString()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var text = item.TryGetProperty("text", out var textElement)
                ? textElement.ToString()
                : string.Empty;

            result[id] = text;
        }

        return result;
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
