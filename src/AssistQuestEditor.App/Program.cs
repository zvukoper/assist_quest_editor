using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppLogger.Info("=== Assist Quest Editor START ===",
            $"Version={VersionInfo.InformationalVersion}; BaseDirectory={AppContext.BaseDirectory}; CurrentDirectory={Environment.CurrentDirectory}; LogPath={AppLogger.LogPath}");

        try
        {
            ApplicationConfiguration.Initialize();

            AppLogger.Info("Загрузка СДО world data.");
            var worldPoints = SdoWorldDataLoader.Load();
            AppLogger.Info("СДО world data загружены.", $"points={worldPoints.Count}");

            var simulatorAdapter = new SimulatorDataSourceAdapter(worldPoints);
            var worldCount = simulatorAdapter.Channels.Get<WorldState>("world").Value.Points.Count;
            AppLogger.Info("Создан SimulatorDataSourceAdapter.",
                $"channels={simulatorAdapter.Channels.Describe().Count}; points={worldCount}");

            using var mainForm = new MainForm(simulatorAdapter.Channels);
            AppLogger.Info("MainForm создан. Запуск Application.Run().");
            Application.Run(mainForm);
            AppLogger.Info("Application.Run завершён.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Необработанное исключение верхнего уровня.", ex);
            MessageBox.Show(
                ex.ToString(),
                "Assist Quest Editor — ошибка запуска",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            AppLogger.Info("=== Assist Quest Editor END ===");
        }
    }
}
