using System.Text.Json;
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
                    {
                        extensionKey.DeleteValue(null, throwOnMissingValue: false);
                    }

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

    private static void RegisterType(
        ResourceFileType resource,
        string exePath,
        string iconDirectory)
    {
        var progId = BuildProgId(resource);
        var extensionKeyPath = ClassesRoot + "\\" + resource.Extension;
        var progIdKeyPath = ClassesRoot + "\\" + progId;

        using (var extensionKey = Registry.CurrentUser.CreateSubKey(extensionKeyPath))
        {
            if (extensionKey is null)
                return;

            var current = extensionKey.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(current))
                extensionKey.SetValue(null, progId, RegistryValueKind.String);

            using var openWith = extensionKey.CreateSubKey("OpenWithProgids");
            openWith?.SetValue(progId, string.Empty, RegistryValueKind.None);
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(progIdKeyPath))
        {
            if (progIdKey is null)
                return;

            progIdKey.SetValue(null, resource.FriendlyName, RegistryValueKind.String);
            progIdKey.SetValue("FriendlyTypeName", resource.FriendlyName, RegistryValueKind.String);
            progIdKey.SetValue("InfoTip", resource.Description + " — Assist Quest Editor", RegistryValueKind.String);

            using (var icon = progIdKey.CreateSubKey("DefaultIcon"))
                icon?.SetValue(null, QuotePath(Path.Combine(iconDirectory, resource.IconFileName) + ",0"), RegistryValueKind.String);

            using var shell = progIdKey.CreateSubKey(@"shellopencommand");
            shell?.SetValue(null, QuotePath(exePath) + " "%1"", RegistryValueKind.String);
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

        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "FileIcons", "file-icons.json");
        if (!File.Exists(source))
            return directory;

        using var document = JsonDocument.Parse(File.ReadAllText(source));
        var icons = document.RootElement.GetProperty("icons");

        foreach (var resource in ResourceFileTypes.All)
        {
            var key = resource.ProgIdPart.ToLowerInvariant();
            if (!icons.TryGetProperty(key, out var encoded))
                continue;

            var bytes = Convert.FromBase64String(encoded.GetString() ?? string.Empty);
            if (bytes.Length == 0)
                continue;

            var target = Path.Combine(directory, resource.IconFileName);
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, true);
        }

        return directory;
    }

    private static string QuotePath(string path) =>
        """ + path.Replace(""", "\"", StringComparison.Ordinal) + """;

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
