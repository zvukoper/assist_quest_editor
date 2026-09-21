using System.Text.Json;
using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

public sealed class MainForm : WebViewForm
{
    private readonly IDataChannelHub _hub;
    private readonly QuestGraphStore _questGraph;
    private readonly SceneCatalog _sceneCatalog;
    private readonly SceneRuntime _sceneRuntime;
    private readonly QuestRuntime _runtime;
    private readonly Dictionary<string, EditorForm> _editors = new(StringComparer.OrdinalIgnoreCase);
    private SimulatorForm? _simulator;
    private SettingsForm? _settings;

    public MainForm(IDataChannelHub hub)
        : base(
            "Assist Quest Editor — Редактор",
            "main.html",
            new Size(1280, 820),
            "main")
    {
        if (!WindowGeometryStore.HasSaved("main"))
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        _hub = hub;
        _questGraph = new QuestGraphStore(QuestDefinitionLoader.LoadOrFallback());
        _sceneCatalog = SceneCatalogLoader.Load();
        _sceneRuntime = new SceneRuntime(_sceneCatalog, _hub);
        _runtime = new QuestRuntime(_questGraph, _hub, _sceneRuntime);
        _sceneRuntime.Published += SceneRuntime_Published;
        Shown += (_, _) => OpenSimulator();
        FormClosed += (_, _) =>
        {
            _sceneRuntime.Published -= SceneRuntime_Published;

            foreach (var editor in _editors.Values.ToArray())
            {
                editor.Close();
            }

            _simulator?.Close();
            _settings?.Close();
        };
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

    private void SceneRuntime_Published(object? sender, SceneRuntimeEvent e)
    {
        AppLogger.Info(
            "Scene Runtime: событие.",
            $"event={e.EventType}; scene={e.SceneId}; node={e.NodeId ?? "<none>"}; " +
            $"choice={e.ChoiceId ?? "<none>"}; message={e.Message}");
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

    private void OpenEditor(string editor)
    {
        var page = editor.ToLowerInvariant() switch
        {
            "graph" => ("Нодовый редактор", "editor.html#graph"),
            "scene" => ("Редактор сцен", "editor.html#scene"),
            "world" => ("Редактор мира", "editor.html#world"),
            "channels" => ("Инспектор каналов", "editor.html#channels"),
            "conditions" => ("Редактор условий и действий", "editor.html#conditions"),
            "localization" => ("Редактор локализации", "editor.html#localization"),
            "validation" => ("Проверка проекта", "editor.html#validation"),
            "registry" => ("Реестр нод и схем", "editor.html#registry"),
            _ => ("Редактор", "editor.html#graph")
        };

        if (_editors.TryGetValue(page.Item2, out var existing) && !existing.IsDisposed)
        {
            existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Activate();
            return;
        }

        var form = new EditorForm(page.Item1, page.Item2, _hub, _questGraph, _runtime);
        _editors[page.Item2] = form;
        form.FormClosed += (_, _) => _editors.Remove(page.Item2);
        PlaceAuxiliaryWindow(form, _editors.Count);
        form.Show(this);
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

        _simulator = new SimulatorForm(_hub, _runtime, _questGraph);
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
