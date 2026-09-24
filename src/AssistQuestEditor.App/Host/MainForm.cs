using System.Diagnostics;
using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class MainForm : WebViewForm
{
    private readonly IDataChannelHub _hub;
    private CampaignStore _campaignStore;
    private readonly QuestGraphStore _questGraph;
    private readonly SceneCatalog _sceneCatalog;
    private readonly SceneGraphStore _sceneGraph;
    private readonly SceneDocumentSession _sceneDocument;
    private readonly SceneRuntime _sceneRuntime;
    private readonly LocationStore _locationStore;
    private readonly LocationRuntimeResolver _locationResolver;
    private readonly DynamicEventStore _dynamicEventStore;
    private readonly IDynamicEventDispatcher _dynamicEventDispatcher;
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
    /// Черты городов, нарисованные автором вручную.
    ///
    /// Отдаются критериям «В любом городе» и «В черте города X», и окну
    /// рисования. В отличие от дорог и перекрёстков это ПОЛЬЗОВАТЕЛЬСКИЕ данные:
    /// в поставке их нет, пока автор не обвёл города.
    /// </summary>
    private readonly ICityBoundarySource _cityBoundaries;

    /// <summary>
    /// Точки мира для окна черт.
    ///
    /// Кэшируются в форме, а не читаются из канала при каждом сохранении черты:
    /// окно живёт долго и должно работать независимо от состояния каналов.
    /// </summary>
    private readonly IReadOnlyList<WorldPoint> _worldPoints;

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
    private CityBoundaryForm? _cityBoundaryForm;

    /// <summary>
    /// Каталог миров и псевдоним автора.
    ///
    /// Стор создаётся в конструкторе, а не берётся из Program: главная форма —
    /// единственное место, которое делает с мирами действия (импорт архива), и
    /// передавать сюда готовый стор значило бы держать два источника истины о
    /// состоянии каталога.
    ///
    /// В CI-режиме стор открыт ТОЛЬКО ДЛЯ ЧТЕНИЯ: прогон не должен менять
    /// пользовательские миры, но обязан уметь их показать.
    /// </summary>
    private readonly WorldStore _worldStore;

    /// <summary>
    /// Режим CI test. Запоминается потому, что от него зависит ЗАПИСЬ настроек:
    /// прогон обязан быть неинтерактивным и не должен менять файлы пользователя.
    /// </summary>
    private readonly bool _ciTest;

    private AppUiPreferences _preferences;

    public MainForm(
        IDataChannelHub hub,
        bool ciTest = false,
        RoadIndex? roads = null,
        JunctionIndex? junctions = null,
        ICityBoundarySource? cityBoundaries = null,
        IReadOnlyList<WorldPoint>? worldPoints = null)
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
        _cityBoundaries = cityBoundaries ?? new StaticCityBoundarySource();
        _worldPoints = worldPoints ?? Array.Empty<WorldPoint>();
        _ciTest = ciTest;
        // Настройки и каталог миров читаются ДО создания стора кампаний: стор
        // ограничивается папкой выбранного мира, а «какой мир выбран» известно
        // только из настроек.
        _preferences = AppUiPreferencesStore.Load();
        // Псевдоним обязателен: им подписываются импортированные ресурсы. В CI
        // настройка не спрашивается, поэтому подставляется системная подпись —
        // иначе импорт в прогоне падал бы на пустом авторе.
        // Подпись пользователя вычисляется один раз: её читают и мир, и кампании,
        // и разойтись эти два значения не должны — иначе правка кампании
        // подписывалась бы другим автором, чем правка мира.
        var author = string.IsNullOrWhiteSpace(_preferences.Author)
            ? ResourceMetadata.DefaultAuthor(DateTimeOffset.Now)
            : _preferences.Author!;

        _worldStore = new WorldStore(AppPaths.UserRoot, author, readOnly: ciTest);

        _campaignStore = ciTest
            // В CI мир может отсутствовать вовсе (прогон не создаёт миров), и
            // тогда читается поставляемый каталог: прогон обязан быть
            // неинтерактивным, но контент ему нужен.
            ? new CampaignStore(Path.Combine(AppPaths.ResourceRoot, "campaigns"), readOnly: true)
            : new CampaignStore(AppPaths.UserQuestRoot, readOnly: false, author)
                .ScopedTo(SelectedWorld?.FolderPath);

        _questGraph = new QuestGraphStore(QuestDefinitionLoader.LoadDocumentOrFallback().Definition);        _sceneCatalog = SceneCatalogLoader.Load();
        var initialScene = _sceneCatalog.TryGetScene("ruslan_start", out var ruslanStart)
            ? ruslanStart
            : _sceneCatalog.Scenes.FirstOrDefault() ?? SceneCatalogFactory.CreateStarter().Scenes.First();
        _sceneGraph = new SceneGraphStore(initialScene);
        _locationStore = ciTest
            ? new LocationStore(Path.Combine(Path.GetTempPath(), "AssistQuestEditor-CI-Locations"), readOnly: true)
            : new LocationStore(
                AppPaths.UserLocationRoot,
                readOnly: false,
                additionalRoots: SelectedWorld is null
                    ? null
                    : new[] { Path.Combine(SelectedWorld.FolderPath, WorldPaths.LocationsFolder) });
        _locationResolver = new LocationRuntimeResolver(_locationStore, _hub, _roads, _junctions, _cityBoundaries);
        _dynamicEventStore = ciTest
            ? new DynamicEventStore(Path.Combine(Path.GetTempPath(), "AssistQuestEditor-CI-DynamicEvents"), readOnly: true)
            : new DynamicEventStore(
                AppPaths.UserDynamicEventRoot,
                readOnly: false,
                additionalRoots: SelectedWorld is null
                    ? null
                    : new[] { Path.Combine(SelectedWorld.FolderPath, WorldPaths.DynamicEventsFolder) });
        _dynamicEventDispatcher = new DynamicEventDispatcher(
            _hub,
            _locationResolver,
            () => _dynamicEventStore.Definitions,
            questRuntime: null);
        _sceneDocument = new SceneDocumentSession(
            initialScene.Id,
            ResolveScenePath(initialScene.Id),
            _preferences.LastSceneDefinitionPath);
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

        // Simulator сохраняет состояние мира при штатном закрытии, поэтому он
        // должен получить событие FormClosing MainForm до Dispose Runtime/Director.
        FormClosing += (_, _) => _simulator?.Close();

        BrowserReady += MainForm_BrowserReady;
        FormClosed += (_, _) =>
        {
            BrowserReady -= MainForm_BrowserReady;
            _sceneRuntime.Published -= SceneRuntime_Published;
            _runtime.Published -= Runtime_Published;
            _sceneCatalog.Changed -= SceneCatalog_Changed;
            _runtime.Dispose();
            _dynamicEventDispatcher.Dispose();
            _recentQuestFiles.Changed -= RecentQuestFiles_Changed;
            _recentSceneFiles.Changed -= RecentSceneFiles_Changed;

            foreach (var editor in _editors.ToArray())
            {
                editor.Close();
            }

            _settings?.Close();
            _dynamicEventDispatcher.Dispose();
            _runtime.Dispose();
            _junctionReview?.Close();
            _cityBoundaryForm?.Close();
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

    /// <summary>
    /// Открывает окно рисования черт городов (одиночный экземпляр).
    ///
    /// Одно окно на приложение: черта — это состояние (нарисованные области), и
    /// два окна показывали бы его по-разному. После закрытия черты перечитываются
    /// из файла — автор мог нарисовать их в другом окне или править файл руками.
    /// </summary>
    private void OpenCityBoundaries()
    {
        if (_cityBoundaryForm is not null && !_cityBoundaryForm.IsDisposed)
        {
            _cityBoundaryForm.WindowState = FormWindowState.Normal;
            _cityBoundaryForm.BringToFront();
            _cityBoundaryForm.Activate();
            return;
        }

        _cityBoundaryForm = new CityBoundaryForm(_roads, _worldPoints);
        _cityBoundaryForm.FormClosed += (_, _) => _cityBoundaryForm = null;
        _cityBoundaryForm.Show(this);
    }

    private void MainForm_BrowserReady(object? sender, EventArgs e)
    {
        // Начальное состояние чипа: симуляция не запускается автоматически, но
        // после перезагрузки страницы web-сторона снова ждёт актуальное значение.
        PostSimulatorState();
        // Селектор [МИР][КАМПАНИЯ] тоже рисуется из состояния Host: web-сторона
        // списков миров не знает, и без этого сообщения селектор был бы пустым.
        PostWorldSelection();
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

        // Архив открывается ДИАЛОГОМ ИМПОРТА, а не редактором: двойной клик по
        // .aqezip означает «посмотри, что внутри, и, если надо, установи».
        // Распаковка без показа содержимого была бы опасной — архив приходит
        // извне и может перезаписать работу автора.
        if (resource.Kind.Equals("Archive", StringComparison.OrdinalIgnoreCase))
        {
            ImportArchive(path);
            return;
        }

        if (resource.Kind.Equals("Campaign", StringComparison.OrdinalIgnoreCase))
        {
            OpenResourceFolder(path);
            return;
        }

        // Мир — ПРОЕКТ, а не документ: у него нет отдельного окна редактирования,
        // и осмысленное действие по двойному клику — открыть его папку. Так же
        // ведёт себя кампания. Без этой ветки мир попадал в общий ответ
        // «редактор не реализован», хотя редактировать в нём нужно не файл, а
        // содержимое папки.
        if (resource.Kind.Equals("World", StringComparison.OrdinalIgnoreCase))
        {
            OpenResourceFolder(path);
            return;
        }

        var pane = resource.Kind switch
        {
            "Quest" => "graph",
            "Scene" => "scene",
            "Location" => "locations",
            "DynamicEvent" => "events",
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

    /// <summary>
    /// Открывает папку ресурса в проводнике.
    ///
    /// Общий метод для мира и кампании: оба — проекты-контейнеры, и «открыть»
    /// для них означает показать файлы, а не документ. Молчаливое бездействие
    /// при отсутствии папки выглядело бы как сломанное действие, поэтому
    /// причина сообщается.
    /// </summary>
    private void OpenResourceFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(this,
                "Папка ресурса не найдена: " + (folder ?? path),
                "Открытие ресурса", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Не удалось открыть папку ресурса.", $"{folder}: {ex.Message}");
            MessageBox.Show(this, "Не удалось открыть папку: " + ex.Message,
                "Открытие ресурса", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Импорт архива <c>.aqezip</c>: показать содержимое, затем распаковать.
    ///
    /// Показ содержимого ДО распаковки — не удобство, а требование: архив может
    /// перезаписать существующий ресурс, и согласие вслепую недопустимо.
    ///
    /// Все три вида (мир, кампания, квест) идут через один диалог, потому что
    /// манифест у них общий. Но ВЫПОЛНЯЕТ импорт разный код: мир распаковывается
    /// в каталог миров, а кампания и квест — внутрь родителя, и родителя надо
    /// знать. Пока реализован импорт мира; для остальных видов выводится явное
    /// сообщение вместо молчаливой распаковки «куда-нибудь».
    /// </summary>
    private void ImportArchive(string archivePath)
    {
        ArchiveInspection inspection;
        try
        {
            inspection = WorldArchiveService.Inspect(archivePath);
        }
        catch (Exception ex)
        {
            // Повреждённый архив — ожидаемый случай (файл могли скопировать
            // неполностью). Пользователю нужно объяснение, а не исключение.
            AppLogger.Error("Импорт архива: не удалось прочитать архив.", ex, "path=" + archivePath);
            MessageBox.Show(this,
                "Не удалось прочитать архив." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Импорт архива", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var dialog = new ArchiveImportForm(inspection);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            if (inspection.Manifest.Kind.Equals(WorldArchiveKinds.World, StringComparison.OrdinalIgnoreCase))
            {
                var world = _worldStore.ImportWorldFromArchive(archivePath, dialog.OverwriteRequested);

                // Запоминается через `with`, а не присваиванием: AppUiPreferences —
                // запись, её поля менять нельзя (CS8852).
                _preferences = _preferences with { LastWorldId = world.Definition.Id };
                AppUiPreferencesStore.Save(_preferences);

                AppLogger.Info("Импорт архива: мир установлен.",
                    $"world={world.Definition.Id}; folder={world.FolderPath}; " +
                    $"overwrite={dialog.OverwriteRequested}");

                MessageBox.Show(this,
                    "Мир «" + world.DisplayName + "» импортирован в:" +
                    Environment.NewLine + world.FolderPath,
                    "Импорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Кампания и квест кладутся ВНУТРЬ родителя: файл кампании лежит в
            // папке мира, файл квеста — в папке кампании. Родитель спрашивается
            // явно, потому что положить ресурс в посторонний мир «по умолчанию»
            // значит показать автору пустоту там, где он его искал.
            var isQuest = inspection.Manifest.Kind.Equals(WorldArchiveKinds.Quest,
                StringComparison.OrdinalIgnoreCase);

            using var parentDialog = new ImportParentForm(
                isQuest ? "квеста" : "кампании",
                inspection.Manifest,
                _worldStore.Worlds,
                SelectedWorld?.Definition.Id,
                folder =>
                {
                    // Кампании читаются из ТОГО мира, который выбран в списке:
                    // кампании с одним id (например common) есть в каждом мире,
                    // и общий каталог отдал бы чужую.
                    var scoped = new CampaignStore(folder, readOnly: true);
                    return scoped.Records;
                });

            if (parentDialog.ShowDialog(this) != DialogResult.OK)
            {
                AppLogger.Info("Импорт архива: выбор родителя отменён.",
                    $"kind={inspection.Manifest.Kind}");
                return;
            }

            var parentWorld = parentDialog.SelectedWorld
                ?? throw new InvalidOperationException("Родительский мир не выбран.");

            ResourceImportResult imported;

            if (isQuest)
            {
                var parentCampaign = parentDialog.SelectedCampaign
                    ?? throw new InvalidOperationException("Родительская кампания не выбрана.");

                imported = ResourceImportService.ImportQuest(
                    _campaignStore, parentCampaign, inspection, dialog.OverwriteRequested);
            }
            else
            {
                imported = ResourceImportService.ImportCampaign(
                    parentWorld, inspection, dialog.OverwriteRequested);
            }

            // Импорт кампании меняет состав КАТАЛОГА текущего мира. Если он и
            // есть тот мир, куда положили ресурс, — симулятор обязан перечитать
            // каталог, иначе нового квеста на карте не будет.
            if (parentWorld.Definition.Id.Equals(SelectedWorld?.Definition.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _simulator?.ReloadCatalog("resource imported");
                _simulator?.RequestSnapshot("resource imported");
            }

            AppLogger.Info("Импорт архива: ресурс установлен.",
                $"kind={imported.Kind}; id={imported.Id}; target={imported.TargetPath}; " +
                $"world={parentWorld.Definition.Id}; overwrite={dialog.OverwriteRequested}");

            MessageBox.Show(this,
                (imported.Kind.Equals(WorldArchiveKinds.Quest, StringComparison.OrdinalIgnoreCase)
                    ? "Квест «"
                    : "Кампания «") + imported.DisplayName + "» импортирован в мир «" +
                parentWorld.DisplayName + "»:" + Environment.NewLine +
                imported.TargetPath,
                "Импорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);

            PostWorldSelection();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Импорт архива: ошибка распаковки.", ex, "path=" + archivePath);
            MessageBox.Show(this,
                "Импорт не выполнен." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Импорт архива", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Смена мира из селектора.
    ///
    /// Пока НЕ выполняется — говорится прямо, а не молча. Причины: стор кампаний
    /// и Runtime Quest созданы один раз на выбранном мире, а окна редакторов
    /// держат ссылки на те же объекты; корректная смена требует пересоздать их
    /// все и сбросить кэши (локации, сцены, каталог квестов). Молчаливая смена
    /// заголовка без смены содержимого — худший вид поломки: «выбрал мир, а
    /// квесты прежние».
    ///
    /// Уже СЕЙЧАС выбор запоминается в файле мира, поэтому он не теряется, и
    /// после перезапуска загрузится именно он.
    /// </summary>
    private void RequestWorldSwitch(string? worldId)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return;

        var current = SelectedWorld;
        var target = _worldStore.FindWorld(worldId);
        if (target is null)
        {
            PostJson(JsonSerializer.Serialize(new
            {
                type = "world_switch_result",
                ok = false,
                message = "Мир не найден: " + worldId
            }));
            return;
        }

        if (string.Equals(target.Definition.Id, current?.Definition.Id, StringComparison.OrdinalIgnoreCase))
            return;

        var reopenSimulator = _simulator is not null && !_simulator.IsDisposed;
        AppLogger.Info("MainForm: выполняется живая смена мира.",
            $"from={current?.Definition.Id ?? "<none>"}; to={target.Definition.Id}; reopenSimulator={reopenSimulator}");

        try
        {
            foreach (var editor in _editors.ToArray())
                editor.Close();
            _simulator?.Close();
            _simulator = null;

            var author = string.IsNullOrWhiteSpace(_preferences.Author)
                ? ResourceMetadata.DefaultAuthor(DateTimeOffset.Now)
                : _preferences.Author!;
            _campaignStore = new CampaignStore(AppPaths.UserQuestRoot, readOnly: _ciTest, author)
                .ScopedTo(target.FolderPath);

            var campaigns = _campaignStore.Records;
            var activeCampaignId = ResourceSelectorRules.ResolveCampaignId(
                target.Definition.LastCampaignId,
                campaigns.Select(item => item.Definition.Id));
            if (!_ciTest && activeCampaignId is not null)
                _worldStore.RememberCampaign(target.Definition.Id, activeCampaignId);

            _preferences = _preferences with
            {
                LastWorldId = target.Definition.Id,
                LastCampaignId = activeCampaignId
            };
            if (!_ciTest)
                AppUiPreferencesStore.Save(_preferences);

            var questDefinitions = _campaignStore.LoadEnabledQuestDefinitions();
            var selectedQuest = questDefinitions.FirstOrDefault();
            var runtimeDefinition = selectedQuest ?? QuestDefinitionLoader.LoadDocumentOrFallback().Definition;
            _questGraph.Replace(runtimeDefinition);

            if (_runtime is QuestRuntimeCoordinator coordinator)
            {
                coordinator.RebindDefinitions(
                    _campaignStore.LoadEnabledQuestDefinitions,
                    runtimeDefinition.Id);
            }

            _locationStore.SetAdditionalRoots(new[]
            {
                Path.Combine(target.FolderPath, WorldPaths.LocationsFolder)
            });
            _dynamicEventStore.SetAdditionalRoots(new[]
            {
                Path.Combine(target.FolderPath, WorldPaths.DynamicEventsFolder)
            });
            _locationResolver.Reset();

            PostWorldSelection();
            PostJson(JsonSerializer.Serialize(new
            {
                type = "world_switch_result",
                ok = true,
                worldId = target.Definition.Id,
                campaignId = activeCampaignId ?? string.Empty
            }));

            if (reopenSimulator)
                OpenSimulator();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MainForm: живая смена мира не выполнена.", ex,
                $"target={target.Definition.Id}");

            PostJson(JsonSerializer.Serialize(new
            {
                type = "world_switch_result",
                ok = false,
                message = "Не удалось переключить мир: " + ex.Message
            }));
            PostWorldSelection();

            MessageBox.Show(this,
                "Не удалось переключить мир: " + ex.Message,
                "Смена мира", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Выбор кампании внутри текущего мира.
    ///
    /// Кампания — часть уже открытого мира, поэтому её смена не требует
    /// пересоздания редакторов: запоминается в файле мира и уходит в Симулятор,
    /// который показывает состав кампании.
    /// </summary>
    private void SelectCampaign(string? campaignId)
    {
        var world = SelectedWorld;
        if (world is null || string.IsNullOrWhiteSpace(campaignId))
            return;

        var exists = _campaignStore.Records.Any(record =>
            record.Definition.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            AppLogger.Warn("MainForm: выбрана кампания, которой нет в мире.",
                $"worldId={world.Definition.Id}; campaignId={campaignId}");
            PostWorldSelection();
            return;
        }

        if (_worldStore.IsReadOnly)
        {
            // В CI-режиме каталог миров только для чтения: выбор не записывается,
            // но интерфейс всё равно должен показать актуальное состояние.
            PostWorldSelection();
            return;
        }

        _worldStore.RememberCampaign(world.Definition.Id, campaignId);
        _preferences = _preferences with
        {
            LastWorldId = world.Definition.Id,
            LastCampaignId = campaignId
        };
        AppUiPreferencesStore.Save(_preferences);

        AppLogger.Info("MainForm: кампания выбрана.",
            $"worldId={world.Definition.Id}; campaignId={campaignId}");

        _simulator?.RequestSnapshot("campaign selected");
        PostWorldSelection();
    }

    /// <summary>
    /// Открывает папку текущего мира в проводнике.
    ///
    /// Отсутствие папки сообщается явно: молчаливое бездействие выглядело бы
    /// как сломанная кнопка.
    /// </summary>
    private void OpenWorldFolder()
    {
        var folder = SelectedWorld?.FolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(this,
                "Папка мира не найдена: " + (folder ?? "мир не выбран"),
                "Папка мира", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    /// <summary>
    /// Выгружает текущий мир папкой либо архивом.
    ///
    /// Диалог показывается ВСЕГДА, даже когда выгружать нечего: молчаливое
    /// «ничего не произошло» не отличить от поломки пункта меню.
    ///
    /// Зависимости (кампании) считаются до показа диалога — по фактическому
    /// содержимому папки мира, а не по каталогу в памяти: выгружается диск, и
    /// обещать в диалоге то, чего на диске нет, нельзя.
    /// </summary>
    private void ImportArchiveFromDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Импорт Assist Quest архива",
            Filter = "Assist Quest archive (*.aqezip)|*.aqezip|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            ImportArchive(dialog.FileName);
    }

    private void ExportWorld()
    {
        var world = SelectedWorld;
        if (world is null)
        {
            MessageBox.Show(this, "Мир не выбран — выгружать нечего.",
                "Экспорт мира", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var source = world.FolderPath;
        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, "Папка мира не найдена: " + source,
                "Экспорт мира", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var moment = DateTimeOffset.UtcNow;
        var exportRoot = WorldPaths.ExportFolder(AppPaths.UserRoot, moment);
        var folderName = ResourceNaming.ToFolderName(world.DisplayName);

        var fileCount = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();
        var totalBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);

        var campaigns = Directory.Exists(WorldPaths.CampaignsRoot(source))
            ? Directory.EnumerateDirectories(WorldPaths.CampaignsRoot(source))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => "кампания «" + name + "»")
                .ToArray()
            : Array.Empty<string>();

        using var dialog = new ResourceExportForm(
            kindLabel: "мира",
            displayName: world.DisplayName,
            sourceFolder: source,
            fileCount: fileCount,
            totalBytes: totalBytes,
            dependencies: campaigns,
            folderDestination: Path.Combine(exportRoot, folderName) + Path.DirectorySeparatorChar,
            archiveDestination: Path.Combine(exportRoot, folderName + WorldArchiveRules.Extension));

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            AppLogger.Info("MainForm: экспорт мира отменён пользователем.",
                $"worldId={world.Definition.Id}");
            return;
        }

        try
        {
            var result = dialog.ArchiveRequested
                ? ResourceExportService.ExportArchive(
                    AppPaths.UserRoot,
                    source,
                    world.DisplayName,
                    ResourceExportService.ManifestForWorld(world),
                    moment)
                : ResourceExportService.ExportFolder(
                    AppPaths.UserRoot,
                    source,
                    world.DisplayName,
                    moment);

            AppLogger.Info("MainForm: мир выгружен.",
                $"worldId={world.Definition.Id}; archive={result.IsArchive}; " +
                $"path={result.Path}; files={result.FileCount}; bytes={result.Bytes}");

            MessageBox.Show(this,
                "Мир «" + world.DisplayName + "» выгружен " +
                (result.IsArchive ? "архивом" : "папкой") + "." +
                Environment.NewLine + Environment.NewLine +
                result.Path +
                Environment.NewLine + Environment.NewLine +
                "Файлов: " + result.FileCount + " · объём: " + FormatBytes(result.Bytes),
                "Экспорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainForm: экспорт мира не удался.",
                $"worldId={world.Definition.Id}; error={ex.Message}");
            MessageBox.Show(this, "Не удалось выгрузить мир: " + ex.Message,
                "Экспорт мира", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Выгружает кампанию текущего мира.
    ///
    /// Кампания выгружается только вместе с указанием родительского мира: при
    /// импорте её некуда положить иначе. Идентификатор родителя попадает в
    /// манифест, поэтому у получателя диалог импорта покажет, чья она.
    /// </summary>
    private void ExportCampaign(string? campaignId)
    {
        var world = SelectedWorld;
        if (world is null)
        {
            MessageBox.Show(this, "Мир не выбран — кампанию выгружать не из чего.",
                "Экспорт кампании", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var record = _campaignStore.Records.FirstOrDefault(item =>
            item.Definition.Id.Equals(campaignId ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        var resolved = record is not null;

        if (!resolved)
        {
            // Идентификатор не пришёл или не найден. Берётся активная кампания
            // мира — по ней селектор и выставлен, — но результат проверяется по
            // каталогу, а не принимается на веру.
            record = _campaignStore.Records.FirstOrDefault(item =>
                item.Definition.Id.Equals(world.Definition.LastCampaignId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
            resolved = record is not null;
        }

        if (!resolved && _campaignStore.Records.Count == 1)
        {
            // Единственная кампания — двусмысленности нет.
            record = _campaignStore.Records[0];
            resolved = true;
        }

        if (!resolved)
        {
            // Угадывать нечего: в мире несколько кампаний, а активная не
            // определилась — выгрузилась бы не та, и автор узнал бы об этом
            // только по содержимому архива.
            AppLogger.Warn("MainForm: экспорт кампании без определённой кампании.",
                $"worldId={world.Definition.Id}; requested={campaignId ?? "<none>"}; " +
                $"available={_campaignStore.Records.Count}");
            MessageBox.Show(this,
                "В мире «" + world.DisplayName + "» не определена активная кампания." +
                Environment.NewLine + Environment.NewLine +
                "Выберите кампанию в списке «КАМПАНИЯ» и повторите экспорт.",
                "Экспорт кампании", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Явная проверка вместо флага «resolved»: компилятор не выводит
        // непустоту из отдельной переменной, а Nullable включён на весь проект.
        var campaign = record ?? throw new InvalidOperationException(
            "Кампания не определена, хотя проверка это подтвердила.");

        var source = campaign.FolderPath;
        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, "Папка кампании не найдена: " + source,
                "Экспорт кампании", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var moment = DateTimeOffset.UtcNow;
        var exportRoot = WorldPaths.ExportFolder(AppPaths.UserRoot, moment);
        var displayName = string.IsNullOrWhiteSpace(record.Definition.FullName)
            ? record.Definition.Name
            : record.Definition.FullName!;
        var folderName = ResourceNaming.ToFolderName(displayName);

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        var questCount = files.Count(file =>
            file.EndsWith(".aqquest", StringComparison.OrdinalIgnoreCase));
        var sceneCount = files.Count(file =>
            file.EndsWith(".aqscene", StringComparison.OrdinalIgnoreCase));

        var dependencies = new List<string>();
        if (questCount > 0) dependencies.Add(questCount + " квест(ов)");
        if (sceneCount > 0) dependencies.Add(sceneCount + " сцен(ы)");

        using var dialog = new ResourceExportForm(
            kindLabel: "кампании",
            displayName: displayName,
            sourceFolder: source,
            fileCount: files.Length,
            totalBytes: files.Sum(file => new FileInfo(file).Length),
            dependencies: dependencies,
            folderDestination: Path.Combine(exportRoot, folderName) + Path.DirectorySeparatorChar,
            archiveDestination: Path.Combine(exportRoot, folderName + WorldArchiveRules.Extension));

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            AppLogger.Info("MainForm: экспорт кампании отменён пользователем.",
                $"campaignId={campaign.Definition.Id}");
            return;
        }

        try
        {
            var exportDependencies = dialog.DependenciesRequested
                ? new[]
                {
                    new ResourceExportService.ExportDependency(
                        world.FolderPath,
                        "world",
                        "мир «" + world.DisplayName + "»",
                        new[] { WorldPaths.WorldFileName, WorldPaths.WorldImageFileName })
                }
                : Array.Empty<ResourceExportService.ExportDependency>();

            var result = dialog.ArchiveRequested
                ? ResourceExportService.ExportArchive(
                    AppPaths.UserRoot,
                    source,
                    displayName,
                    ResourceExportService.ManifestForCampaign(
                        campaign,
                        world.Definition.Id,
                        campaign.Definition.Metadata),
                    moment,
                    exportDependencies)
                : ResourceExportService.ExportFolder(
                    AppPaths.UserRoot,
                    source,
                    displayName,
                    moment,
                    exportDependencies);

            AppLogger.Info("MainForm: кампания выгружена.",
                $"worldId={world.Definition.Id}; campaignId={record.Definition.Id}; " +
                $"archive={result.IsArchive}; path={result.Path}; files={result.FileCount}");

            MessageBox.Show(this,
                "Кампания «" + displayName + "» выгружена " +
                (result.IsArchive ? "архивом" : "папкой") + "." +
                Environment.NewLine + Environment.NewLine +
                result.Path +
                Environment.NewLine + Environment.NewLine +
                "В манифесте указан родительский мир: " + world.Definition.Id,
                "Экспорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainForm: экспорт кампании не удался.",
                $"campaignId={record.Definition.Id}; error={ex.Message}");
            MessageBox.Show(this, "Не удалось выгрузить кампанию: " + ex.Message,
                "Экспорт кампании", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportQuest(string campaignId, string questId, string path)
    {
        var world = SelectedWorld;
        if (world is null)
        {
            MessageBox.Show(this, "Мир не выбран — квест выгружать не из чего.",
                "Экспорт квеста", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(this, "Файл квеста не найден: " + path,
                "Экспорт квеста", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var campaign = _campaignStore.Records.FirstOrDefault(record =>
            record.Definition.Id.Equals(campaignId, StringComparison.OrdinalIgnoreCase));
        if (campaign is null)
        {
            MessageBox.Show(this, "Родительская кампания не найдена: " + campaignId,
                "Экспорт квеста", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        QuestDefinition quest;
        try
        {
            var document = ResourceJsonFormat.Deserialize<QuestDefinitionDocument>(File.ReadAllText(path));
            quest = document?.Definition
                ?? throw new InvalidDataException("Файл Quest не содержит определения.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось прочитать квест: " + ex.Message,
                "Экспорт квеста", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var moment = DateTimeOffset.UtcNow;
        var exportRoot = WorldPaths.ExportFolder(AppPaths.UserRoot, moment);
        var displayName = string.IsNullOrWhiteSpace(quest.Title) ? quest.Id : quest.Title;
        var fileSize = new FileInfo(path).Length;

        var sceneFiles = Directory.Exists(WorldPaths.ScenesFolderPath(campaign.FolderPath))
            ? Directory.EnumerateFiles(
                    WorldPaths.ScenesFolderPath(campaign.FolderPath),
                    "*.aqscene",
                    SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(campaign.FolderPath, file).Replace('\\', '/'))
                .ToArray()
            : Array.Empty<string>();

        var dependencies = new[]
        {
            new ResourceExportService.ExportDependency(
                world.FolderPath,
                "world",
                "мир «" + world.DisplayName + "»",
                new[] { WorldPaths.WorldFileName, WorldPaths.WorldImageFileName }),
            new ResourceExportService.ExportDependency(
                campaign.FolderPath,
                "campaign",
                "кампания «" +
                WorldDisplayRules.DisplayName(campaign.Definition.Name, campaign.Definition.FullName) +
                "» + сцены, без соседних квестов",
                new[] { WorldPaths.CampaignFileName, WorldPaths.CampaignImageFileName }
                    .Concat(sceneFiles)
                    .ToArray())
        };

        var dependencyNames = new[]
        {
            "мир «" + world.DisplayName + "»",
            "кампания «" +
            WorldDisplayRules.DisplayName(campaign.Definition.Name, campaign.Definition.FullName) + "»",
            "сцены кампании (" + sceneFiles.Length + ")"
        };

        using var dialog = new ResourceExportForm(
            kindLabel: "квеста",
            displayName: displayName,
            sourceFolder: Path.GetDirectoryName(path) ?? campaign.FolderPath,
            fileCount: 1,
            totalBytes: fileSize,
            dependencies: dependencyNames,
            folderDestination: Path.Combine(exportRoot, ResourceNaming.ToFolderName(displayName)) + Path.DirectorySeparatorChar,
            archiveDestination: Path.Combine(exportRoot, ResourceNaming.ToFolderName(displayName) + WorldArchiveRules.Extension));

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            ResourceExportResult result;
            if (dialog.ArchiveRequested)
            {
                result = ResourceExportService.ExportQuest(
                    AppPaths.UserRoot,
                    path,
                    displayName,
                    ResourceExportService.ManifestForQuest(quest),
                    moment,
                    asArchive: true,
                    dialog.DependenciesRequested ? dependencies : Array.Empty<ResourceExportService.ExportDependency>());
            }
            else
            {
                result = ResourceExportService.ExportQuest(
                    AppPaths.UserRoot,
                    path,
                    displayName,
                    ResourceExportService.ManifestForQuest(quest),
                    moment,
                    asArchive: false,
                    dialog.DependenciesRequested ? dependencies : Array.Empty<ResourceExportService.ExportDependency>());
            }

            MessageBox.Show(this,
                "Квест «" + displayName + "» выгружен " +
                (result.IsArchive ? "архивом" : "папкой") + "." +
                Environment.NewLine + Environment.NewLine +
                result.Path,
                "Экспорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainForm: экспорт квеста не удался.",
                $"questId={quest.Id}; error={ex.Message}");
            MessageBox.Show(this, "Не удалось выгрузить квест: " + ex.Message,
                "Экспорт квеста", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " Б";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " КБ";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ";
    }

    /// <summary>
    /// Показывает свойства мира или кампании — «Ред.» либо «ℹ️».
    ///
    /// Одно окно на оба вида ресурсов и на оба режима. Причина: «Ред.» и «ℹ️»
    /// спрашивают про одно и то же содержимое, и разные формы неизбежно
    /// расходились бы — правя описание, автор не видел бы автора и дату, по
    /// которым у получателя решается вопрос о перезаписи при импорте.
    ///
    /// Запись идёт ПОСЛЕ закрытия окна и только если было что менять: сохранение
    /// «того же самого» обновило бы modified_on, то есть изменило бы ресурс,
    /// не изменив ничего.
    /// </summary>
    private void ShowResourceProperties(string kind, bool edit, string? campaignId = null)
    {
        var world = SelectedWorld;
        if (world is null)
        {
            MessageBox.Show(this, "Мир не выбран.",
                "Свойства ресурса", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var isCampaign = kind.Equals("campaign", StringComparison.OrdinalIgnoreCase);
        var record = isCampaign ? ResolveCampaign(campaignId, world) : null;

        if (isCampaign && record is null)
        {
            MessageBox.Show(this,
                "В мире «" + world.DisplayName + "» не определена кампания." +
                Environment.NewLine + Environment.NewLine +
                "Выберите кампанию в списке «КАМПАНИЯ» и повторите.",
                "Свойства кампании", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var properties = isCampaign
            ? ResourcePropertiesService.DescribeCampaign(record!, world)
            : ResourcePropertiesService.DescribeWorld(world, _campaignStore);

        using var dialog = new ResourcePropertiesForm(properties, edit);
        var result = dialog.ShowDialog(this);

        if (!edit || result != DialogResult.OK)
        {
            AppLogger.Info("MainForm: окно свойств закрыто без изменений.",
                $"kind={kind}; edit={edit}; id={properties.Id}");
            return;
        }

        var imageFileName = properties.ImageFileName;

        if (dialog.ImageChanged)
        {
            try
            {
                imageFileName = dialog.PickedImagePath is { Length: > 0 } picked
                    ? ResourcePropertiesService.CopyImageIn(picked, properties.FolderPath,
                        properties.ImageFileName ?? WorldPaths.WorldImageFileName)
                    // Пустая строка означает «снять изображение»: файл остаётся
                    // на диске (его могли использовать другие ресурсы), но
                    // определение больше на него не ссылается.
                    : null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MainForm: изображение не скопировано.",
                    $"kind={kind}; id={properties.Id}; error={ex.Message}");
                MessageBox.Show(this,
                    "Не удалось применить изображение: " + ex.Message +
                    Environment.NewLine + Environment.NewLine +
                    "Остальные свойства не сохранены — исправьте и повторите.",
                    "Свойства ресурса", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        try
        {
            if (isCampaign)
            {
                _campaignStore.UpdateCampaign(
                    properties.Id,
                    dialog.ShortNameValue,
                    dialog.FullNameValue,
                    dialog.DescriptionValue,
                    imageFileName);

                _simulator?.ReloadCatalog("campaign properties changed");
                _simulator?.RequestSnapshot("campaign properties changed");
            }
            else
            {
                _worldStore.UpdateWorld(
                    properties.Id,
                    dialog.ShortNameValue,
                    dialog.FullNameValue,
                    dialog.DescriptionValue,
                    imageFileName);

                // Каталог кампаний привязан к ПАПКЕ мира, а она при правке имени
                // не меняется, поэтому перечитывать его не нужно. Обновляется
                // только то, что показывает имя мира.
                _simulator?.RequestSnapshot("world properties changed");
            }

            PostWorldSelection();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainForm: свойства ресурса не сохранены.",
                $"kind={kind}; id={properties.Id}; error={ex.Message}");
            MessageBox.Show(this, "Не удалось сохранить свойства: " + ex.Message,
                "Свойства ресурса", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show(this,
            (isCampaign ? "Кампания «" : "Мир «") + dialog.FullNameValue +
            (dialog.FullNameValue.Length == 0 ? dialog.ShortNameValue : string.Empty) +
            "» сохранён." + Environment.NewLine + Environment.NewLine +
            "Файл: " + (isCampaign ? record!.CampaignFilePath : world.WorldFilePath),
            "Свойства сохранены", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Кампания по id из селектора, иначе активная кампания мира.
    ///
    /// Возвращает <c>null</c>, когда определить нечего: угадывать между
    /// несколькими кампаниями нельзя — откроется не та, и автор узнает об этом
    /// только по содержимому.
    /// </summary>
    private CampaignStore.CampaignRecord? ResolveCampaign(string? campaignId, WorldRecord world)
    {
        var byId = _campaignStore.Records.FirstOrDefault(item =>
            item.Definition.Id.Equals(campaignId ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;

        var byRemembered = _campaignStore.Records.FirstOrDefault(item =>
            item.Definition.Id.Equals(world.Definition.LastCampaignId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
        if (byRemembered is not null)
            return byRemembered;

        return _campaignStore.Records.Count == 1 ? _campaignStore.Records[0] : null;
    }

    /// <summary>
    /// Создаёт новый мир.
    ///
    /// Результат НЕ открывается здесь же: мир только что создан, и смена
    /// открытого мира на живом окне пока не реализована (редакторы и Runtime
    /// держат объекты прежнего мира). Поэтому смена мира запоминается и автору
    /// говорится прямо, что произойдёт при следующем запуске, — вместо
    /// молчаливой подмены заголовка без смены содержимого.
    /// </summary>
    private void CreateWorld()
    {
        var parent = WorldPaths.WorldsRoot(AppPaths.UserRoot);

        using var dialog = new ResourceCreateForm("мир", parent);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            AppLogger.Info("MainForm: создание мира отменено пользователем.");
            return;
        }

        try
        {
            var world = _worldStore.CreateWorld(
                dialog.NameValue,
                dialog.FullNameValue.Length == 0 ? null : dialog.FullNameValue,
                dialog.DescriptionValue.Length == 0 ? null : dialog.DescriptionValue);

            // Запоминается через `with`, а не присваиванием: AppUiPreferences —
            // запись, её поля менять нельзя (CS8852).
            _preferences = _preferences with { LastWorldId = world.Definition.Id };
            AppUiPreferencesStore.Save(_preferences);

            AppLogger.Info("MainForm: мир создан.",
                $"worldId={world.Definition.Id}; folder={world.FolderPath}");

            // Селектор обновляется сразу: созданный мир обязан появиться в списке,
            // иначе «создал мир, а его нигде нет».
            PostWorldSelection();

            MessageBox.Show(this,
                "Мир «" + world.DisplayName + "» создан." + Environment.NewLine + Environment.NewLine +
                world.FolderPath + Environment.NewLine + Environment.NewLine +
                "Общая кампания с демонстрационным квестом создана автоматически." +
                Environment.NewLine + Environment.NewLine +
                "Мир выбран и запомнен. Переключить мир можно в верхнем селекторе без перезапуска приложения.",
                "Мир создан", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainForm: мир не создан.", ex.Message);
            MessageBox.Show(this, "Не удалось создать мир: " + ex.Message,
                "Новый мир", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Создаёт новую кампанию в текущем мире.
    ///
    /// В отличие от мира, кампания появляется в УЖЕ открытом мире, поэтому она
    /// сразу попадает в селектор и её можно начать наполнять. Активной она не
    /// становится: активная кампания задаёт мир симуляции, и «создал кампанию —
    /// сменил мир» было бы неожиданным побочным эффектом.
    /// </summary>
    private void CreateCampaign()
    {
        var world = SelectedWorld;
        if (world is null)
        {
            MessageBox.Show(this,
                "Мир не выбран: кампания создаётся внутри мира.",
                "Новая кампания", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var parent = WorldPaths.CampaignsRoot(world.FolderPath);

        using var dialog = new ResourceCreateForm("кампания", parent);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            AppLogger.Info("MainForm: создание кампании отменено пользователем.");
            return;
        }

        try
        {
            var campaign = _campaignStore.CreateCampaign(
                world.Definition.Id,
                dialog.NameValue,
                dialog.FullNameValue.Length == 0 ? null : dialog.FullNameValue,
                dialog.DescriptionValue.Length == 0 ? null : dialog.DescriptionValue);

            AppLogger.Info("MainForm: кампания создана.",
                $"worldId={world.Definition.Id}; campaignId={campaign.Definition.Id}");

            _simulator?.ReloadCatalog("campaign created");
            _simulator?.RequestSnapshot("campaign created");
            PostWorldSelection();

            MessageBox.Show(this,
                "Кампания «" + WorldDisplayRules.DisplayName(campaign.Definition.Name,
                    campaign.Definition.FullName) + "» создана в мире «" +
                world.DisplayName + "»." + Environment.NewLine + Environment.NewLine +
                campaign.FolderPath + Environment.NewLine + Environment.NewLine +
                "Кампания отключена: включите её, когда будете готовы играть. " +
                "Квесты добавляются в папке quests.",
                "Кампания создана", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainForm: кампания не создана.", ex.Message);
            MessageBox.Show(this, "Не удалось создать кампанию: " + ex.Message,
                "Новая кампания", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Сообщает, что действие из меню ещё не реализовано.
    ///
    /// Заглушка «ничего не делать» недопустима: пункт меню выглядел бы сломанным,
    /// и пользователь искал бы причину в своём мире. Текст называет пункт, чтобы
    /// было понятно, о чём речь.
    /// </summary>
    private void NotifyNotImplemented(string action)
    {
        var label = action switch
        {
            "world_info" => "Сведения о мире",
            "world_edit" => "Редактирование мира",
            "campaign_info" => "Сведения о кампании",
            "campaign_edit" => "Редактирование кампании",
            "export_world" => "Экспорт мира",
            "export_campaign" => "Экспорт кампании",
            "import_archive" => "Импорт архива",
            "create_world" => "Создание мира",
            "create_campaign" => "Создание кампании",
            _ => action
        };

        AppLogger.Info("MainForm: пункт меню ещё не реализован.", "action=" + action);
        MessageBox.Show(this,
            label + " пока не реализовано — этот пункт появится в следующих обновлениях." +
            Environment.NewLine + Environment.NewLine +
            "Сейчас доступны: выбор и запоминание мира, выбор кампании и папка мира.",
            "Ещё не реализовано", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

                // Черты городов: тоже длительная работа с картой и своё состояние.
                case "open_city_boundaries":
                    OpenCityBoundaries();
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

                // --- Селектор мира и кампании ---
                //
                // Смена мира выполняется через единый Host-контекст: закрываются
                // старые editor/simulator окна, перепривязываются CampaignStore и
                // Quest Runtime, затем селекторы получают новый фактический мир.
                case "select_world":
                    RequestWorldSwitch(root.TryGetProperty("worldId", out var worldNode)
                        ? worldNode.GetString()
                        : null);
                    break;

                case "select_campaign":
                    SelectCampaign(root.TryGetProperty("campaignId", out var campaignNode)
                        ? campaignNode.GetString()
                        : null);
                    break;

                case "open_world_folder":
                    OpenWorldFolder();
                    break;

                // Экспорт не просто «сохранить куда-то»: от галочки «Архивация в
                // aqezip» зависит и содержимое, и вид результата, поэтому решение
                // собирается диалогом ДО записи на диск.
                case "export_world":
                    ExportWorld();
                    break;

                case "export_campaign":
                    ExportCampaign(root.TryGetProperty("campaignId", out var exportCampaignNode)
                        ? exportCampaignNode.GetString()
                        : null);
                    break;

                case "world_info":
                    ShowResourceProperties(kind: "world", edit: false);
                    break;

                case "world_edit":
                    ShowResourceProperties(kind: "world", edit: true);
                    break;

                case "campaign_info":
                    ShowResourceProperties(kind: "campaign", edit: false,
                        root.TryGetProperty("campaignId", out var infoCampaignNode)
                            ? infoCampaignNode.GetString()
                            : null);
                    break;

                case "campaign_edit":
                    ShowResourceProperties(kind: "campaign", edit: true,
                        root.TryGetProperty("campaignId", out var editCampaignNode)
                            ? editCampaignNode.GetString()
                            : null);
                    break;

                case "create_world":
                    CreateWorld();
                    break;

                case "create_campaign":
                    CreateCampaign();
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

    /// <summary>
    /// Отправляет в главное окно состояние селектора [МИР][КАМПАНИЯ][меню].
    ///
    /// Источник истины — Host: он владеет каталогом миров и знает, какой мир
    /// открыт. Web только рисует присланное, поэтому селектор в главном окне и
    /// в Симуляторе не может разойтись.
    ///
    /// Кампании берутся из стора, УЖЕ ограниченного текущим миром: это и есть
    /// причина, по которой список не может показать чужой мир.
    /// </summary>
    private void PostWorldSelection()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        var world = SelectedWorld;
        var campaigns = _campaignStore.Records
            .Select(record => new
            {
                id = record.Definition.Id,
                name = WorldDisplayRules.DisplayName(record.Definition.Name, record.Definition.FullName)
            })
            .ToArray();

        var activeCampaignId = ResourceSelectorRules.ResolveCampaignId(
            world?.Definition.LastCampaignId,
            campaigns.Select(item => item.id));

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                type = "world_selection",
                worlds = _worldStore.Worlds
                    .Select(record => new { id = record.Definition.Id, name = record.DisplayName })
                    .ToArray(),
                campaigns,
                worldId = world?.Definition.Id ?? string.Empty,
                campaignId = activeCampaignId ?? string.Empty
            });
            PostJson(payload);
            _simulator?.SetWorldSelectionJson(payload);
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
            _dynamicEventStore,
            _dynamicEventDispatcher,
            _roads,
            _junctions,
            _cityBoundaries);

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
            "events" or "dynamic-events" => ("Редактор динамических событий", "editor.html#events"),
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
            if (window.IsDisposed)
                continue;

            window.RefreshLocationCatalog();

            if (string.Equals(
                    Path.GetExtension(path),
                    DynamicEventStore.Extension,
                    StringComparison.OrdinalIgnoreCase))
            {
                window.RefreshDynamicEventCatalog();
            }
        }

        if (string.Equals(Path.GetExtension(path), LocationStore.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _locationResolver.Reset();
        }

        if (string.Equals(Path.GetExtension(path), DynamicEventStore.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _dynamicEventStore.Reload();
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
            _dynamicEventDispatcher,
            _roads,
            // Мир передаётся явно: Симулятор показывает его кампании, и без
            // ссылки на мир он видел бы кампании ВСЕХ миров сразу.
            SelectedWorld);
        _simulator.WorldSwitchRequested += (_, e) => RequestWorldSwitch(e.WorldId);
        _simulator.CampaignSelectionRequested += (_, e) => SelectCampaign(e.CampaignId);
        _simulator.WorldExportRequested += (_, _) => ExportWorld();
        _simulator.CampaignExportRequested += (_, e) => ExportCampaign(e.CampaignId);
        _simulator.QuestExportRequested += (_, e) => ExportQuest(e.CampaignId, e.QuestId, e.Path);
        _simulator.ImportArchiveRequested += (_, _) => ImportArchiveFromDialog();
        _simulator.FormClosed += (_, _) => _simulator = null;
        // ℹ️ в дереве кампаний открывает то же окно свойств, что и меню главной
        // формы: список показывает несколько кампаний, и для каждой — своя
        // иконка, поэтому кампания передаётся явно, а не берётся из селектора.
        _simulator.WorldPropertiesRequested += (_, _) =>
            ShowResourceProperties(kind: "world", edit: false);
        _simulator.CampaignPropertiesRequested += (_, e) =>
            ShowResourceProperties(kind: "campaign", edit: false, campaignId: e.CampaignId);
        PlaceOnSecondaryScreen(_simulator);
        _simulator.Show(this);
        PostWorldSelection();
    }

    /// <summary>
    /// Текущий мир приложения.
    ///
    /// Берётся по id из настроек: мир выбирается один раз при первом запуске, а
    /// дальше восстанавливается. Если сохранённого мира нет (удалили папку,
    /// переустановка), берётся первый доступный — работать без мира нельзя, но
    /// и молча открывать ничего нельзя, поэтому выбор фиксируется в настройках.
    /// </summary>
    private WorldRecord? SelectedWorld
    {
        get
        {
            var stored = _worldStore.Worlds.FirstOrDefault(world =>
                world.Definition.Id.Equals(_preferences.LastWorldId, StringComparison.OrdinalIgnoreCase));

            if (stored is not null)
                return stored;

            var fallback = _worldStore.Worlds.FirstOrDefault();
            if (fallback is null)
                return null;

            if (!string.Equals(_preferences.LastWorldId, fallback.Definition.Id, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info("MainForm: сохранённый мир недоступен, выбран первый доступный.",
                    $"stored={_preferences.LastWorldId ?? "нет"}; selected={fallback.Definition.Id}");

                // В CI-прогоне настройки НЕ записываются: прогон обязан быть
                // неинтерактивным и не менять файлы пользователя. Сам выбор при
                // этом работает — он нужен, чтобы приложение запустилось.
                if (!_ciTest)
                {
                    _preferences = _preferences with { LastWorldId = fallback.Definition.Id };
                    AppUiPreferencesStore.Save(_preferences);
                }
            }

            return fallback;
        }
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