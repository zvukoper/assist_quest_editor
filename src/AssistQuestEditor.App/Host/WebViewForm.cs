using Microsoft.Web.WebView2.WinForms;

namespace AssistQuestEditor.App;

public abstract class WebViewForm : Form
{
    private readonly string _page;
    protected readonly WebView2 Browser;

    protected WebViewForm(string title, string page, Size initialSize)
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
            DefaultBackgroundColor = Color.FromArgb(10, 12, 16)
        };

        Controls.Add(Browser);
        Load += HandleLoad;
    }

    private async void HandleLoad(object? sender, EventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.WebMessageReceived += Browser_WebMessageReceived;

            var hashIndex = _page.IndexOf('#');
            var fileName = hashIndex >= 0 ? _page[..hashIndex] : _page;
            var fragment = hashIndex >= 0 ? _page[(hashIndex + 1)..] : string.Empty;
            var path = Path.Combine(AppContext.BaseDirectory, "Web", fileName);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Web-страница не найдена в опубликованном приложении.", path);
            }

            var uri = new Uri(path).AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                uri += "#" + fragment;
            }

            Browser.CoreWebView2.Navigate(uri);
            OnBrowserReady();
        }
        catch (Exception ex)
        {
            ShowWebViewError(ex);
        }
    }

    protected virtual void OnBrowserReady()
    {
    }

    protected virtual void OnWebMessage(string json)
    {
    }

    private void Browser_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        OnWebMessage(e.WebMessageAsJson);
    }

    protected void PostJson(string json)
    {
        if (IsDisposed || Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void ShowWebViewError(Exception ex)
    {
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
