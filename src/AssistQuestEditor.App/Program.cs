using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var hub = new SimulatorDataChannelHub();
        using var mainForm = new MainForm(hub);
        Application.Run(mainForm);
    }
}
