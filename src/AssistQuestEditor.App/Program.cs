using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var worldPoints = SdoWorldDataLoader.Load();
        var simulatorAdapter = new SimulatorDataSourceAdapter(worldPoints);
        using var mainForm = new MainForm(simulatorAdapter.Channels);
        Application.Run(mainForm);
    }
}
