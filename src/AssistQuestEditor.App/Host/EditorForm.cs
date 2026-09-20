using System.Globalization;
using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class EditorForm : WebViewForm
{
    private static readonly JsonSerializerOptions WebJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDataChannel<PlayerState> _player;
    private readonly IDataChannel<WorldSelectionState> _selection;
    private readonly QuestGraphStore _questGraph;
    private readonly bool _isGraphEditor;

    public EditorForm(string title, string page, IDataChannelHub hub, QuestGraphStore questGraph)
        : base($"Assist Quest Editor — {title}", page, new Size(1380, 900))
    {
        _player = hub.Get<PlayerState>("player");
        _selection = hub.Get<WorldSelectionState>("world-selection");
        _questGraph = questGraph;
        _isGraphEditor = page.EndsWith("#graph", StringComparison.OrdinalIgnoreCase);

        _player.Changed += Player_Changed;
        _selection.Changed += Selection_Changed;
        _questGraph.Changed += QuestGraph_Changed;

        FormClosed += (_, _) =>
        {
            _player.Changed -= Player_Changed;
            _selection.Changed -= Selection_Changed;
            _questGraph.Changed -= QuestGraph_Changed;
        };
    }

    protected override void OnBrowserReady()
    {
        PostSimulatorContext();
        if (_isGraphEditor)
        {
            PostQuestGraph();
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
                    NumberOrNull(root, "y"));

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
        }
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
            selectedNodeId
        }, WebJsonOptions));
    }

    private void QuestGraph_Changed(object? sender, EventArgs e)
    {
        if (_isGraphEditor)
        {
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

    private static string String(JsonElement root, string name, string? fallback)
    {
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind != JsonValueKind.Null &&
               element.ValueKind != JsonValueKind.Undefined
            ? element.ToString()
            : fallback ?? string.Empty;
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
