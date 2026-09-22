namespace AssistQuestEditor.App;

/// <summary>
/// Small one-shot startup dialog for campaign installation/update operations.
/// The user only needs to acknowledge the list; detailed diagnostics remain in logs.
/// </summary>
public sealed class CampaignSyncNoticeForm : Form
{
    public CampaignSyncNoticeForm(IReadOnlyList<string> changes)
    {
        Text = "Assist Quest Editor — обновление квестов";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 560);
        MinimumSize = new Size(600, 420);
        BackColor = Color.FromArgb(16, 18, 23);
        ForeColor = Color.FromArgb(231, 237, 244);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(18, 16, 18, 8),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(231, 237, 244),
            Text = "Изменения в установленных кампаниях"
        };

        var body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(10, 12, 16),
            ForeColor = Color.FromArgb(231, 237, 244),
            Font = new Font("Segoe UI", 10f),
            Text = string.Join(Environment.NewLine + Environment.NewLine, changes.Select((item, index) =>
                (index + 1).ToString() + ". " + item))
        };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(12, 8, 12, 10),
            BackColor = Color.FromArgb(22, 25, 31)
        };

        var ok = new Button
        {
            Text = "ОК",
            AutoSize = true,
            MinimumSize = new Size(110, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            DialogResult = DialogResult.OK
        };
        ok.Location = new Point(
            footer.ClientSize.Width - ok.Width - 8,
            footer.Padding.Top);
        ok.Click += (_, _) => Close();
        footer.Resize += (_, _) =>
        {
            ok.Left = footer.ClientSize.Width - ok.Width - footer.Padding.Right;
            ok.Top = footer.Padding.Top;
        };

        footer.Controls.Add(ok);
        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(title);

        AcceptButton = ok;
    }
}
