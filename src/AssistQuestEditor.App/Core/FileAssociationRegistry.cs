using Microsoft.Win32;

namespace AssistQuestEditor.App;

public static class FileAssociationRegistry
{
    private const string ClassesRoot = @"Software\Classes";
    private const string ApplicationName = "AssistQuestEditor";

    public static void Register()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var iconDirectory = EnsureIconPackage();
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            foreach (var resource in ResourceFileTypes.All)
                RegisterType(resource, exePath, iconDirectory);

            NativeMethods.NotifyShellAssociationsChanged();
            AppLogger.Info(
                "File associations: зарегистрированы.",
                "extensions=" + string.Join(", ", ResourceFileTypes.All.Select(item => item.Extension)));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("File associations: регистрация не выполнена.", ex.Message);
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
                        extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);

                    extensionKey?.OpenSubKey("OpenWithProgids", writable: true)
                        ?.DeleteValue(progId, throwOnMissingValue: false);
                }

                classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            }

            NativeMethods.NotifyShellAssociationsChanged();
            AppLogger.Info("File associations: удалены.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("File associations: удаление не выполнено.", ex.Message);
        }
    }

    private static void RegisterType(ResourceFileType resource, string exePath, string iconDirectory)
    {
        var progId = BuildProgId(resource);

        using (var extensionKey = Registry.CurrentUser.CreateSubKey(ClassesRoot + "\\" + resource.Extension))
        {
            if (extensionKey is null)
                return;

            // Не забираем ассоциацию, которую пользователь уже выбрал вручную.
            if (string.IsNullOrWhiteSpace(extensionKey.GetValue(null) as string))
                extensionKey.SetValue(null, progId, RegistryValueKind.String);

            using var openWith = extensionKey.CreateSubKey("OpenWithProgids");
            openWith?.SetValue(progId, string.Empty, RegistryValueKind.None);
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(ClassesRoot + "\\" + progId))
        {
            if (progIdKey is null)
                return;

            progIdKey.SetValue(null, resource.FriendlyName, RegistryValueKind.String);
            progIdKey.SetValue("FriendlyTypeName", resource.FriendlyName, RegistryValueKind.String);
            progIdKey.SetValue("InfoTip", resource.Description + " — Assist Quest Editor", RegistryValueKind.String);

            using (var icon = progIdKey.CreateSubKey("DefaultIcon"))
                icon?.SetValue(null, QuotePath(Path.Combine(iconDirectory, resource.IconFileName) + ",0"), RegistryValueKind.String);

            using var shell = progIdKey.CreateSubKey("shell\\open\\command");
            shell?.SetValue(null, QuotePath(exePath) + " \"%1\"", RegistryValueKind.String);
        }
    }

    private static string BuildProgId(ResourceFileType resource) =>
        ApplicationName + "." + resource.ProgIdPart + ".1";

    private static string EnsureIconPackage()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationName,
            "FileIcons");

        Directory.CreateDirectory(directory);

        foreach (var resource in ResourceFileTypes.All)
        {
            var target = Path.Combine(directory, resource.IconFileName);
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, CreateIcon(resource));
            File.Move(temp, target, true);
        }

        return directory;
    }

    // Generates a tiny self-contained ICO package at first registration.
    // This keeps the project free of binary icon dependencies while still
    // giving every registered resource type its own stable Explorer icon.
    private static byte[] CreateIcon(ResourceFileType resource)
    {
        const int width = 32;
        const int height = 32;

        var (r, g, b) = resource.ProgIdPart switch
        {
            "Quest" => (210, 70, 70),
            "Scene" => (70, 120, 220),
            "Dialogue" => (150, 80, 210),
            "Choice" => (180, 90, 210),
            "Campaign" => (220, 140, 40),
            "World" => (60, 130, 180),
            "Point" => (50, 160, 110),
            "City" => (40, 150, 170),
            "Item" => (80, 160, 170),
            "Condition" => (200, 120, 60),
            "Effect" => (90, 170, 100),
            "Localization" => (200, 90, 150),
            "NodeRegistry" => (110, 90, 170),
            "RuntimeSnapshot" => (120, 120, 120),
            _ => (90, 90, 90)
        };

        var letter = resource.ProgIdPart switch
        {
            "NodeRegistry" => 'N',
            "RuntimeSnapshot" => 'R',
            _ => resource.ProgIdPart[0]
        };

        var pixels = new byte[width * height * 4];
        var glyph = Glyphs.GetValueOrDefault(letter, Glyphs['R']);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var border = x < 2 || y < 2 || x >= width - 2 || y >= height - 2;
            var gx = (x - 7) / 4;
            var gy = (y - 5) / 4;
            var glyphPixel = gx is >= 0 and < 5 &&
                             gy is >= 0 and < 7 &&
                             glyph[gy][gx] == '1';

            var index = ((height - 1 - y) * width + x) * 4;
            pixels[index] = (byte)(glyphPixel ? 255 : b);
            pixels[index + 1] = (byte)(glyphPixel ? 255 : g);
            pixels[index + 2] = (byte)(glyphPixel ? 255 : r);
            pixels[index + 3] = 255;

            if (border)
                pixels[index] = pixels[index + 1] = pixels[index + 2] = 255;
        }

        using var image = new MemoryStream();
        using var writer = new BinaryWriter(image);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);

        const int imageOffset = 22;
        const int dibSize = 40;
        const int pixelBytes = width * height * 4;
        const int maskBytes = ((width + 31) / 32) * 4 * height;
        const int imageSize = dibSize + pixelBytes + maskBytes;

        writer.Write((byte)width);
        writer.Write((byte)height);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(imageSize);
        writer.Write(imageOffset);

        writer.Write(dibSize);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixelBytes);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
        writer.Write(new byte[maskBytes]);

        return image.ToArray();
    }

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['Q'] = ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
        ['S'] = ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['C'] = ["01111", "10000", "10000", "10000", "10000", "10000", "01111"],
        ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ['I'] = ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"]
    };

    private static string QuotePath(string path) =>
        "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static class NativeMethods
    {
        private const uint ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public static void NotifyShellAssociationsChanged() =>
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }
}
