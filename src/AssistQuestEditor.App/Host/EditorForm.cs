namespace AssistQuestEditor.App;

public sealed class EditorForm : WebViewForm
{
    public EditorForm(string title, string page)
        : base(
            $"Assist Quest Editor — {title}",
            page,
            new Size(1380, 900))
    {
    }
}
