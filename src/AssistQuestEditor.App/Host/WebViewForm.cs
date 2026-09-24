using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AssistQuestEditor.App;

public abstract class WebViewForm : Form
{
    private readonly string _page;
    private bool _browserReadyRaised;

    public event EventHandler? BrowserReady;
    public event EventHandler? BrowserFailed;
    protected readonly WebView2 Browser;

    protected WebViewForm(string title, string page, Size initialSize, string? windowKey = null)
    {
        Text = title;
        _page = page;
        StartPosition = FormStartPosition.Manual;
        Size = initialSize;
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(10, 12, 16);

        Browser = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(10, 12, 16),
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = GetWebViewUserDataFolder()
            }
        };

        Controls.Add(Browser);
        WindowGeometryStore.Attach(this, windowKey ?? page);
        Browser.NavigationCompleted += Browser_NavigationCompleted;
        Load += HandleLoad;
    }

    private async void HandleLoad(object? sender, EventArgs e)
    {
        try
        {
            AppLogger.Info("WebView2: начало инициализации.", $"form={Text}; page={_page}");
            _browserReadyRaised = false;
            await Browser.EnsureCoreWebView2Async();
            AppLogger.Info("WebView2: CoreWebView2 создан.", $"browser={Browser.CoreWebView2.Environment.BrowserVersionString}");
            Browser.WebMessageReceived += Browser_WebMessageReceived;

            var hashIndex = _page.IndexOf("#");
            var fileName = hashIndex >= 0 ? _page[..hashIndex] : _page;
            var fragment = hashIndex >= 0 ? _page[(hashIndex + 1)..] : string.Empty;
            var path = Path.Combine(AppContext.BaseDirectory, "Web", fileName);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Web-страница не найдена в опубликованном приложении.", path);
            }

            var uri = new Uri(path).AbsoluteUri;
            uri += "?v=" + Uri.EscapeDataString(VersionInfo.NumericVersion);
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                uri += "#" + fragment;
            }

            AppLogger.Info("WebView2: Navigate.", $"form={Text}; uri={uri}; sourceExists={File.Exists(path)}; sourceBytes={new FileInfo(path).Length}");
            Browser.CoreWebView2.Navigate(uri);
        }
        catch (Exception ex)
        {
            ShowWebViewError(ex);
        }
    }

    private void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        AppLogger.Info("WebView2: NavigationCompleted.", $"form={Text}; success={e.IsSuccess}; error={e.WebErrorStatus}");
        if (IsDisposed || _browserReadyRaised)
        {
            return;
        }

        if (!e.IsSuccess)
        {
            ShowWebViewError(new InvalidOperationException(
                "Web-страница не загрузилась. Код навигации: " + e.WebErrorStatus));
            return;
        }

        _browserReadyRaised = true;
        AppLogger.Info("WebView2: browser ready.", $"form={Text}; page={_page}");
        OnBrowserReady();
        BrowserReady?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void OnBrowserReady()
    {
    }

    protected virtual void OnWebMessage(string json)
    {
    }

    private void Browser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var action = root.TryGetProperty("action", out var actionNode) ? actionNode.GetString() : null;
            if (string.Equals(action, "web_log", StringComparison.OrdinalIgnoreCase))
            {
                var level = root.TryGetProperty("level", out var levelNode) ? levelNode.GetString() : "INFO";
                var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : "Web log";
                var details = root.TryGetProperty("details", out var detailsNode)
                    ? FormatWebLogDetails(detailsNode)
                    : null;
                var logMessage = "WEB: " + (message ?? "Web info");

                if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                    AppLogger.Error(logMessage, details: details);
                else if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
                    AppLogger.Warn(logMessage, details);
                else
                    AppLogger.Info(logMessage, details);

                if (Text.Contains("Симулятор", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                        QuestLogger.Error(logMessage, details: details);
                    else if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
                        QuestLogger.Warn(logMessage, details);
                    else
                        QuestLogger.Info(logMessage, details);
                }
                return;
            }

            if (string.Equals(action, "quest_log", StringComparison.OrdinalIgnoreCase))
            {
                var level = root.TryGetProperty("level", out var levelNode) ? levelNode.GetString() : "INFO";
                var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : "Quest UI log";
                var details = root.TryGetProperty("details", out var detailsNode)
                    ? FormatWebLogDetails(detailsNode)
                    : null;

                if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                    QuestLogger.Error(message ?? "Quest UI error", details: details);
                else if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase))
                    QuestLogger.Warn(message ?? "Quest UI warning", details);
                else
                    QuestLogger.Info(message ?? "Quest UI info", details);
                return;
            }

            var actionDetails = "form=" + Text + "; action=" + (action ?? "<none>");
            AppLogger.Info("WebView2: получено действие.", actionDetails);
            if (Text.Contains("Симулятор", StringComparison.OrdinalIgnoreCase))
                QuestLogger.Info("Simulator WebView action.", actionDetails);
        }
        catch (Exception ex)
        {
            AppLogger.Error("WebView2: ошибка разбора web message.", ex);
        }

        OnWebMessage(e.WebMessageAsJson);
    }

    private static string FormatWebLogDetails(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? string.Empty;

        try
        {
            using var document = JsonDocument.Parse(element.GetRawText());
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }
        catch (JsonException)
        {
            return element.GetRawText();
        }
    }

    protected void PostJson(string json)
    {
        if (IsDisposed || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string GetWebViewUserDataFolder()
    {
        // Профиль WebView2 остаётся в AppData (см. AppPaths): это технические
        // данные, привязанные к машине, и переносить их в Документы нельзя.
        return AppPaths.WebViewUserDataRoot;
    }

    private void ShowWebViewError(Exception ex)
    {
        BrowserFailed?.Invoke(this, EventArgs.Empty);
        Browser.Visible = false;
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(10, 12, 16),
            Padding = new Padding(32)
        };

        var label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 12f),
            Text = "Не удалось открыть Web-интерфейс.\r\n\r\n" +
                   ex.Message + "\r\n\r\n" +
                   "Проверьте содержимое папки Web в опубликованном приложении."
        };

        panel.Controls.Add(label);
        Controls.Add(panel);
        panel.BringToFront();
    }
}
