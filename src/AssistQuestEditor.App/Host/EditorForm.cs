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

    public EditorForm(string title, string page, IDataChannelHub hub)
        : base(
            $"Assist Quest Editor — {title}",
            page,
            new Size(1380, 900))
    {
        _player = hub.Get<PlayerState>("player");
        _selection = hub.Get<WorldSelectionState>("world-selection");

        _player.Changed += Player_Changed;
        _selection.Changed += Selection_Changed;

        FormClosed += (_, _) =>
        {
            _player.Changed -= Player_Changed;
            _selection.Changed -= Selection_Changed;
        };
    }

    protected override void OnBrowserReady()
    {
        PostSimulatorContext();
    }

    protected override void OnWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!string.Equals(root.GetProperty("action").GetString(), "coordinate_request", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var source = root.TryGetProperty("source", out var sourceNode)
                ? sourceNode.GetString()
                : null;

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
                    point = point,
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
        catch (Exception ex)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "coordinate_error",
                message = ex.Message
            }, WebJsonOptions));
        }
    }

    private void Player_Changed(object? sender, EventArgs e)
    {
        PushContextOnUiThread();
    }

    private void Selection_Changed(object? sender, EventArgs e)
    {
        PushContextOnUiThread();
    }

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
}
