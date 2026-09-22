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
        SingleInstanceGuard? singleInstance = null;

        try
        {
            ApplicationConfiguration.Initialize();

            if (args.Any(arg =>
                    string.Equals(arg, "--unregister-file-associations", StringComparison.OrdinalIgnoreCase)))
            {
                FileAssociationRegistry.Unregister();
                return;
            }

            // Проверка ресурсов из командной строки: нужна сборке и диагностике,
            // где нет GUI и нельзя показать диалог. Код возврата: 0 — целостность
            // подтверждена, 2 — есть расхождения, 3 — проверку выполнить не удалось.
            if (args.Any(arg => string.Equals(arg, "--verify-resources", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = VerifyResourcesFromCommandLine();
                return;
            }

            var startupFile = args.FirstOrDefault(arg =>
                !arg.StartsWith("--", StringComparison.Ordinal) &&
                File.Exists(arg));

            // Единственный экземпляр. Если приложение уже запущено, второй процесс
            // передаёт путь работающему окну и завершается, не создавая второго.
            if (!SingleInstanceGuard.TryAcquire(startupFile, out var acquired))
            {
                AppLogger.Info("Запуск отменён: приложение уже работает, запрос передан.");
                return;
            }

            singleInstance = acquired;

            if (startupFile is not null)
                FileActivationRequest.Set(startupFile);

            var splashPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SplashScreen.png");
            AppLogger.Info("Splash: подготовка.", $"path={splashPath}; exists={File.Exists(splashPath)}");
            splash = new SplashForm(splashPath);
            splash.Show();
            splash.Refresh();
            Application.DoEvents();

            // Ресурсы проверяются до создания окон. Запускать игру на расходящейся
            // папке хуже, чем отказаться от старта: непонятная ошибка в квесте
            // выглядит как баг редактора, хотя причина в несовместимом файле.
            if (!VerifyResourcesAtStartup())
            {
                return;
            }

            // До создания MainForm синхронизируем bundled campaign store с
            // постоянным пользовательским хранилищем. Это гарантирует, что
            // Runtime и редакторы увидят одну и ту же установленную версию.
            var campaignSync = CampaignInstaller.Synchronize();
            if (campaignSync.UserVisibleChanges.Count > 0)
            {
                using var notice = new CampaignSyncNoticeForm(campaignSync.UserVisibleChanges);
                notice.ShowDialog();
            }

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

            // Запросы от последующих запусков приходят в фоновом потоке канала,
            // поэтому обработка переводится в UI-поток.
            singleInstance.ActivationRequested += (_, path) =>
            {
                if (mainForm.IsDisposed)
                    return;

                mainForm.BeginInvoke(() => mainForm.HandleExternalActivation(path));
            };
            singleInstance.StartListening();

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

            // Освобождаем мьютекс и останавливаем слушатель канала: иначе
            // следующий запуск решил бы, что приложение всё ещё работает.
            singleInstance?.Dispose();

            AppLogger.Info("=== Assist Quest Editor END ===");
        }
    }

    /// <summary>
    /// Проверяет ресурсы при запуске и объясняет причину, если они разошлись.
    ///
    /// Молчаливое продолжение здесь опаснее отказа: старый или несовместимый
    /// resource-файл проявится как непонятная ошибка где-то в редакторе. Поэтому
    /// при расхождении показывается список проблем и запуск прекращается.
    ///
    /// Возвращает true, если можно продолжать. Отсутствие манифеста не блокирует
    /// запуск: так работают отладочная сборка и запуск из каталога сборки, где
    /// каталог ресурсов создаётся целью синхронизации при обычной сборке.
    /// </summary>
    private static bool VerifyResourcesAtStartup()
    {
        var resourceRoot = AppPaths.ResourceRoot;

        ResourceIntegrityResult result;
        try
        {
            result = ResourceIntegrityChecker.Verify(resourceRoot);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Проверка ресурсов при запуске не удалась.", ex, "root=" + resourceRoot);
            return true;
        }

        if (result.Ok)
        {
            AppLogger.Info("Ресурсы при запуске проверены.", result.Summary);
            return true;
        }

        // Отсутствие манифеста означает «каталог не публиковался», а не порчу
        // ресурсов: блокировать запуск из-за этого нельзя.
        var onlyManifestMissing = result.Issues.All(issue =>
            issue.Kind == ResourceIssueKind.ManifestMissing);

        foreach (var line in ResourceIntegrityChecker.FormatReport(result, resourceRoot))
        {
            AppLogger.Warn("Ресурсы при запуске: " + line);
        }

        if (onlyManifestMissing)
        {
            return true;
        }

        MessageBox.Show(
            "Ресурсы не совпадают с манифестом сборки:\r\n\r\n" +
            string.Join("\r\n", result.Issues.Select(issue => "• " + issue.Message)) +
            "\r\n\r\nКаталог: " + resourceRoot +
            "\r\n\r\nВосстановите ресурсы командой compile.ps1 (или ci\\sync_data_resources.ps1).",
            "Assist Quest Editor — ресурсы устарели",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        return false;
    }

    /// <summary>
    /// Проверяет ресурсы публикации по манифесту для неинтерактивного режима.
    ///
    /// Вывод идёт и в stdout, и в файл рядом с манифестом: приложение собрано как
    /// WinExe без консоли, поэтому stdout из вызывающего процесса (MSBuild) не
    /// читается. Файл отчёта — канал, который видит и сборка, и человек.
    /// Код возврата: 0 — целостность подтверждена, 2 — ресурсы разошлись,
    /// 3 — проверку выполнить не удалось.
    /// </summary>
    private static int VerifyResourcesFromCommandLine()
    {
        // Отчёт пишется рядом с EXE, а не в BaseDirectory: последний в single-file
        // публикации указывает на кэш распаковки, где файл не найдёт ни сборка,
        // ни человек.
        var appDirectory = ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        var reportPath = Path.Combine(appDirectory, "data-verify-report.txt");
        try
        {
            // Тот же каталог, что использует приложение: иначе проверка смотрела бы
            // в кэш распаковки single-file и сообщала о расхождении там, где
            // опубликованная папка рядом с EXE в порядке.
            var resourceRoot = AppPaths.ResourceRoot;
            var result = ResourceIntegrityChecker.Verify(resourceRoot);
            var lines = ResourceIntegrityChecker.FormatReport(result, resourceRoot);

            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }

            File.WriteAllLines(reportPath, lines);
            return result.Ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            // Отсутствие манифеста и недоступный каталог — тоже результат проверки,
            // но отличный от «ресурсы разошлись»: код 3 говорит, что проверить не удалось.
            var message = "Проверку ресурсов выполнить не удалось: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                File.WriteAllLines(reportPath, new[] { message });
            }
            catch (IOException)
            {
                // Отчёт — диагностика: если его не записать, это не меняет вердикт.
            }

            return 3;
        }
    }
}
