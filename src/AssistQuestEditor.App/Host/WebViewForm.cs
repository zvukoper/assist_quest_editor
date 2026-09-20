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
            Browser.CoreWebView2.Navigate(new Uri(GetPagePath()).AbsoluteUri);
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

    protected string GetPagePath() =>
        Path.Combine(AppContext.BaseDirectory, "Web", _page);

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
            Text = "Не удалось запустить WebView2.\r\n\r\n" +
                   ex.Message + "\r\n\r\n" +
                   "Для работы редактора требуется установленный Microsoft Edge WebView2 Runtime."
        };

        panel.Controls.Add(label);
        Controls.Add(panel);
        panel.BringToFront();
    }
}
