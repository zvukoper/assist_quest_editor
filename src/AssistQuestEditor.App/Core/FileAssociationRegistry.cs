using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AssistQuestEditor.App;

public sealed record FileAssociationRegistrationResult(
    bool Success,
    int RegisteredCount,
    string Message);

/// <summary>
/// Per-user Windows file associations for Assist Quest resources.
/// Registration lives under HKCU so no administrator rights are required.
/// </summary>
public static class FileAssociationRegistry
{
    private const string ClassesRoot = @"Software\Classes";
    private const string ApplicationName = "AssistQuestEditor";
    private const string IconDirectoryRelative = @"AssistQuestEditor\FileIcons";

    public static FileAssociationRegistrationResult Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, 0, "Регистрация расширений доступна только в Windows.");
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return new(false, 0, "Не удалось определить путь к AssistQuestEditor.exe.");
            }

            EnsureIconPackage();

            var registered = 0;
            foreach (var resource in ResourceFileTypes.All)
            {
                RegisterType(resource, exePath);
                registered++;
            }

            NativeMethods.NotifyShellAssociationsChanged();

            var message =
                "Расширения Assist Quest зарегистрированы для текущего пользователя.\r\n\r\n" +
                string.Join(
                    "\r\n",
                    ResourceFileTypes.All.Select(resource =>
                        resource.Extension + " → " + resource.FriendlyName));

            AppLogger.Info(
                "File associations: зарегистрированы пользователем.",
                "extensions=" + string.Join(", ", ResourceFileTypes.All.Select(item => item.Extension)));

            return new(true, registered, message);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("File associations: регистрация не выполнена.", ex.Message);
            return new(false, 0, "Не удалось зарегистрировать расширения.\r\n\r\n" + ex.Message);
        }
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: true);
            if (classes is null)
                return;

            foreach (var resource in ResourceFileTypes.All)
            {
                var progId = BuildProgId(resource);

                using (var extensionKey = classes.OpenSubKey(resource.Extension, writable: true))
                {
                    if (extensionKey?.GetValue(null) is string current &&
                        current.Equals(progId, StringComparison.OrdinalIgnoreCase))
                    {
                        extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }

                    extensionKey?.OpenSubKey("OpenWithProgids", writable: true)
                        ?.DeleteValue(progId, throwOnMissingValue: false);
                }

                classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            }

            NativeMethods.NotifyShellAssociationsChanged();
            AppLogger.Info("File associations: удалены пользователем.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("File associations: удаление не выполнено.", ex.Message);
        }
    }

    private static void RegisterType(ResourceFileType resource, string exePath)
    {
        var progId = BuildProgId(resource);

        using (var extensionKey = Registry.CurrentUser.CreateSubKey(
                   ClassesRoot + "\\" + resource.Extension))
        {
            if (extensionKey is null)
                throw new InvalidOperationException(
                    "Не удалось создать раздел реестра для " + resource.Extension + ".");

            // Не перехватываем ассоциацию, которую пользователь уже выбрал.
            // При пустой ассоциации первый запуск регистрации назначает наш ProgID.
            if (string.IsNullOrWhiteSpace(extensionKey.GetValue(null) as string))
                extensionKey.SetValue(null, progId, RegistryValueKind.String);

            using var openWith = extensionKey.CreateSubKey("OpenWithProgids");
            openWith?.SetValue(progId, string.Empty, RegistryValueKind.String);
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(
                   ClassesRoot + "\\" + progId))
        {
            if (progIdKey is null)
                throw new InvalidOperationException(
                    "Не удалось создать ProgID для " + resource.Extension + ".");

            progIdKey.SetValue(null, resource.FriendlyName, RegistryValueKind.String);
            progIdKey.SetValue(
                "InfoTip",
                resource.Description + " — Assist Quest Editor",
                RegistryValueKind.String);

            using (var icon = progIdKey.CreateSubKey("DefaultIcon"))
            {
                if (icon is not null)
                {
                    // REG_EXPAND_SZ позволяет не зашивать конкретный профиль
                    // пользователя в реестр.
                    var iconValue =
                        QuotePath($@"%LOCALAPPDATA%\{IconDirectoryRelative}\{resource.IconFileName}") +
                        ",0";

                    icon.SetValue(
                        null,
                        iconValue,
                        RegistryValueKind.ExpandString);
                }
            }

            using var shell = progIdKey.CreateSubKey("shell\open\command");
            shell?.SetValue(
                null,
                QuotePath(exePath) + " \"%1\"",
                RegistryValueKind.String);
        }
    }

    private static string BuildProgId(ResourceFileType resource) =>
        ApplicationName + "." + resource.ProgIdPart + ".1";

    private static void EnsureIconPackage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Не удалось определить LocalAppData.");

        var directory = Path.Combine(localAppData, IconDirectoryRelative);
        Directory.CreateDirectory(directory);

        foreach (var resource in ResourceFileTypes.All)
        {
            var target = Path.Combine(directory, resource.IconFileName);
            var temp = target + ".tmp";

            try
            {
                File.WriteAllBytes(temp, CreateIcon(resource));
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Creates a multi-resolution ICO without external binary dependencies.
    /// The actual files are kept persistently in %LOCALAPPDATA% because a
    /// per-user HKCU association must keep pointing to a stable icon path.
    /// </summary>
    private static byte[] CreateIcon(ResourceFileType resource)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var pngFrames = sizes.Select(size => RenderPng(resource, size)).ToArray();

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)pngFrames.Length);

        const int directorySize = 6 + (16 * 7);
        var imageOffset = directorySize;

        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var frame = pngFrames[i];

            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frame.Length);
            writer.Write(imageOffset);

            imageOffset += frame.Length;
        }

        foreach (var frame in pngFrames)
            writer.Write(frame);

        return output.ToArray();
    }

    private static byte[] RenderPng(ResourceFileType resource, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.Clear(Color.Transparent);

        var scale = size / 64f;
        float S(float value) => Math.Max(1f, value * scale);

        using var background = new SolidBrush(Color.FromArgb(20, 24, 31));
        using var border = new Pen(Color.FromArgb(250, 176, 3), S(5f))
        {
            LineJoin = LineJoin.Round
        };
        using var white = new Pen(Color.FromArgb(231, 237, 244), S(4.5f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var gray = new Pen(Color.FromArgb(170, 180, 192), S(3.2f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var accent = new SolidBrush(
            resource.ProgIdPart.Equals("Quest", StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(231, 95, 95)
                : Color.FromArgb(86, 142, 255));
        using var orange = new SolidBrush(Color.FromArgb(250, 176, 3));

        var margin = S(4f);
        var panel = new RectangleF(
            margin,
            margin,
            size - margin * 2f,
            size - margin * 2f);

        using var path = new GraphicsPath();
        path.AddRoundedRectangle(
            panel,
            new SizeF(S(13f), S(13f)));
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);

        if (resource.ProgIdPart.Equals("Quest", StringComparison.OrdinalIgnoreCase))
            DrawQuestIcon(graphics, size, white, gray, accent, orange, S);
        else
            DrawSceneIcon(graphics, size, white, gray, accent, orange, S);

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static void DrawQuestIcon(
        Graphics g,
        int size,
        Pen white,
        Pen gray,
        Brush accent,
        Brush orange,
        Func<float, float> S)
    {
        var centerX = size / 2f;
        var topY = S(15f);
        var boxW = S(24f);
        var boxH = S(12f);

        DrawNode(g, new RectangleF(centerX - boxW / 2, topY, boxW, boxH), orange, white, S);
        DrawNode(g, new RectangleF(S(11f), S(35f), S(20f), S(11f)), null, gray, S);
        DrawNode(g, new RectangleF(size - S(31f), S(35f), S(20f), S(11f)), null, gray, S);
        DrawNode(g, new RectangleF(S(17f), S(48f), S(15f), S(9f)), null, gray, S);
        DrawNode(g, new RectangleF(size - S(32f), S(48f), S(15f), S(9f)), null, gray, S);

        using var branchPen = new Pen(Color.FromArgb(231, 237, 244), S(2.7f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var left = S(21f);
        var right = size - S(21f);
        var centerBottom = S(27f);

        g.DrawLine(branchPen, centerX, centerBottom, left, S(35f));
        g.DrawLine(branchPen, centerX, centerBottom, right, S(35f));
        g.DrawLine(branchPen, left, S(46f), S(25f), S(48f));
        g.DrawLine(branchPen, right, S(46f), size - S(25f), S(48f));

        using var dot = new SolidBrush(Color.White);
        using var dotAccent = new SolidBrush(Color.FromArgb(250, 176, 3));
        foreach (var point in new[]
        {
            new PointF(left, S(34f)),
            new PointF(right, S(34f)),
            new PointF(S(25f), S(47f)),
            new PointF(size - S(25f), S(47f))
        })
        {
            g.FillEllipse(dot, point.X - S(2.7f), point.Y - S(2.7f), S(5.4f), S(5.4f));
            g.FillEllipse(dotAccent, point.X - S(1.4f), point.Y - S(1.4f), S(2.8f), S(2.8f));
        }

        using var qPen = new Pen(accent, S(3.4f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var q = new RectangleF(size - S(23f), size - S(20f), S(10f), S(10f));
        g.DrawArc(q, 35, 290, qPen);
        g.DrawLine(qPen, size - S(16f), size - S(15f), size - S(12f), size - S(11f));
    }

    private static void DrawSceneIcon(
        Graphics g,
        int size,
        Pen white,
        Pen gray,
        Brush accent,
        Brush orange,
        Func<float, float> S)
    {
        var top = new RectangleF(S(9f), S(13f), S(29f), S(16f));
        var bottom = new RectangleF(S(25f), S(33f), S(29f), S(16f));

        DrawBubble(g, top, orange, white, S);
        DrawBubble(g, bottom, null, white, S);

        using var connector = new Pen(Color.FromArgb(231, 237, 244), S(2.6f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        g.DrawBezier(
            connector,
            S(37f), S(23f),
            S(48f), S(23f),
            S(48f), S(32f),
            S(48f), S(35f));

        using var dot = new SolidBrush(Color.FromArgb(250, 176, 3));
        g.FillEllipse(dot, S(43f), S(29f), S(7f), S(7f));

        using var sPen = new Pen(accent, S(3.6f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var cx = size - S(16f);
        var cy = size - S(16f);
        g.DrawArc(new RectangleF(cx - S(8f), cy - S(8f), S(16f), S(16f)), 25, 180, sPen);
        g.DrawArc(new RectangleF(cx - S(8f), cy - S(8f), S(16f), S(16f)), 205, 170, sPen);

        using var line = new Pen(Color.FromArgb(170, 180, 192), S(2.3f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(line, S(15f), S(20f), S(31f), S(20f));
        g.DrawLine(line, S(15f), S(24f), S(25f), S(24f));
        g.DrawLine(line, S(31f), S(39f), S(47f), S(39f));
        g.DrawLine(line, S(31f), S(43f), S(42f), S(43f));
    }

    private static void DrawNode(
        Graphics g,
        RectangleF rect,
        Brush? fill,
        Pen outline,
        Func<float, float> S)
    {
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(
            rect,
            new SizeF(S(4f), S(4f)));

        if (fill is not null)
            g.FillPath(fill, path);

        g.DrawPath(outline, path);

        var x = rect.X + rect.Width * 0.25f;
        var y = rect.Y + rect.Height * 0.35f;
        g.DrawLine(outline, x, y, rect.Right - rect.Width * 0.25f, y);

        if (rect.Width >= S(15f))
        {
            g.DrawLine(
                outline,
                x,
                y + rect.Height * 0.30f,
                rect.Right - rect.Width * 0.40f,
                y + rect.Height * 0.30f);
        }
    }

    private static void DrawBubble(
        Graphics g,
        RectangleF rect,
        Brush? fill,
        Pen outline,
        Func<float, float> S)
    {
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(
            rect,
            new SizeF(S(4f), S(4f)));
        path.StartFigure();
        path.AddLine(
            rect.X + S(5f),
            rect.Bottom,
            rect.X + S(3f),
            rect.Bottom + S(5f));
        path.AddLine(
            rect.X + S(3f),
            rect.Bottom + S(5f),
            rect.X + S(10f),
            rect.Bottom);

        if (fill is not null)
            g.FillPath(fill, path);

        g.DrawPath(outline, path);
    }

    private static string QuotePath(string path) =>
        "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static class NativeMethods
    {
        private const uint ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;

        [System.Runtime.InteropServices.DllImport(
            "shell32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SHChangeNotify(
            uint wEventId,
            uint uFlags,
            IntPtr dwItem1,
            IntPtr dwItem2);

        public static void NotifyShellAssociationsChanged() =>
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }
}
