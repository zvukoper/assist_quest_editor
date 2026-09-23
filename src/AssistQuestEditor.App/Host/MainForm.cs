using System.Diagnostics;
using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class MainForm : WebViewForm
{
    private readonly IDataChannelHub _hub;
    private readonly CampaignStore _campaignStore;
    private readonly QuestGraphStore _questGraph;
    private readonly SceneCatalog _sceneCatalog;
    private readonly SceneGraphStore _sceneGraph;
    private readonly SceneDocumentSession _sceneDocument;
    private readonly SceneRuntime _sceneRuntime;
    private readonly LocationStore _locationStore;
    private readonly LocationRuntimeResolver _locationResolver;
    private readonly IQuestRuntimeController _runtime;

    /// <summary>
    /// Дорожная геометрия мира.
    ///
    /// Живёт в Host и передаётся в окна, а не лежит в точках мира: дорог ~98 000,
    /// и в снимке карты они весили бы почти 19 МБ вместо 0.9 МБ. В поиске они
    /// участвуют критерием «рядом с дорогой», на карте — отдельным слоем.
    /// </summary>
    private readonly RoadIndex _roads;

    /// <summary>
    /// Перекрёстки дорожной сети (предпосчитанные).
    ///
    /// Отдаются и критерию «в радиусе от перекрёстка», и панели ручной проверки,
    /// где их видно на карте. Считать нодировку при старте редактора нельзя.
    /// </summary>
    private readonly JunctionIndex _junctions;

    /// <summary>
    /// Окна редакторов. Список, а не словарь по странице: одно окно может
    /// переключать активную панель (сайдбар = селектор рабочей области), поэтому
    /// ключ «страница» перестал быть уникальным идентификатором окна.
    /// </summary>
    private readonly List<EditorForm> _editors = new();

    private readonly RecentFileList _recentQuestFiles;
    private readonly RecentFileList _recentSceneFiles;
    private SimulatorForm? _simulator;
    private SettingsForm? _settings;
    private JunctionReviewForm? _junctionReview;

    public MainForm(
        IDataChannelHub hub,
        bool ciTest = false,
        RoadIndex? roads = null,
        JunctionIndex? junctions = null)
        : base(
            "Редактор",
            "main.html",
            new Size(1280, 820),
            "main")
    {
        if (!WindowGeometryStore.HasSaved("main"))
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        _hub = hub;
        _roads = roads ?? new RoadIndex(Array.Empty<RoadSegment>());
        _junctions = junctions ?? new JunctionIndex(Array.Empty<JunctionPoint>());
        _campaignStore = ciTest
            ? new CampaignStore(Path.Combine(AppPaths.ResourceRoot, "campaigns"), readOnly: true)
            : new CampaignStore();
        _questGraph = new QuestGraphStore(QuestDefinitionLoader.LoadDocumentOrFallback().Definition);
        _sceneCatalog = SceneCatalogLoader.Load();
        var initialScene = _sceneCatalog.TryGetScene("ruslan_start", out var ruslanStart)
            ? ruslanStart
            : _sceneCatalog.Scenes.FirstOrDefault() ?? SceneCatalogFactory.CreateStarter().Scenes.First();
        _sceneGraph = new SceneGraphStore(initialScene);
        _locationStore = ciTest
            ? new LocationStore(Path.Combine(Path.GetTempPath(), "AssistQuestEditor-CI-Locations"), readOnly: true)
            : new LocationStore(AppPaths.UserLocationRoot);
        _locationResolver = new LocationRuntimeResolver(_locationStore, _hub, _roads, _junctions);
        var preferences = AppUiPreferencesStore.Load();
        _sceneDocument = new SceneDocumentSession(
            initialScene.Id,
            ResolveScenePath(initialScene.Id),
            preferences.LastSceneDefinitionPath);
        _sceneCatalog.Changed += SceneCatalog_Changed;
        _sceneRuntime = new SceneRuntime(_sceneCatalog, _hub);
        _runtime = new QuestRuntimeCoordinator(
            _hub,
            _sceneRuntime,
            _campaignStore.LoadEnabledQuestDefinitions,
            "tutorial_ruslan_shashlik",
            _locationResolver);

        foreach (var campaign in _campaignStore.BuildSimulatorCatalog())
        {
            foreach (var quest in campaign.Quests)
            {
                _runtime.SetQuestEnabled(
                    quest.QuestId,
                    quest.CampaignActive &&
                    quest.Status == CampaignQuestStatus.Enabled);
            }
        }

        _sceneRuntime.Published += SceneRuntime_Published;
        // Индикатор «Симулятор: активен» зависит от запущенной симуляции, а не от
        // открытого окна, поэтому состояние подписывается на те же события
        // Runtime, что видит окно симулятора.
        _runtime.Published += Runtime_Published;

        // История последних открытых ресурсов: список читается здесь один раз и
        // дальше живёт в памяти, чтобы клик по нему не ходил на диск.
        _recentQuestFiles = RecentFileList.FromPaths(
            AppUiPreferencesStore.LoadRecentFiles(RecentFileKind.Quest));
        _recentSceneFiles = RecentFileList.FromPaths(
            AppUiPreferencesStore.LoadRecentFiles(RecentFileKind.Scene));
        PruneRecentFiles();
        _recentQuestFiles.Changed += RecentQuestFiles_Changed;
        _recentSceneFiles.Changed += RecentSceneFiles_Changed;

        BrowserReady += MainForm_BrowserReady;
        FormClosed += (_, _) =>
        {
            BrowserReady -= MainForm_BrowserReady;
            _sceneRuntime.Published -= SceneRuntime_Published;
            _runtime.Published -= Runtime_Published;
            _sceneCatalog.Changed -= SceneCatalog_Changed;
            _runtime.Dispose();
            _recentQuestFiles.Changed -= RecentQuestFiles_Changed;
            _recentSceneFiles.Changed -= RecentSceneFiles_Changed;

            foreach (var editor in _editors.ToArray())
            {
                editor.Close();
            }

            _simulator?.Close();
            _settings?.Close();
            _junctionReview?.Close();
        };
    }

    /// <summary>
    /// Открывает окно ручной проверки перекрёстков (одиночный экземпляр).
    ///
    /// Одно окно на приложение: проверка — это состояние (исключённые и
    /// добавленные узлы), и два окна показывали бы его по-разному.
    /// </summary>
    private void OpenJunctionReview()
    {
        if (_junctionReview is not null && !_junctionReview.IsDisposed)
        {
            _junctionReview.WindowState = FormWindowState.Normal;
            _junctionReview.BringToFront();
            _junctionReview.Activate();
            return;
        }

        _junctionReview = new JunctionReviewForm(_roads, _junctions);
        _junctionReview.FormClosed += (_, _) => _junctionReview = null;
        _junctionReview.Show(this);
    }

    private void MainForm_BrowserReady(object? sender, EventArgs e)
    {
        // Начальное состояние чипа: симуляция не запускается автоматически, но
        // после перезагрузки страницы web-сторона снова ждёт актуальное значение.
        PostSimulatorState();
        OpenSimulator();

        var startupPath = FileActivationRequest.Consume();
        if (!string.IsNullOrWhiteSpace(startupPath))
        {
            BeginInvoke(() => OpenStartupResource(startupPath));
        }
    }

    /// <summary>
    /// Обрабатывает запрос от повторного запуска приложения (двойной клик по
    /// файлу при уже открытом редакторе).
    ///
    /// Пустой путь означает запуск без файла: окно просто показывается и
    /// активируется. Непустой путь открывается в соответствующем редакторе;
    /// если в нём есть несохранённые изменения, редактор сначала спросит, как
    /// с ними поступить.
    /// </summary>
    public void HandleExternalActivation(string? path)
    {
        if (IsDisposed)
            return;

        AppLogger.Info("Внешний запуск: обработка запроса.", $"path={path ?? "<none>"}");

        RevealAndActivate();

        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            AppLogger.Warn("Внешний запуск: файл не найден.", path);
            MessageBox.Show(
                this,
                "Файл не найден:\r\n\r\n" + path,
                "Открытие ресурса",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        OpenStartupResource(path);
    }

    private void RevealAndActivate()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        Show();
        BringToFront();
        Activate();
    }

    private void OpenStartupResource(string path)
    {
        if (!ResourceFileTypes.TryGet(Path.GetExtension(path), out var resource))
        {
            MessageBox.Show(this, "Неизвестный тип Assist Quest ресурса: " + Path.GetExtension(path),
                "Открытие ресурса", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (resource.Kind.Equals("Campaign", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            return;
        }

        var pane = resource.Kind switch
        {
            "Quest" => "graph",
            "Scene" => "scene",
            "Location" => "locations",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(pane))
        {
            MessageBox.Show(this, resource.FriendlyName + " зарегистрирован в системе, но соответствующий редактор ещё не реализован.",
                "Открытие ресурса", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Открытие файла по ассоциации ведёт себя как навигация по ресурсу:
        // ресурс показывается в подходящем окне, а не в новом поверх него.
        var editor = EnsureEditorFor(pane);
        if (editor is null)
            return;

        try
        {
            editor.OpenResourcePath(path);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Не удалось открыть ресурс через ассоциацию файла.", ex, "path=" + path);
            MessageBox.Show(this, ex.Message, "Ошибка открытия ресурса", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var action = root.GetProperty("action").GetString();

            switch (action)
            {
                case "open_editor":
                    OpenEditor(root.GetProperty("editor").GetString() ?? "graph");
                    break;

                case "open_simulator":
                    OpenSimulator();
                    break;

                // Ручная проверка перекрёстков: отдельное окно, потому что это
                // длительная работа с картой, а не краткая команда.
                case "open_junction_review":
                    OpenJunctionReview();
                    break;

                case "reset_simulator":
                    if (_hub is SimulatorDataChannelHub simulatorHub)
                    {
                        simulatorHub.Reset();
                        _simulator?.RequestSnapshot("main reset");
                    }
                    break;

                case "push_logs":
                    StartLogPush();
                    break;

                case "open_settings":
                    OpenSettings();
                    break;
            }
        }
        catch (Exception ex)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "host_error",
                message = "Ошибка команды интерфейса: " + ex.Message
            }));
        }
    }

    private int _logPushInProgress;

    private void StartLogPush()
    {
        if (Interlocked.Exchange(ref _logPushInProgress, 1) != 0)
            return;

        PostLogsPushState("running", "Выполняется загрузка LOGS в GitHub…");

        _ = Task.Run(() =>
        {
            GitLogPushResult result;
            try
            {
                result = GitLogPublisher.PublishLogs();
                if (!result.Success)
                {
                    AppLogger.Error(
                        "GitHub: загрузка LOGS не выполнена.",
                        details: result.Message + Environment.NewLine + result.Details);
                }
                else
                {
                    // После успешного commit/push журнал уже находится в GitHub.
                    // Все последующие события процесса до выхода должны оставаться
                    // только визуальными, иначе они загрязнят опубликованный файл.
                    AppLogger.SuppressWrites();
                }
            }
            catch (Exception ex)
            {
                result = new(false, "Непредвиденная ошибка при загрузке LOGS.", ex.ToString());
                AppLogger.Error("GitHub: непредвиденная ошибка загрузки LOGS.", ex);
            }

            Interlocked.Exchange(ref _logPushInProgress, 0);

            try
            {
                BeginInvoke(() => PostLogsPushState(
                    result.Success ? "success" : "error",
                    result.Success ? result.Message : result.Message + " " + result.Details));
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void PostLogsPushState(string state, string message)
    {
        PostJson(JsonSerializer.Serialize(new
        {
            type = "logs_push_state",
            state,
            message
        }));
    }

    /// <summary>
    /// Пересылает в web-сторону факт запуска/остановки симуляции.
    ///
    /// Подписка на события Runtime нужна потому, что «Запустить симуляцию»
    /// нажимается в окне симулятора, а индикатор живёт в шапке главного окна.
    /// Обрабатываются только события смены режима: они публикуются исключительно
    /// при реальном изменении, а остальные события приходят постоянно и рассылать
    /// их было бы лишней работой.
    /// </summary>
    private void Runtime_Published(object? sender, QuestRuntimeEvent e)
    {
        if (!e.EventType.Equals("SimulationStarted", StringComparison.OrdinalIgnoreCase) &&
            !e.EventType.Equals("SimulationStopped", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PostSimulatorState();
    }

    private void PostSimulatorState()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "simulator_state",
                // Состояние читается у Runtime: он единственный источник истины,
                // поэтому чип совпадает с кнопкой в окне симулятора даже после
                // перезагрузки страницы.
                running = _runtime.SimulationRunning
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SceneCatalog_Changed(object? sender, EventArgs e)
    {
        foreach (var editor in _editors.ToArray())
        {
            if (!editor.IsDisposed)
                editor.RefreshSceneCatalog();
        }
    }

    private void SceneRuntime_Published(object? sender, SceneRuntimeEvent e)
    {
        AppLogger.Info(
            "Scene Runtime: событие.",
            $"event={e.EventType}; scene={e.SceneId}; node={e.NodeId ?? "<none>"}; " +
            $"choice={e.ChoiceId ?? "<none>"}; message={e.Message}");
    }

    /// <summary>
    /// Регистрирует открытый ресурс в истории последних файлов.
    /// Вызывается редакторами при открытии и сохранении документа.
    /// </summary>
    public void NoteRecentFile(string kind, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var list = ResolveRecentList(kind);
        if (list is null)
            return;

        if (list.Touch(path))
            AppLogger.Info("История файлов: открыт ресурс.", $"kind={kind}; path={path}");
    }

    /// <summary>Список последних файлов указанного вида для отправки в UI.</summary>
    public IReadOnlyList<string> GetRecentFiles(string kind) =>
        ResolveRecentList(kind)?.Items ?? Array.Empty<string>();

    /// <summary>
    /// Убирает недоступный ресурс из истории: файл удалён или переименован.
    /// </summary>
    public void ForgetRecentFile(string kind, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var list = ResolveRecentList(kind);
        if (list is null)
            return;

        if (list.Remove(path))
            AppLogger.Info("История файлов: удалён недоступный ресурс.", $"kind={kind}; path={path}");
    }

    private RecentFileList? ResolveRecentList(string kind) =>
        kind switch
        {
            RecentFileKind.Quest => _recentQuestFiles,
            RecentFileKind.Scene => _recentSceneFiles,
            _ => null
        };

    private void PruneRecentFiles()
    {
        // Битые пути (файл удалён или переименован) не должны висеть в списке.
        if (_recentQuestFiles.PruneMissing(File.Exists))
            AppLogger.Info("История файлов: удалены недоступные Quest-пути.");

        if (_recentSceneFiles.PruneMissing(File.Exists))
            AppLogger.Info("История файлов: удалены недоступные Scene-пути.");
    }

    private void RecentQuestFiles_Changed(object? sender, EventArgs e) =>
        PersistRecentFiles(RecentFileKind.Quest, _recentQuestFiles);

    private void RecentSceneFiles_Changed(object? sender, EventArgs e) =>
        PersistRecentFiles(RecentFileKind.Scene, _recentSceneFiles);

    private void PersistRecentFiles(string kind, RecentFileList list)
    {
        AppUiPreferencesStore.SaveRecentFiles(kind, list.Items);
        PostRecentFiles(kind);
    }

    /// <summary>Рассылает обновлённый список во все открытые редакторы.</summary>
    private void PostRecentFiles(string kind)
    {
        var paths = GetRecentFiles(kind);

        foreach (var editor in _editors.ToArray())
        {
            if (!editor.IsDisposed)
                editor.UpdateRecentFiles(kind, paths);
        }
    }

    private void PostAllRecentFiles()
    {
        PostRecentFiles(RecentFileKind.Quest);
        PostRecentFiles(RecentFileKind.Scene);
    }

    private void OpenSettings()
    {
        if (_settings is not null && !_settings.IsDisposed)
        {
            _settings.BringToFront();
            _settings.Activate();
            return;
        }

        _settings = new SettingsForm();
        _settings.ResetWindowSettingsRequested += Settings_ResetWindowSettingsRequested;
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show(this);
    }

    private void Settings_ResetWindowSettingsRequested(object? sender, EventArgs e)
    {
        WindowGeometryStore.ClearSavedGeometry();
    }

    private string? ResolveScenePath(string sceneId)
    {
        return _campaignStore.FindScenePath(sceneId)
            ?? Path.Combine(AppPaths.ResourceRoot, "scenes", sceneId + ".aqscene");
    }

    /// <summary>
    /// Открытие редактора из основной формы (кнопка «Открыть»).
    ///
    /// Семантика: каждый редактор открывается СВОИМ окном. Повторный клик по
    /// тому же редактору поднимает уже открытое окно, а не создаёт второе —
    /// иначе кнопка плодила бы дубликаты документов одного ресурса.
    /// </summary>
    private void OpenEditor(string editor)
    {
        var targetPage = EditorPageFor(editor).Page;
        var existing = FindEditor(form =>
            form.ActivePanePage.Equals(targetPage, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Activate();
            return;
        }

        CreateEditor(editor);
    }

    /// <summary>
    /// Создаёт НОВОЕ окно редактора с указанной активной панелью.
    ///
    /// Используется кнопкой «Открыть» и навигацией между ресурсами: одному
    /// ресурсу нужно своё окно, даже если другое окно уже показывает другую
    /// панель того же редактора.
    /// </summary>
    private EditorForm CreateEditor(string editor)
    {
        var page = EditorPageFor(editor);
        var form = new EditorForm(
            page.Title,
            page.Page,
            _hub,
            _questGraph,
            _sceneGraph,
            _sceneCatalog,
            _sceneDocument,
            _runtime,
            _locationStore,
            _roads,
            _junctions);

        _editors.Add(form);
        form.NavigationRequested += Editor_NavigationRequested;
        form.RecentFileOpened += (_, path) => NoteRecentFile(form.RecentFileKind, path);
        form.RecentFileUnavailable += (_, path) => ForgetRecentFile(form.RecentFileKind, path);
        form.DocumentSaved += Editor_DocumentSaved;
        form.LocationVisualisationRequested += Editor_LocationVisualisationRequested;
        form.FormClosed += (_, _) =>
        {
            form.NavigationRequested -= Editor_NavigationRequested;
            form.DocumentSaved -= Editor_DocumentSaved;
            form.LocationVisualisationRequested -= Editor_LocationVisualisationRequested;
            _editors.Remove(form);
        };
        PlaceAuxiliaryWindow(form, _editors.Count);
        form.Show(this);
        return form;
    }

    /// <summary>
    /// Окно, показывающее нужную панель. Возвращает null, если такого нет.
    /// Поиск идёт по фактической активной панели, а не по странице создания:
    /// окно могло переключиться на другую панель через сайдбар.
    /// </summary>
    private EditorForm? FindEditor(Func<EditorForm, bool> predicate) =>
        _editors.FirstOrDefault(editor => !editor.IsDisposed && predicate(editor));

    private static (string Title, string Page) EditorPageFor(string editor) =>
        editor.ToLowerInvariant() switch
        {
            "graph" => ("Нодовый редактор", "editor.html#graph"),
            "scene" => ("Редактор сцен", "editor.html#scene"),
            "dialogue" => ("Рабочее пространство диалогов", "editor.html#dialogue"),
            "world" => ("Редактор мира", "editor.html#world"),
            "locations" or "location" => ("Редактор локаций", "editor.html#locations"),
            "channels" => ("Инспектор каналов", "editor.html#channels"),
            "conditions" => ("Редактор условий и действий", "editor.html#conditions"),
            "localization" => ("Редактор локализации", "editor.html#localization"),
            "validation" => ("Проверка проекта", "editor.html#validation"),
            "registry" => ("Реестр нод и схем", "editor.html#registry"),
            _ => ("Редактор", "editor.html#graph")
        };

    /// <summary>
    /// Возвращает окно, показывающее нужную панель, создавая его при
    /// необходимости. Существующее окно переключается на панель через
    /// <see cref="EditorForm.ActivatePane"/>, а не заменяется новым окном:
    /// навигация между ресурсами продолжает работать в одном окне, пока
    /// пользователь сам не откроет ещё одно кнопкой «Открыть».
    /// </summary>
    private EditorForm? EnsureEditorFor(string pane)
    {
        var targetPage = EditorPageFor(pane).Page;
        var existing = FindEditor(form =>
            form.ActivePanePage.Equals(targetPage, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing;

        var form = CreateEditor(pane);

        // Окно могло не переключиться (пользователь отменил смену контекста
        // из-за несохранённых изменений) — тогда навигацию вести некуда.
        return form.ActivePanePage.Equals(targetPage, StringComparison.OrdinalIgnoreCase)
            ? form
            : null;
    }

    /// <summary>
    /// Сохранение документа в редакторе должно быть видно на карте Симулятора.
    ///
    /// Карта строит маркеры квестов из кэшированного каталога кампании, поэтому
    /// без перечитывания файлов правка точки активации не отображалась бы до
    /// перезапуска окна симулятора. Каталог перечитывает только Quest-редактор:
    /// сохранение сцены требует лишь свежего снимка, а лишний Reload зря
    /// перечитывал бы все файлы кампаний.
    /// </summary>
    private void Editor_DocumentSaved(object? sender, string path)
    {
        // Имя переменной цикла не может совпадать с pattern-переменной editor
        // ниже: это давало CS0136 (локальная переменная используется во
        // включающей области для определения локальной переменной).
        foreach (var window in _editors.ToArray())
        {
            if (!window.IsDisposed)
                window.RefreshLocationCatalog();
        }

        if (string.Equals(Path.GetExtension(path), LocationStore.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _locationResolver.Reset();
        }

        if (_simulator is null || _simulator.IsDisposed)
        {
            return;
        }

        if (sender is EditorForm editor && editor.RecentFileKind != App.RecentFileKind.Quest)
        {
            _simulator.RequestSnapshot("document saved: " + Path.GetFileName(path));
            return;
        }

        _simulator.ReloadCatalog("document saved: " + Path.GetFileName(path));
    }

    private void Editor_NavigationRequested(object? sender, EditorNavigationRequestEventArgs e)
    {
        switch (e.Action)
        {
            case "open_editor":
                OpenEditor(e.Editor ?? "graph");
                break;

            case "graph_open_scene":
                // Источник нужен, чтобы вернуть Scene в тот Quest, из которого ушли:
                // открытых нодовых редакторов может быть несколько.
                OpenSceneFromQuestNode(e.NodeId, sender as EditorForm);
                break;
            case "navigate_to_quest_node":
                OpenQuestNodeFromScene(e.NodeId, e.QuestId, e.QuestPath);
                break;
        }
    }

    /// <summary>
    /// Показывает набор точек Location в режиме визуализации на основной карте.
    ///
    /// Редактор локаций не знает про окно Симулятора (событие вместо ссылки),
    /// поэтому именно здесь решается, кто покажет точки. Окно Симулятора
    /// открывается/поднимается само: иначе режим выглядел бы как «кнопка ничего
    /// не делает» при закрытом симуляторе.
    /// </summary>
    private void Editor_LocationVisualisationRequested(object? sender, LocationVisualisationRequest request)
    {
        OpenSimulator();
        _simulator?.ShowLocationVisualisation(request);
    }

    private void OpenSceneFromQuestNode(string nodeId, EditorForm? sourceEditor)
    {
        var node = _questGraph.FindNode(nodeId);
        if (node is null)
        {
            MessageBox.Show(this, "Нода не найдена в текущем Quest Graph: " + nodeId,
                "Навигация по ресурсу", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var reference = QuestNodeReferenceCatalog.GetReferences(node)
            .FirstOrDefault(item => item.ResourceKind.Equals("Scene", StringComparison.OrdinalIgnoreCase));

        if (reference is null)
        {
            MessageBox.Show(this, "У ноды «" + node.Title + "» нет заполненной ссылки на Scene.",
                "Навигация по ресурсу", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_sceneCatalog.TryGetScene(reference.ResourceId, out var scene))
        {
            MessageBox.Show(this, "Scene «" + reference.ResourceId + "» не найдена в Scene Catalog.",
                "Навигация по ресурсу", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var path = ResolveScenePath(scene.Id);
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Для Scene «" + scene.Id + "» не найден файл .aqscene.",
                "Навигация по ресурсу", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var sceneEditor = EnsureEditorFor("scene");
        if (sceneEditor is null)
            return;

        try
        {
            sceneEditor.OpenSceneFromQuestNode(
                path,
                node.NodeId,
                node.Title,
                _questGraph.Value.Id,
                sourceEditor?.CurrentQuestPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "Не удалось перейти из Quest Graph в Scene.",
                ex,
                "node=" + node.NodeId + "; scene=" + scene.Id);
            MessageBox.Show(this, ex.Message, "Навигация по ресурсу",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenQuestNodeFromScene(string nodeId, string? questId, string? questPath)
    {
        var graphEditor = EnsureEditorFor("graph");
        if (graphEditor is null)
            return;

        try
        {
            if (!string.IsNullOrWhiteSpace(questPath) && File.Exists(questPath))
            {
                if (!graphEditor.OpenQuestResourceAndSelectNode(questPath, nodeId))
                    return;

                return;
            }

            if (!string.IsNullOrWhiteSpace(questId) &&
                !_questGraph.Value.Id.Equals(questId, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    "Исходный Quest resource «" + questId + "» больше недоступен.",
                    "Возврат в Quest Graph",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (_questGraph.FindNode(nodeId) is null)
            {
                MessageBox.Show(
                    this,
                    "Связанная Quest Graph нода больше не существует: " + nodeId,
                    "Возврат в Quest Graph",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            graphEditor.FocusQuestNode(nodeId);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "Не удалось вернуться из Scene в исходный Quest node.",
                ex,
                "quest=" + (questId ?? "<current>") + "; path=" + (questPath ?? "<none>") + "; node=" + nodeId);
            MessageBox.Show(this, ex.Message, "Возврат в Quest Graph",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSimulator()
    {
        if (_simulator is not null && !_simulator.IsDisposed)
        {
            _simulator.WindowState = FormWindowState.Normal;
            _simulator.BringToFront();
            _simulator.Activate();
            return;
        }

        _simulator = new SimulatorForm(
            _hub,
            _runtime,
            _questGraph,
            _campaignStore,
            path => OpenStartupResource(path),
            _locationResolver,
            _roads);
        _simulator.FormClosed += (_, _) => _simulator = null;
        PlaceOnSecondaryScreen(_simulator);
        _simulator.Show(this);
    }

    private void PlaceOnSecondaryScreen(Form form)
    {
        if (WindowGeometryStore.HasSaved("simulator"))
        {
            return;
        }

        var screens = Screen.AllScreens;
        var target = screens.Length > 1
            ? screens.FirstOrDefault(screen => !screen.Primary)
            : Screen.PrimaryScreen;

        if (target is null)
        {
            form.StartPosition = FormStartPosition.CenterScreen;
            return;
        }

        var area = target.WorkingArea;
        form.Bounds = new Rectangle(
            area.Left + 12,
            area.Top + 12,
            Math.Max(1000, area.Width - 24),
            Math.Max(700, area.Height - 24));
        form.WindowState = FormWindowState.Normal;
    }

    private void PlaceAuxiliaryWindow(Form form, int offset)
    {
        if (form is EditorForm editorForm && WindowGeometryStore.HasSaved(editorForm.WindowKey))
        {
            return;
        }

        var screens = Screen.AllScreens;
        var target = screens.FirstOrDefault(screen => screen.Primary) ?? Screen.PrimaryScreen;
        if (target is null)
        {
            return;
        }

        var area = target.WorkingArea;
        var x = Math.Min(area.Right - form.Width - 20, area.Left + 40 + (offset % 4) * 36);
        var y = Math.Min(area.Bottom - form.Height - 20, area.Top + 70 + (offset % 4) * 36);
        form.Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }
}
