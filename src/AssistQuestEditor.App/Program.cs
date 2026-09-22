using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppLogger.Info("=== Assist Quest Editor START ===",
            $"Version={VersionInfo.InformationalVersion}; BaseDirectory={AppContext.BaseDirectory}; CurrentDirectory={Environment.CurrentDirectory}; LogPath={AppLogger.LogPath}");

        SplashForm? splash = null;
        MainForm? mainForm = null;

        try
        {
            ApplicationConfiguration.Initialize();

            if (args.Any(arg =>
                    string.Equals(arg, "--unregister-file-associations", StringComparison.OrdinalIgnoreCase)))
            {
                FileAssociationRegistry.Unregister();
                return;
            }

            var startupFile = args.FirstOrDefault(arg =>
                !arg.StartsWith("--", StringComparison.Ordinal) &&
                File.Exists(arg));

            if (startupFile is not null)
                FileActivationRequest.Set(startupFile);

            var splashPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SplashScreen.png");
            AppLogger.Info("Splash: подготовка.", $"path={splashPath}; exists={File.Exists(splashPath)}");
            splash = new SplashForm(splashPath);
            splash.Show();
            splash.Refresh();
            Application.DoEvents();

            AppLogger.Info("Загрузка СДО world data.");
            var sdoPoints = SdoWorldDataLoader.Load();
            var cityPoints = CityWorldDataLoader.Load();
            var worldPoints = sdoPoints.Concat(cityPoints).ToArray();
            AppLogger.Info("World data загружены.", $"sdo={sdoPoints.Count}; cities={cityPoints.Count}; total={worldPoints.Length}");

            var simulatorAdapter = new SimulatorDataSourceAdapter(worldPoints);
            var worldCount = simulatorAdapter.Channels.Get<WorldState>("world").Value.Points.Count;
            AppLogger.Info("Создан SimulatorDataSourceAdapter.",
                $"channels={simulatorAdapter.Channels.Describe().Count}; points={worldCount}");

            mainForm = new MainForm(simulatorAdapter.Channels)
            {
                Opacity = 0
            };

            void RevealMainWindow(string reason)
            {
                if (mainForm.IsDisposed)
                    return;

                mainForm.BeginInvoke(() =>
                {
                    if (mainForm.IsDisposed)
                        return;

                    mainForm.Opacity = 1;
                    mainForm.Activate();

                    if (splash is not null && !splash.IsDisposed)
                    {
                        splash.Close();
                        splash.Dispose();
                        splash = null;
                    }

                    AppLogger.Info("Splash: завершён.", reason);
                });
            }

            mainForm.BrowserReady += (_, _) => RevealMainWindow("Основной WebView2 готов.");
            mainForm.BrowserFailed += (_, _) => RevealMainWindow("Основной WebView2 не загрузился; показана страница ошибки.");

            AppLogger.Info("MainForm создан. Запуск Application.Run().");
            Application.Run(mainForm);
            AppLogger.Info("Application.Run завершён.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Необработанное исключение верхнего уровня.", ex);
            splash?.Close();
            splash?.Dispose();
            mainForm?.Dispose();

            MessageBox.Show(
                ex.ToString(),
                "Assist Quest Editor — ошибка запуска",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (splash is not null && !splash.IsDisposed)
            {
                splash.Close();
                splash.Dispose();
            }

            AppLogger.Info("=== Assist Quest Editor END ===");
        }
    }
}
