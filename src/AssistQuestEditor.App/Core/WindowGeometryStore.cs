using System.Drawing;
using System.Windows.Forms;

namespace AssistQuestEditor.App;

public static class WindowGeometryStore
{
    private static readonly Dictionary<Form, Binding> Bindings = new();

    public static bool HasSaved(string key) =>
        !string.IsNullOrWhiteSpace(key) && AppUiPreferencesStore.TryGetWindow(key, out _);

    public static void Attach(Form form, string key)
    {
        ArgumentNullException.ThrowIfNull(form);

        if (string.IsNullOrWhiteSpace(key) || Bindings.ContainsKey(form))
        {
            return;
        }

        var binding = new Binding(form, key);
        Bindings[form] = binding;
        binding.Restore();
        binding.Start();
    }

    public static void ClearSavedGeometry()
    {
        AppUiPreferencesStore.ClearWindowGeometry();

        foreach (var binding in Bindings.Values.ToArray())
        {
            binding.MarkReset();
        }

        AppLogger.Info("Настройки окон сброшены.", "Сохранённые координаты и размеры удалены.");
    }

    private static Rectangle MakeVisible(Rectangle bounds, Size minimumSize)
    {
        var width = Math.Max(minimumSize.Width, bounds.Width);
        var height = Math.Max(minimumSize.Height, bounds.Height);
        var candidate = new Rectangle(bounds.X, bounds.Y, width, height);

        var screens = Screen.AllScreens;
        var visibleScreen = screens.FirstOrDefault(screen => screen.WorkingArea.IntersectsWith(candidate));
        if (visibleScreen is not null)
        {
            return ClampToScreen(candidate, visibleScreen.WorkingArea);
        }

        var primary = Screen.PrimaryScreen;
        return primary is null
            ? candidate
            : CenterOnWorkingArea(candidate.Size, primary.WorkingArea);
    }

    private static Rectangle ClampToScreen(Rectangle bounds, Rectangle area)
    {
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        var x = Math.Clamp(bounds.X, area.Left, area.Right - width);
        var y = Math.Clamp(bounds.Y, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    private static Rectangle CenterOnWorkingArea(Size size, Rectangle area)
    {
        var width = Math.Min(Math.Max(1, size.Width), area.Width);
        var height = Math.Min(Math.Max(1, size.Height), area.Height);
        return new Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
    }

    private sealed class Binding
    {
        private readonly Form _form;
        private readonly string _key;
        private readonly System.Windows.Forms.Timer _saveTimer;
        private bool _resetPending;
        private Rectangle _boundsAtReset;

        public Binding(Form form, string key)
        {
            _form = form;
            _key = key;
            _saveTimer = new System.Windows.Forms.Timer { Interval = 450 };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
        }

        public void Start()
        {
            _form.LocationChanged += Changed;
            _form.SizeChanged += Changed;
            _form.ResizeEnd += ResizeEnd;
            _form.FormClosed += Closed;
        }

        public void Restore()
        {
            if (!AppUiPreferencesStore.TryGetWindow(_key, out var saved))
            {
                return;
            }

            var bounds = MakeVisible(
                new Rectangle(saved.X, saved.Y, saved.Width, saved.Height),
                _form.MinimumSize);

            _form.Bounds = bounds;
            _form.WindowState =
                Enum.TryParse<FormWindowState>(saved.WindowState, true, out var state) &&
                state == FormWindowState.Maximized
                    ? FormWindowState.Maximized
                    : FormWindowState.Normal;
        }

        public void MarkReset()
        {
            _saveTimer.Stop();
            _resetPending = true;
            _boundsAtReset = GetStoredBounds();
        }

        private void Changed(object? sender, EventArgs e)
        {
            if (_resetPending && GetStoredBounds() != _boundsAtReset)
            {
                _resetPending = false;
            }

            ScheduleSave();
        }

        private void ResizeEnd(object? sender, EventArgs e) => ScheduleSave();

        private void ScheduleSave()
        {
            if (_form.IsDisposed || !_form.IsHandleCreated)
            {
                return;
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void SaveNow()
        {
            if (_form.IsDisposed || _resetPending)
            {
                return;
            }

            var bounds = GetStoredBounds();
            if (bounds.Width < 80 || bounds.Height < 80)
            {
                return;
            }

            AppUiPreferencesStore.SaveWindow(
                _key,
                new WindowGeometry(
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    _form.WindowState == FormWindowState.Maximized ? "Maximized" : "Normal"));
        }

        private Rectangle GetStoredBounds() =>
            _form.WindowState == FormWindowState.Normal || _form.RestoreBounds.IsEmpty
                ? _form.Bounds
                : _form.RestoreBounds;

        private void Closed(object? sender, FormClosedEventArgs e)
        {
            _saveTimer.Stop();
            SaveNow();

            _form.LocationChanged -= Changed;
            _form.SizeChanged -= Changed;
            _form.ResizeEnd -= ResizeEnd;
            _form.FormClosed -= Closed;
            _saveTimer.Dispose();
            Bindings.Remove(_form);
        }
    }
}
