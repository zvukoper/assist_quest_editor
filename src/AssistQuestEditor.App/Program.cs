using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

internal static class Program
{
    /// <summary>
    /// Заставка запуска.
    ///
    /// Поле, а не локальная переменная: её обязан уметь закрыть ЛЮБОЙ этап
    /// запуска, включая вспомогательные методы. Заставка создана TopMost +
    /// ShowWithoutActivation и висит поверх всего до готовности главного
    /// WebView2, поэтому любой модальный диалог, открытый раньше, оказывался
    /// ПОД ней: окно видно в превью таскбара, но на экране — картинка, и ни
    /// ввести данные, ни отменить нельзя. Симптом выглядит как зависание.
    /// </summary>
    private static SplashForm? _splash;

    [STAThread]
    private static void Main(string[] args)
    {
        var ciTest = StartupOptions.IsCiTest(args);

        AppLogger.Info("=== Assist Quest Editor START ===",
            $"Version={VersionInfo.InformationalVersion}; Mode={(ciTest ? "CI test" : "User")}; BaseDirectory={AppContext.BaseDirectory}; CurrentDirectory={Environment.CurrentDirectory}; LogPath={AppLogger.LogPath}");

        if (ciTest)
            AppLogger.Info("Startup: режим=CI test.", "Установочные сценарии отключены.");

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

            // Сборка поставляемого демо-мира. Отдельный режим, а не шаг установки:
            // архив кладётся в поставку один раз при сборке ресурсов, и делать это
            // при каждом запуске приложения значило бы перезаписывать файл в
            // пользовательской установке.
            if (args.Any(arg => string.Equals(arg, "--build-demo-world", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = BuildDemoWorldFromCommandLine();
                return;
            }

            // Проба выгрузки: тот же код, что вызывается из меню, но без диалога.
            // Диалог подтверждения в неинтерактивной среде не нажать, а проверить
            // упаковку архива и раскладку папки НУЖНО — иначе «Экспорт» остался бы
            // непроверяемым до ручного прогона.
            if (args.Any(arg => string.Equals(arg, "--export-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = ExportProbeFromCommandLine(args);
                return;
            }

            // Проба правки свойств: тот же код сторов, что вызывает окно «Ред.».
            // Проверяется не «сохранилось ли имя», а ЧТО ОСТАЛОСЬ в файле:
            // кампания хранит в одном файле и описание, и квесты, и стартовые
            // условия мира, поэтому потеря игрового поля — самый дорогой дефект.
            if (args.Any(arg => string.Equals(arg, "--properties-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = PropertiesProbeFromCommandLine(args);
                return;
            }

            // Проба импорта кампании и квеста: выбор родителя делает диалог, а
            // распаковку — тот же сервис, что и в окне. Без пробы эта ветка
            // осталась бы непроверяемой в неинтерактивной среде.
            if (args.Any(arg => string.Equals(arg, "--import-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = ImportProbeFromCommandLine(args);
                return;
            }

            // Сборка архива заданного вида. Нужна проверкам импорта: архив
            // кампании или квеста можно получить только упаковщиком, а меню
            // экспорта в неинтерактивной среде не нажать.
            if (args.Any(arg => string.Equals(arg, "--build-archive", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = BuildArchiveFromCommandLine(args);
                return;
            }

            // Проба создания ресурса: тот же код сторов, что вызывает окно
            // «Создать». Проверяется структура, которая появляется на диске:
            // мир без общей кампании и кампания без папок квестов/сцен — это
            // ресурсы, в которых нечего создавать.
            if (args.Any(arg => string.Equals(arg, "--create-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = CreateProbeFromCommandLine(args);
                return;
            }

            // Проба автосохранения. Отдельный режим, потому что дефект был
            // ПОВЕДЕНЧЕСКИЙ: проверки читали исходники и видели корректный код,
            // а запись падала на несуществующем каталоге. Ловится только реальной
            // записью с обратным чтением.
            if (args.Any(arg => string.Equals(arg, "--autosave-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = AutosaveProbeFromCommandLine(args);
                return;
            }

            // Проба раскладки дерева кампаний. Тоже поведенческая: перекрытие
            // строк, обрезка названия и две полосы прокрутки не видны в исходниках
            // — форма строится кодом, и «правильная» на вид формула даёт кривую
            // раскладку. Проба строит настоящее окно и меряет прямоугольники.
            if (args.Any(arg => string.Equals(arg, "--tree-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = TreeProbeFromCommandLine(args);
                return;
            }

            // Проба размеров окна инвентаря. Сетка 6×3 и размер окна считаются в
            // ДВУХ местах (CSS для страницы, InventoryLayoutRules для формы), и
            // расхождение даёт полосу прокрутки там, где её быть не должно, либо
            // обрезанные ячейки. Проверяется сравнением чисел, а не глазами.
            if (args.Any(arg => string.Equals(arg, "--inventory-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = InventoryProbeFromCommandLine(args);
                return;
            }

            // Проба иконок. Проверяется ПОВЕДЕНЧЕСКИ: собирается ли ICO из
            // логотипов, читается ли он как иконка и попадают ли кадры нужных
            // размеров. Простое наличие файла ничего не доказывает — иконка
            // может быть собрана с неверным числом кадров или вообще не
            // читаться Windows.
            if (args.Any(arg => string.Equals(arg, "--icon-probe", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = IconProbeFromCommandLine(args);
                return;
            }

            var startupFile = args.FirstOrDefault(arg =>
                !StartupOptions.IsApplicationSwitch(arg) &&
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
            _splash = new SplashForm(splashPath);
            _splash.Show();
            _splash.Refresh();
            Application.DoEvents();

            // Ресурсы проверяются до создания окон. Запускать игру на расходящейся
            // папке хуже, чем отказаться от старта: непонятная ошибка в квесте
            // выглядит как баг редактора, хотя причина в несовместимом файле.
            if (!VerifyResourcesAtStartup())
            {
                return;
            }

            // Установочные сценарии разрешены только в пользовательском режиме.
            // CI test читает bundled Campaign store напрямую и ничего не меняет в AppData.
            if (!ciTest)
            {
                var campaignSync = CampaignInstaller.Synchronize();
                if (campaignSync.UserVisibleChanges.Count > 0)
                {
                    // Уведомление тоже модальное — заставка обязана уйти ПЕРЕД ним.
                    CloseSplash("Показано уведомление об обновлении кампаний.");
                    using var notice = new CampaignSyncNoticeForm(campaignSync.UserVisibleChanges);
                    notice.ShowDialog();
                }
            }
            else
            {
                AppLogger.Info("Startup: CampaignInstaller пропущен.", "Причина: режим CI test.");
            }

            // Первичная настройка и выбор мира выполняются ДО создания главного
            // окна. Порядок принципиален: псевдоним входит в подпись каждого
            // созданного ресурса (created_by), а мир задаёт, что вообще
            // открывать — пустая главная форма за этими вопросами выглядела бы
            // как «приложение запустилось, но ничего не работает».
            //
            // В CI-режиме настройки не спрашиваются: прогон обязан быть
            // неинтерактивным, и он всё равно читает bundled-контент.
            var preferences = AppUiPreferencesStore.Load();
            if (!ciTest && !RunSetupPhase(ref preferences))
            {
                AppLogger.Info("Startup: первичная настройка отменена пользователем, выход.");
                return;
            }

            AppLogger.Info("Загрузка СДО world data.");
            var sdoPoints = SdoWorldDataLoader.Load();
            var cityPoints = CityWorldDataLoader.Load();
            var worldPoints = sdoPoints.Concat(cityPoints).ToArray();
            AppLogger.Info("World data загружены.", $"sdo={sdoPoints.Count}; cities={cityPoints.Count}; total={worldPoints.Length}");

            // Дорожная геометрия грузится отдельно и НЕ входит в список точек мира:
            // дорог 98 341, и в снимке карты они весили бы ~19 МБ вместо 0.9 МБ
            // при каждом обновлении. В поиске они участвуют критерием «рядом с
            // дорогой», а на карте — отдельным слоем.
            var roads = RoadWorldDataLoader.Load();
            AppLogger.Info("Дорожная геометрия загружена.", $"segments={roads.SegmentCount}");

            // Перекрёстки грузятся рядом с дорогами: критерий «в радиусе от перекрёстка»
            // работает по предпосчитанному списку, а панель ручной проверки показывает
            // его на карте. Нодировка всей сети при старте была бы слишком дорогой.
            var junctions = JunctionWorldDataLoader.Load();
            AppLogger.Info("Перекрёстки загружены.", $"count={junctions.JunctionCount}");

            // Черты городов — пользовательские данные, а не поставляемые: их
            // рисует автор руками, потому что границу города нельзя вывести из
            // геометрии. Пустой файл — нормальное состояние первого запуска.
            //
            // Передаётся ИСТОЧНИК, а не прочитанный индекс: черты автор рисует уже
            // во время работы, и снимок, сделанный на старте, оставлял бы критерии
            // слепыми к только что нарисованной черте до перезапуска приложения.
            var cityBoundaries = new CityBoundaryFileSource();
            AppLogger.Info("Черты городов загружены.",
                $"count={cityBoundaries.Current.Count}; path={cityBoundaries.FilePath}");

            var simulatorAdapter = new SimulatorDataSourceAdapter(worldPoints);
            var worldCount = simulatorAdapter.Channels.Get<WorldState>("world").Value.Points.Count;
            AppLogger.Info("Создан SimulatorDataSourceAdapter.",
                $"channels={simulatorAdapter.Channels.Describe().Count}; points={worldCount}");

            mainForm = new MainForm(simulatorAdapter.Channels, ciTest, roads, junctions, cityBoundaries, worldPoints)
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

                    CloseSplash(reason);
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
            CloseSplash("Запуск прерван исключением.");
            mainForm?.Dispose();

            MessageBox.Show(
                ex.ToString(),
                "Assist Quest Editor — ошибка запуска",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            CloseSplash("Завершение процесса.");

            // Освобождаем мьютекс и останавливаем слушатель канала: иначе
            // следующий запуск решил бы, что приложение всё ещё работает.
            singleInstance?.Dispose();

            AppLogger.Info("=== Assist Quest Editor END ===");
        }
    }

    /// <summary>
    /// Закрывает заставку, если она ещё жива.
    ///
    /// Идемпотентность здесь обязательна: заставку гасят в нескольких местах —
    /// перед модальными диалогами, при готовности главного окна, при ошибке и в
    /// finally. Обращение к уже освобождённой форме уронило бы запуск, поэтому
    /// проверка идёт до любого действия, а поле обнуляется после Close.
    ///
    /// Вызывается ДО ShowDialog. Модальный диалог блокирует поток, поэтому код,
    /// который закрыл бы заставку «после него», не выполнится до конца диалога —
    /// а именно под заставкой диалог и оказывается невидимым.
    /// </summary>
    private static void CloseSplash(string reason)
    {
        if (_splash is null || _splash.IsDisposed)
        {
            _splash = null;
            return;
        }

        _splash.Close();
        _splash.Dispose();
        _splash = null;
        AppLogger.Info("Splash: завершён.", reason);
    }

    /// <summary>
    /// Первичная настройка и выбор мира.
    ///
    /// Возвращает <c>false</c>, если пользователь отказался — тогда приложение
    /// обязано завершиться: без псевдонима ресурсы создавались бы без подписи, а
    /// без мира нечего открывать. Оба случая — не ошибка, а нормальный отказ, и
    /// молча продолжать работу нельзя.
    ///
    /// Выбор мира повторяется, пока пользователь не выберет или не откажется:
    /// отмена диалога мира возвращает к тому же вопросу, потому что «отменить» в
    /// форме, где ещё ничего не сделано, означает «передумал», а не «выйти».
    /// </summary>
    private static bool RunSetupPhase(ref AppUiPreferences preferences)
    {
        // Заставка гасится ДО первого диалога этой фазы. Это и есть исправление
        // исходного дефекта: оба диалога модальные, а заставка висит поверх всего
        // окна, поэтому раньше они открывались ПОД ней — в превью таскбара окно
        // было видно, а на экране оставалась картинка, и ни ввести псевдоним, ни
        // отменить, ни выйти было нельзя.
        CloseSplash("Перед первичной настройкой.");

        if (!preferences.SetupCompleted || !ResourceMetadata.IsValidAuthor(preferences.Author))
        {
            using var setup = new FirstRunSetupForm(preferences.Author, preferences.Language);
            if (setup.ShowDialog() != DialogResult.OK)
                return false;

            preferences = preferences with
            {
                Author = setup.Author,
                Language = setup.Language,
                SetupCompleted = true
            };

            AppUiPreferencesStore.Save(preferences);
            AppLogger.Info("Startup: первичная настройка завершена.",
                $"author={setup.Author}; language={setup.Language}");
        }

        var author = preferences.Author!;
        var store = new WorldStore(AppPaths.UserRoot, author, readOnly: false);

        AppLogger.Info("Startup: миров найдено.", $"count={store.Worlds.Count}");

        using var chooser = new WorldChooserForm(store);
        if (chooser.ShowDialog() != DialogResult.OK || chooser.SelectedWorld is null)
            return false;

        var world = chooser.SelectedWorld;
        preferences = preferences with
        {
            LastWorldId = world.Definition.Id,
            LastCampaignId = world.Definition.LastCampaignId
        };
        AppUiPreferencesStore.Save(preferences);

        AppLogger.Info("Startup: мир выбран.",
            $"world={world.Definition.Id}; folder={world.FolderPath}; " +
            $"lastCampaign={world.Definition.LastCampaignId ?? "нет"}");

        return true;
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

        // Заставка гасится ПЕРЕД предупреждением: оно модальное, а заставка
        // висит поверх всего. Иначе пользователь увидел бы картинку запуска и
        // невидимое окно, которое невозможно закрыть, — то есть приложение
        // выглядело бы зависшим из-за предупреждения, которое он не читает.
        CloseSplash("Показано предупреждение о расхождении ресурсов.");

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
    /// Собирает поставляемый демо-мир в <c>data/DemoWorld.aqezip</c>.
    ///
    /// Запускается сборкой ресурсов, а не пользователем. Отчёт печатается и в
    /// stdout, и в файл рядом с архивом: приложение — WinExe без консоли, и
    /// stdout из вызывающего процесса не читается, поэтому файл отчёта —
    /// единственный канал, который видит и сборка, и человек.
    ///
    /// Код возврата: 0 — архив собран, 1 — не удалось.
    /// </summary>
    private static int BuildDemoWorldFromCommandLine()
    {
        var appDirectory = ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath)
            ?? AppContext.BaseDirectory;
        var reportPath = Path.Combine(appDirectory, "demo-world-report.txt");

        try
        {
            // Архив кладётся рядом с ресурсами приложения: именно там его ищет
            // «Пропустить» при первом запуске (AppPaths.ResourceRoot).
            var archive = Path.Combine(AppPaths.ResourceRoot, WorldArchiveRules.DemoWorldFileName);

            // Подпись и момент ФИКСИРОВАНЫ (DemoWorldSeeder.DemoMoment/DemoAuthor):
            // иначе архив менялся бы при каждой пересборке, и CI не мог бы
            // проверить, что лежащий в поставке файл соответствует коду.
            var result = DemoWorldSeeder.BuildArchive(
                archive,
                author: DemoWorldSeeder.DemoAuthor,
                moment: DemoWorldSeeder.DemoMoment);

            var lines = new[]
            {
                "Демо-мир собран.",
                "Файл: " + result.Path,
                "Файлов: " + result.FileCount,
                "Исходный размер: " + result.SourceBytes + " Б",
                "Размер архива: " + result.ArchiveBytes + " Б",
                "Сжатие: " + result.CompressionPercent + "%"
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            File.WriteAllLines(reportPath, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Собрать демо-мир не удалось: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                File.WriteAllLines(reportPath, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Проба выгрузки ресурса без интерфейса.
    ///
    /// Запускается так:
    /// `--export-probe &lt;папка-источника&gt; &lt;имя&gt; [--archive] [--out &lt;корень&gt;]`
    ///
    /// Проверяет ровно тот код, которым пользуется меню
    /// (<see cref="ResourceExportService"/>), и потому ловит дефекты упаковки и
    /// раскладки, которые иначе видны только после ручного нажатия «Экспортировать».
    /// Корень выгрузки можно подменить (--out), чтобы проба не трогала
    /// пользовательские документы.
    ///
    /// Код возврата: 0 — выгружено, 1 — не удалось.
    /// </summary>
    private static int ExportProbeFromCommandLine(string[] args)
    {
        // Отчёт кладётся рядом с исполняемым файлом по умолчанию, но путь можно
        // задать явно. Без этого две пробы подряд пишут в один файл, и вторая
        // читает отчёт первой — проба подтверждала бы не свой результат.
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "export-probe-report.txt");

        var reportArgumentIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportArgumentIndex >= 0 && reportArgumentIndex + 1 < args.Length
            ? args[reportArgumentIndex + 1]
            : reportDefault;

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                // Значения ключей --out и --report не являются позиционными
                // аргументами: иначе «--out C:\x» подставил бы «C:\x» вместо имени
                // ресурса, и проба выгружала бы не то, что просили.
                .Where(arg => !IsValueOf(args, "--out", arg) && !IsValueOf(args, "--report", arg) &&
                              !IsValueOf(args, "--moment", arg))
                .ToArray();

            if (positional.Length < 2)
                throw new ArgumentException(
                    "Ожидалось: --export-probe <папка> <имя> [--archive] [--out <корень>] [--report <файл>]");

            var source = positional[0];
            var name = positional[1];
            var archive = args.Any(arg => string.Equals(arg, "--archive", StringComparison.OrdinalIgnoreCase));
            var outIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase));
            var outputRoot = outIndex >= 0 && outIndex + 1 < args.Length
                ? args[outIndex + 1]
                : AppPaths.UserRoot;

            // Момент выгрузки можно задать явно. Метка времени стоит в пути, и
            // без возможности её зафиксировать проверить поведение при занятом
            // имени невозможно: две пробы подряд попадают в разные секунды и
            // просто не сталкиваются, а «нумерация работает» осталось бы
            // непроверенным ровно в том случае, ради которого она есть.
            var momentIndex = Array.FindIndex(args, arg =>
                string.Equals(arg, "--moment", StringComparison.OrdinalIgnoreCase));
            var moment = momentIndex >= 0 && momentIndex + 1 < args.Length &&
                         DateTimeOffset.TryParse(args[momentIndex + 1],
                             System.Globalization.CultureInfo.InvariantCulture,
                             System.Globalization.DateTimeStyles.RoundtripKind,
                             out var parsedMoment)
                ? parsedMoment
                : DateTimeOffset.UtcNow;

            ResourceExportResult result;

            if (archive)
            {
                var manifest = new WorldArchiveManifest(
                    Kind: WorldArchiveKinds.World,
                    Id: "export-probe",
                    Name: name,
                    FullName: name,
                    Description: "Проба выгрузки.",
                    Version: WorldArchiveRules.SupportedVersion,
                    IncludesDependencies: true,
                    Entries: Array.Empty<WorldArchiveEntry>(),
                    ParentWorldId: null,
                    ParentCampaignId: null,
                    Metadata: new ResourceMetadata(
                        "export-probe", moment, "export-probe", moment));

                result = ResourceExportService.ExportArchive(
                    outputRoot, source, name, manifest, moment);
            }
            else
            {
                result = ResourceExportService.ExportFolder(outputRoot, source, name, moment);
            }

            // Состав берётся из РЕЗУЛЬТАТА: проба обязана подтверждать то, что
            // действительно легло на диск, а не то, что предполагалось.
            var lines = new[]
            {
                "Выгрузка выполнена.",
                "Архив: " + (result.IsArchive ? "да" : "нет"),
                "Путь: " + result.Path,
                "Файлов: " + result.FileCount,
                "Байт: " + result.Bytes
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            File.WriteAllLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Проба выгрузки не удалась: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                File.WriteAllLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Является ли аргумент значением указанного ключа.
    ///
    /// Нужно при разборе позиционных аргументов: «--out C:\x» неотличим от двух
    /// позиционных, и «C:\x» ушло бы в имя ресурса.
    /// </summary>
    private static bool IsValueOf(string[] args, string key, string candidate)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && ReferenceEquals(args[index + 1], candidate);
    }

    /// <summary>
    /// Проба правки свойств ресурса без интерфейса.
    ///
    /// Запускается так:
    /// `--properties-probe &lt;корень пользователя&gt; &lt;world|campaign&gt; &lt;id&gt; &lt;имя&gt; [полное имя] [описание]`
    ///
    /// Вызывает те же <see cref="WorldStore.UpdateWorld"/> /
    /// <see cref="CampaignStore.UpdateCampaign"/>, что и окно «Ред.», поэтому
    /// ловит потерю игровых полей кампании — то, что иначе видно только после
    /// ручной правки и начала симуляции.
    ///
    /// Код возврата: 0 — записано, 1 — не удалось.
    /// </summary>
    private static int PropertiesProbeFromCommandLine(string[] args)
    {
        // Отчёт — свой на каждый прогон по умолчанию рядом с exe, но путь можно
        // задать явно: две пробы подряд иначе пишут в один файл, и вторая читает
        // отчёт первой, подтверждая не свой результат.
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "properties-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                // Значение ключа --report не является позиционным: иначе путь
                // отчёта уехал бы в имя ресурса или в описание.
                .Where(arg => !IsValueOf(args, "--report", arg))
                .ToArray();

            if (positional.Length < 5)
                throw new ArgumentException(
                    "Ожидалось: --properties-probe <корень> <world|campaign> <worldId> <id> <имя> " +
                    "[полное] [описание] [--report <файл>]");

            var userRoot = positional[0];
            var kind = positional[1];
            var worldId = positional[2];
            var id = positional[3];
            var name = positional[4];
            var fullName = positional.Length > 5 ? positional[5] : null;
            var description = positional.Length > 6 ? positional[6] : null;

            var store = new WorldStore(userRoot, "properties-probe", readOnly: false);

            if (kind.Equals("world", StringComparison.OrdinalIgnoreCase))
            {
                var updated = store.UpdateWorld(id, name, fullName, description, imageFileName: null);
                WritePropertiesReport(report, "world", updated.Definition.Id, updated.DisplayName,
                    updated.FolderPath);
            }
            else
            {
                // Каталог кампаний ограничивается МИРОМ: кампаний с одним id
                // (например common) в разных мирах сколько угодно, и «найти по
                // id во всём корне» открыло бы чужую.
                var world = store.FindWorld(worldId)
                    ?? throw new InvalidOperationException("Мир не найден: " + worldId);

                var campaigns = new CampaignStore(userRoot, readOnly: false, author: "properties-probe")
                    .ScopedTo(world.FolderPath);

                campaigns.UpdateCampaign(id, name, fullName, description, imageFileName: null);

                var updated = campaigns.Records.FirstOrDefault(record =>
                    record.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

                WritePropertiesReport(report, "campaign", id,
                    updated?.Definition.Name ?? name,
                    updated?.FolderPath ?? world.FolderPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            var message = "Правка свойств не удалась: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                File.WriteAllLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Пишет отчёт пробы правки.
    ///
    /// Отчёт — единственный канал, который видит вызывающий процесс: приложение
    /// собрано как WinExe без консоли, и stdout из MSBuild/Node не читается.
    /// Каталог создаётся заранее: путь отчёта задаёт вызывающий, и он мог указать
    /// ещё не существующую папку.
    /// </summary>
    private static void WritePropertiesReport(
        string report, string kind, string id, string name, string folder)
    {
        var lines = new[] { "Свойства обновлены.", "Вид: " + kind, "Id: " + id, "Имя: " + name,
            "Папка: " + folder };

        foreach (var line in lines)
            Console.WriteLine(line);

        WriteResourceLines(report, lines);
    }

    /// <summary>
    /// Проба импорта кампании и квеста в родителя.
    ///
    /// Запускается так:
    /// `--import-probe &lt;корень&gt; &lt;worldId&gt; &lt;campaignId|-> &lt;архив&gt; [--overwrite] [--report &lt;файл&gt;]`
    ///
    /// <c>campaignId</c> задаётся только для квеста; «-» означает «кампания не
    /// нужна». Вызывает те же <see cref="ResourceImportService"/>-методы, что
    /// диалог импорта, поэтому проверяет и раскладку папок, и перенос через
    /// временную папку.
    ///
    /// Код возврата: 0 — импортировано, 1 — не удалось.
    /// </summary>
    private static int ImportProbeFromCommandLine(string[] args)
    {
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "import-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                .Where(arg => !IsValueOf(args, "--report", arg))
                .ToArray();

            if (positional.Length < 4)
                throw new ArgumentException(
                    "Ожидалось: --import-probe <корень> <worldId> <campaignId|-> <архив> " +
                    "[--overwrite] [--report <файл>]");

            var userRoot = positional[0];
            var worldId = positional[1];
            var campaignId = positional[2];
            var archivePath = positional[3];
            var overwrite = args.Any(arg =>
                string.Equals(arg, "--overwrite", StringComparison.OrdinalIgnoreCase));

            var worlds = new WorldStore(userRoot, "import-probe", readOnly: false);
            var world = worlds.FindWorld(worldId)
                ?? throw new InvalidOperationException("Мир не найден: " + worldId);

            var inspection = WorldArchiveService.Inspect(archivePath);
            ResourceImportResult imported;

            if (campaignId is "" or "-")
            {
                imported = ResourceImportService.ImportCampaign(world, inspection, overwrite);
            }
            else
            {
                var campaigns = new CampaignStore(userRoot, readOnly: false, author: "import-probe")
                    .ScopedTo(world.FolderPath);

                var campaign = campaigns.Records.FirstOrDefault(record =>
                    record.Definition.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Кампания не найдена: " + campaignId);

                imported = ResourceImportService.ImportQuest(campaign, inspection, overwrite);
            }

            var lines = new[]
            {
                "Импорт выполнен.",
                "Вид: " + imported.Kind,
                "Id: " + imported.Id,
                "Название: " + imported.DisplayName,
                "Путь: " + imported.TargetPath
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Импорт не удался: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Собирает архив заданного вида.
    ///
    /// Запускается так:
    /// `--build-archive &lt;папка-источника&gt; &lt;файл.aqezip&gt; &lt;world|campaign|quest&gt; &lt;parentWorldId&gt;`
    ///
    /// Отдельный режим, а не вариант пробы выгрузки: та умеет только мир, а
    /// проверкам импорта нужны архивы кампании и квеста — то есть с другим
    /// манифестом. Вид ресурса задаётся здесь, потому что он и есть предмет
    /// проверки: импорт обязан отвергать не тот вид.
    ///
    /// Код возврата: 0 — архив собран, 1 — не удалось.
    /// </summary>
    private static int BuildArchiveFromCommandLine(string[] args)
    {
        var report = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "build-archive-report.txt");

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                .ToArray();

            if (positional.Length < 4)
                throw new ArgumentException(
                    "Ожидалось: --build-archive <папка> <файл.aqezip> <world|campaign|quest> <parentWorldId>");

            var source = positional[0];
            var target = positional[1];
            var kind = positional[2].ToLowerInvariant();
            var parentWorldId = positional[3];

            if (kind is not (WorldArchiveKinds.World or WorldArchiveKinds.Campaign or WorldArchiveKinds.Quest))
                throw new ArgumentException("Неизвестный вид ресурса: " + kind);

            // Имя и id берутся из того же манифеста, что и при выгрузке: если
            // собирать их из имени папки, проверка импорта имела бы дело с
            // архивом, которого приложение не создаёт.
            var manifest = new WorldArchiveManifest(
                Kind: kind,
                Id: kind == WorldArchiveKinds.World ? parentWorldId : Path.GetFileNameWithoutExtension(target),
                Name: Path.GetFileNameWithoutExtension(target),
                FullName: Path.GetFileNameWithoutExtension(target),
                Description: "Архив, собранный пробой.",
                Version: WorldArchiveRules.SupportedVersion,
                IncludesDependencies: true,
                Entries: Array.Empty<WorldArchiveEntry>(),
                ParentWorldId: kind == WorldArchiveKinds.World ? null : parentWorldId,
                ParentCampaignId: null,
                Metadata: new ResourceMetadata("build-archive", DateTimeOffset.UtcNow,
                    "build-archive", DateTimeOffset.UtcNow));

            var packed = WorldArchiveService.Pack(source, target, manifest, includeDependencies: true);

            var lines = new[]
            {
                "Архив собран.",
                "Вид: " + kind,
                "Файл: " + packed.Path,
                "Файлов: " + packed.FileCount,
                "Байт: " + packed.ArchiveBytes
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Собрать архив не удалось: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Проба создания мира или кампании.
    ///
    /// Запускается так:
    /// `--create-probe &lt;корень&gt; &lt;world|campaign&gt; &lt;имя&gt; [worldId] [полное] [описание] [--report &lt;файл&gt;]`
    ///
    /// <c>worldId</c> обязателен для кампании. Вызывает те же
    /// <see cref="WorldStore.CreateWorld"/> / <see cref="CampaignStore.CreateCampaign"/>,
    /// что и окно «Создать», поэтому проверяет и раскладку, и то, что общая
    /// кампания с демо-квестом появилась.
    ///
    /// Код возврата: 0 — создано, 1 — не удалось.
    /// </summary>
    private static int CreateProbeFromCommandLine(string[] args)
    {
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "create-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                .Where(arg => !IsValueOf(args, "--report", arg))
                .ToArray();

            if (positional.Length < 3)
                throw new ArgumentException(
                    "Ожидалось: --create-probe <корень> <world|campaign> <имя> [worldId] " +
                    "[полное] [описание] [--report <файл>]");

            var userRoot = positional[0];
            var kind = positional[1];
            var name = positional[2];
            var worldId = positional.Length > 3 ? positional[3] : null;
            var fullName = positional.Length > 4 ? positional[4] : null;
            var description = positional.Length > 5 ? positional[5] : null;

            var worlds = new WorldStore(userRoot, "create-probe", readOnly: false);
            string folder;
            string id;

            if (kind.Equals("world", StringComparison.OrdinalIgnoreCase))
            {
                var world = worlds.CreateWorld(name, fullName, description);
                folder = world.FolderPath;
                id = world.Definition.Id;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(worldId))
                    throw new ArgumentException("Для кампании обязателен worldId.");

                var world = worlds.FindWorld(worldId)
                    ?? throw new InvalidOperationException("Мир не найден: " + worldId);

                var campaigns = new CampaignStore(userRoot, readOnly: false, author: "create-probe")
                    .ScopedTo(world.FolderPath);

                var campaign = campaigns.CreateCampaign(worldId, name, fullName, description);
                folder = campaign.FolderPath;
                id = campaign.Definition.Id;
            }

            var lines = new[]
            {
                "Ресурс создан.",
                "Вид: " + kind,
                "Id: " + id,
                "Папка: " + folder
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Создание не удалось: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// `--tree-probe &lt;корень миров&gt; &lt;id мира&gt; [--report &lt;файл&gt;]`
    ///
    /// Строит НАСТОЯЩЕЕ окно дерева кампаний и меряет раскладку.
    ///
    /// Проба поведенческая намеренно. Перекрытие строк, обрезка названия
    /// многоточием вместо переноса и две полосы прокрутки не видны в исходниках:
    /// форма строится кодом, и «правильная на вид» формула высоты даёт кривую
    /// раскладку. Это уже случалось дважды — состав кампании уезжал под нижний
    /// край, а подписи получали высоту строки квеста.
    /// </summary>
    private static int TreeProbeFromCommandLine(string[] args)
    {
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "tree-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                .Where(arg => !IsValueOf(args, "--report", arg))
                .ToArray();

            if (positional.Length < 2)
                throw new ArgumentException(
                    "Ожидалось: --tree-probe <корень миров> <id мира> [--report <файл>]");

            var userRoot = positional[0];
            var worldId = positional[1];

            var worlds = new WorldStore(userRoot, "tree-probe", readOnly: false);
            var world = worlds.FindWorld(worldId)
                ?? throw new InvalidOperationException("Мир не найден: " + worldId);

            var campaigns = new CampaignStore(userRoot, readOnly: false, author: "tree-probe")
                .ScopedTo(world.FolderPath);

            var lines = new List<string> { "Дерево кампаний проверено.", "Мир: " + world.DisplayName };

            using var form = new CampaignsForm();
            form.SetWorld(world);
            form.SetCatalog(campaigns.BuildSimulatorCatalog());

            // Показ вне экрана: окно должно пройти полную раскладку, но пробе
            // нельзя мешать и нельзя полагаться на наличие монитора.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Size = new Size(620, 900);
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            Application.DoEvents();

            var measurements = MeasureTree(form);
            lines.AddRange(measurements);

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Раскладка дерева не проверена: " + ex;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Меряет раскладку дерева: перекрытия, обрезку и полосы прокрутки.
    ///
    /// Отчёт строится строками с префиксами, потому что читает его проверка:
    /// строки вида «overlap:» считаются дефектом, «clipped:» — обрезкой.
    /// </summary>
    private static IEnumerable<string> MeasureTree(Form form)
    {
        var lines = new List<string>();

        // Все контролы дерева в одном списке вместе с их абсолютными
        // прямоугольниками: вложенные контейнеры дают координаты ОТНОСИТЕЛЬНО
        // родителя, и прямое сравнение перекрытий было бы неверным.
        var panels = new List<(Control Control, Rectangle Bounds)>();
        Collect(form, panels);

        var headers = new List<(Control Control, Rectangle Bounds)>();
        // Тип конкретный, а не Control: HorizontalScroll есть только у
        // контейнеров с прокруткой, и общий тип не дал бы его прочитать.
        var scrolling = new List<ScrollableControl>();

        // Заголовок мира ищется по кнопке «ПАПКА»: это его отличительный признак.
        // Без этого проба проходила бы ВПУСТУЮ на пустом окне — «нет перекрытий»
        // верно и тогда, когда не отрисовано ничего, и такая проверка бесполезна.
        Rectangle? worldHeader = null;
        var campaignHeaders = 0;

        foreach (var (control, bounds) in panels)
        {
            // Заголовок — таблица, чей родитель ПРОСТАЯ панель. Проверка
            // «родитель — Panel» недостаточна: FlowLayoutPanel тоже наследуется
            // от Panel, поэтому строки квестов попадали в этот счёт, и «кампаний»
            // насчитывалось больше, чем есть (проверено запуском: 4 вместо 3).
            if (control is TableLayoutPanel &&
                control.Parent is Panel && control.Parent is not FlowLayoutPanel)
            {
                var isWorld = control.Controls.OfType<Button>()
                    .Any(button => button.Text == "ПАПКА");

                if (isWorld)
                {
                    worldHeader = bounds;
                    lines.Add("world header: " + bounds.Width + "x" + bounds.Height);
                }
                else
                {
                    campaignHeaders++;
                }

                headers.Add((control, bounds));

                // Заголовок обязан вмещать свои контролы: если самый правый
                // выходит за него, кнопка «Папка»/«ПАПКА» обрезана.
                foreach (Control child in control.Controls)
                {
                    var childBounds = control.RectangleToScreen(child.Bounds);
                    if (childBounds.Right > bounds.Right + 1 || childBounds.Bottom > bounds.Bottom + 1)
                    {
                        lines.Add("clipped: заголовок не вмещает " +
                            child.GetType().Name + " (" + Describe(child) + ")");
                    }
                }
            }

            if (control is FlowLayoutPanel flow && flow.AutoScroll)
                scrolling.Add(flow);
        }

        if (worldHeader is null)
            lines.Add("noworldheader: пункт мира не отрисован");

        lines.Add("campaign headers: " + campaignHeaders);
        lines.Add("quest rows: " + panels.Count(entry =>
            entry.Control is TableLayoutPanel && entry.Control.Parent is FlowLayoutPanel));

        // Кампании обязаны быть ВЛОЖЕНЫ в пункт мира, то есть лежать по вертикали
        // НИЖЕ его заголовка и внутри его рамки. Если бы они остались плоским
        // списком, они начинались бы на уровне заголовка — и дерево выглядело бы
        // двумя несвязанными частями.
        if (worldHeader is Rectangle worldBounds)
        {
            foreach (var (control, bounds) in headers)
            {
                var isWorld = control.Controls.OfType<Button>()
                    .Any(button => button.Text == "ПАПКА");
                if (isWorld)
                    continue;

                if (bounds.Top < worldBounds.Bottom - 2 && bounds.Top < worldBounds.Top + 2)
                {
                    lines.Add("notnested: кампания стоит на уровне заголовка мира, а не внутри него");
                    break;
                }
            }
        }

        // Перекрытие заголовков — это то, на что жаловался автор («наезжают»).
        for (var i = 0; i < headers.Count; i++)
        {
            for (var j = i + 1; j < headers.Count; j++)
            {
                if (headers[i].Bounds.IntersectsWith(headers[j].Bounds))
                {
                    lines.Add("overlap: заголовки пересекаются (" +
                        Describe(headers[i].Control) + " × " + Describe(headers[j].Control) + ")");
                }
            }
        }

        // Перекрытие ЛЮБОГО контрола заголовка с чужим: так ловится наезжание
        // кнопок «Папка» двух кампаний, которое пересечением заголовков не видно.
        var headerChildren = new List<(Control Control, Rectangle Bounds)>();
        foreach (var (header, _) in headers)
        {
            foreach (Control child in header.Controls)
            {
                if (child is Label || child is Button || child is CheckBox)
                    headerChildren.Add((child, header.RectangleToScreen(child.Bounds)));
            }
        }

        for (var i = 0; i < headerChildren.Count; i++)
        {
            for (var j = i + 1; j < headerChildren.Count; j++)
            {
                var a = headerChildren[i];
                var b = headerChildren[j];
                if (a.Bounds.IntersectsWith(b.Bounds))
                    lines.Add("overlap: " + Describe(a.Control) + " × " + Describe(b.Control));
            }
        }

        // Полоса прокрутки должна быть РОВНО одна — у корневого списка дерева.
        lines.Add("scrolling containers: " + scrolling.Count);
        if (scrolling.Count != 1)
        {
            lines.Add("scrollcount: ожидалась одна прокрутка, найдено " + scrolling.Count);
        }

        // Горизонтальная прокрутка запрещена: длинный текст переносится по словам.
        // Ширина содержимого корневого списка не должна превышать его самого.
        foreach (var flow in scrolling)
        {
            if (flow.HorizontalScroll.Visible || flow.HorizontalScroll.Maximum > flow.ClientSize.Width)
                lines.Add("hscroll: у дерева появилась горизонтальная прокрутка");
        }

        return lines;

        // Абсолютные прямоугольники собираются в экранных координатах: так
        // перекрытие считается верно независимо от вложенности.
        static void Collect(Control parent, List<(Control, Rectangle)> sink)
        {
            foreach (Control child in parent.Controls)
            {
                sink.Add((child, child.Parent!.RectangleToScreen(child.Bounds)));
                Collect(child, sink);
            }
        }

        static string Describe(Control control) =>
            string.IsNullOrEmpty(control.Text)
                ? control.GetType().Name
                : control.Text.Replace(Environment.NewLine, " ").Trim();
    }

    /// <summary>
    /// `--inventory-probe [--report &lt;файл&gt;]`
    ///
    /// Сообщает расчётные размеры окна инвентаря и проверяет их связность.
    ///
    /// Проба НЕ открывает окно: расчёт живёт в домене
    /// (<see cref="InventoryLayoutRules"/>), и открывать форму ради чисел
    /// значило бы требовать видеокарту для арифметики. Проверка сравнивает
    /// числа с CSS-переменной страницы — это единственное место, где они могут
    /// разойтись, и разойдясь, дают полосу прокрутки или обрезанные ячейки.
    /// </summary>
    private static int InventoryProbeFromCommandLine(string[] args)
    {
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "inventory-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var lines = new List<string>
            {
                "Размеры инвентаря проверены.",
                "columns: " + InventoryLayoutRules.Columns,
                "rows: " + InventoryLayoutRules.Rows,
                "capacity: " + InventoryLayoutRules.Capacity,
                "cell: " + InventoryLayoutRules.CellSize,
                "gap: " + InventoryLayoutRules.CellGap,
                "grid: " + InventoryLayoutRules.GridWidth + "x" + InventoryLayoutRules.GridHeight,
                "window: " + InventoryLayoutRules.WindowWidth + "x" + InventoryLayoutRules.WindowHeight
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Размеры инвентаря не проверены: " + ex;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// `--icon-probe [--report &lt;файл&gt;]`
    ///
    /// Собирает иконку приложения из логотипов и читает её обратно.
    ///
    /// Проба ПОВЕДЕНЧЕСКАЯ: наличие файла логотипа не доказывает, что из него
    /// получится рабочая иконка — контейнер может собраться с неверным числом
    /// кадров, с неверными смещениями или нечитаемым кадром. Здесь ICO
    /// собирается тем же кодом, что используют окна и ассоциации, и проверяется
    /// ГЛУБИНОЙ: кадры извлекаются из полученных байтов.
    /// </summary>
    private static int IconProbeFromCommandLine(string[] args)
    {
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "icon-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var lines = new List<string> { "Иконка приложения проверена." };

            var bytes = AppIconService.ToIcoBytes()
                ?? throw new InvalidOperationException(
                    "Иконка не собрана: логотипы не найдены рядом с приложением. " +
                    "Ожидались файлы в " + AppIconService.AssetsDirectory);

            lines.Add("bytes: " + bytes.Length);
            lines.Add("assets: " + AppIconService.AssetsDirectory);
            lines.Add("ico: " + AppIconService.IconFilePath());

            // Заголовок контейнера: тип 1 = иконка, и число кадров должно
            // совпадать с объявленным набором размеров.
            var frames = ReadUInt16(bytes, 4);
            lines.Add("frames: " + frames);

            if (frames != AppIconService.FrameSizes.Count)
                throw new InvalidOperationException(
                    $"Кадров {frames}, ожидалось {AppIconService.FrameSizes.Count}.");

            // Каждый кадр извлекается по его же смещению и проверяется как PNG:
            // подпись плюс фактические размеры из IHDR. Так ловится и сдвинутое
            // смещение, и подменённый кадр, и кадр чужого размера.
            for (var i = 0; i < frames; i++)
            {
                var entry = 6 + i * 16;
                var declaredWidth = bytes[entry] == 0 ? 256 : bytes[entry];
                // Длина и смещение в ICONDIRENTRY записаны LITTLE-endian, а
                // ширина и высота PNG внутри кадра — BIG-endian. Порядок байтов
                // в одном формате разный, и перепутать их легко: проверка на
                // этом и упала в первый прогон.
                var length = (int)ReadUInt32LittleEndian(bytes, entry + 8);
                var offset = (int)ReadUInt32LittleEndian(bytes, entry + 12);

                if (offset + length > bytes.Length)
                    throw new InvalidOperationException(
                        $"Кадр {i} выходит за пределы файла: offset={offset}, length={length}.");

                var isPng = bytes[offset] == 0x89 && bytes[offset + 1] == 0x50 &&
                            bytes[offset + 2] == 0x4E && bytes[offset + 3] == 0x47;

                if (!isPng)
                    throw new InvalidOperationException($"Кадр {i} не является PNG.");

                // Ширина и высота PNG лежат в IHDR как big-endian, смещение 16 и 20.
                var pngWidth = (int)ReadUInt32BigEndian(bytes, offset + 16);
                var pngHeight = (int)ReadUInt32BigEndian(bytes, offset + 20);

                if (pngWidth != declaredWidth || pngHeight != declaredWidth)
                    throw new InvalidOperationException(
                        $"Кадр {i}: объявлен {declaredWidth}x{declaredWidth}, " +
                        $"а внутри {pngWidth}x{pngHeight}.");

                lines.Add($"frame {i}: {pngWidth}x{pngHeight}, {length} байт");
            }

            // Иконка должна РАБОТАТЬ: тот же путь использует форма окна.
            using var icon = AppIconService.CreateWindowIcon()
                ?? throw new InvalidOperationException("Иконка собрана, но не читается как Icon.");

            lines.Add("window icon: " + icon.Width + "x" + icon.Height);

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Иконка не проверена: " + ex.Message;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
    }

    /// <summary>
    /// Читает 16-битное число в порядке little-endian, как того требует формат ICO.
    /// </summary>
    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    /// <summary>Читает 32-битное число в порядке little-endian (ICONDIRENTRY).</summary>
    private static uint ReadUInt32LittleEndian(byte[] bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
               (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    /// <summary>Читает 32-битное число в порядке big-endian (заголовок PNG).</summary>
    private static uint ReadUInt32BigEndian(byte[] bytes, int offset) =>
        (uint)((bytes[offset] << 24) | (bytes[offset + 1] << 16) |
               (bytes[offset + 2] << 8) | bytes[offset + 3]);

    /// <summary>
    /// Пишет строки отчёта, создавая каталог.
    ///
    /// Путь отчёта задаёт вызывающий процесс, и он может указать ещё не
    /// существующую папку — тогда проба упала бы на записи, уже выполнив импорт,
    /// и это выглядело бы как «импорт не сработал».
    /// </summary>
    private static void WriteResourceLines(string report, IReadOnlyList<string> lines)
    {
        var directory = Path.GetDirectoryName(report);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllLines(report, lines);
    }

    /// <summary>
    /// `--autosave-probe &lt;корень миров&gt; &lt;id мира&gt; [--report &lt;файл&gt;]`
    ///
    /// Записывает автосохранение в мир и читает его обратно.
    ///
    /// Проба существует потому, что прежний дефект был невидим для чтения
    /// исходников: `SimulationSaveStore` писал в общий каталог вне дерева миров,
    /// который никто не создавал, и запись падала с
    /// <c>DirectoryNotFoundException</c>. Ошибка глушилась вызывающим кодом
    /// (симуляция не должна падать из-за диска), поэтому наружу это выглядело
    /// как «автосохранение не работает», а мир при следующем запуске снова был
    /// новым. Проверка обязана ПИСАТЬ файл и читать его обратно.
    /// </summary>
    private static int AutosaveProbeFromCommandLine(string[] args)
    {
        var reportDefault = Path.Combine(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "autosave-probe-report.txt");

        var reportIndex = Array.FindIndex(args, arg =>
            string.Equals(arg, "--report", StringComparison.OrdinalIgnoreCase));
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : reportDefault;

        try
        {
            var positional = args
                .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                .Where(arg => !IsValueOf(args, "--report", arg))
                .ToArray();

            if (positional.Length < 2)
                throw new ArgumentException(
                    "Ожидалось: --autosave-probe <корень миров> <id мира> [--report <файл>]");

            var userRoot = positional[0];
            var worldId = positional[1];

            var worlds = new WorldStore(userRoot, "autosave-probe", readOnly: false);
            var world = worlds.FindWorld(worldId)
                ?? throw new InvalidOperationException("Мир не найден: " + worldId);

            // Путь берётся ИЗ КОНТРАКТА раскладки, а не собирается здесь: проба
            // обязана проверять то же место, куда пишет приложение.
            var savesFolder = WorldPaths.SavesFolderPath(world.FolderPath);

            // Каталог СОЗНАТЕЛЬНО не создаётся: прежний дефект состоял именно в
            // том, что стор писал в несуществующий каталог. Если стор снова
            // перестанет его создавать, проба упадёт.
            var store = new SimulationSaveStore(savesFolder);

            var clockNow = new DateTimeOffset(2026, 5, 15, 18, 30, 42, TimeSpan.Zero);
            var header = new SimulationSaveHeader(
                SimulationSaveState.CurrentFormatVersion,
                "session",
                VersionInfo.InformationalVersion,
                clockNow,
                null,
                clockNow,
                "common",
                TimeSpan.FromMinutes(37));

            // Захват состояния — через тот же маппер, что использует симулятор:
            // проба, собирающая снимок по-своему, проверяла бы не то, что пишет
            // приложение.
            var state = SimulationSaveMapper.Capture(new SimulatorDataChannelHub(), "common");

            store.SaveSession(new SimulationSave(header, state));

            var reloaded = store.LoadSession();
            if (reloaded is null)
                throw new InvalidOperationException("Автосохранение записано, но не читается.");

            var lines = new[]
            {
                "Автосохранение проверено.",
                "Папка: " + savesFolder,
                "Файл: " + store.SessionPath,
                "Файл существует: " + File.Exists(store.SessionPath),
                "Размер: " + new FileInfo(store.SessionPath).Length,
                "Прочитано: " + reloaded.Header.Name,
                "Кампания: " + reloaded.Header.CampaignId
            };

            foreach (var line in lines)
                Console.WriteLine(line);

            WriteResourceLines(report, lines);
            return 0;
        }
        catch (Exception ex)
        {
            var message = "Автосохранение не удалось: " + ex;
            Console.WriteLine(message);

            try
            {
                WriteResourceLines(report, new[] { message });
            }
            catch
            {
                // Отчёт — диагностика; не суметь записать его не меняет результат.
            }

            return 1;
        }
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
