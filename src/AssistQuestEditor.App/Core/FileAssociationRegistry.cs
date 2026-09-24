using Microsoft.Win32;

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

    /// <summary>
    /// Имя файла иконки в пользовательском каталоге иконок.
    ///
    /// ОДНО на все зарегистрированные типы: специконки для внутренних
    /// расширений не рисуются, и все они получают иконку приложения.
    /// </summary>
    private const string ApplicationIconFileName = AppIconService.IconFileName;

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

            var iconPath = Path.Combine(IconDirectoryRelative, ApplicationIconFileName);

            var registered = 0;
            foreach (var resource in ResourceFileTypes.All)
            {
                RegisterType(resource, exePath, iconPath);
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

    private static void RegisterType(ResourceFileType resource, string exePath, string iconPath)
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
                    // пользователя в реестр. Иконка ОДНА на все типы — иконка
                    // приложения, собранная из логотипов: специконки для
                    // внутренних расширений не рисуются.
                    var iconValue = QuotePath($@"%LOCALAPPDATA%\{iconPath}") + ",0";

                    icon.SetValue(
                        null,
                        iconValue,
                        RegistryValueKind.ExpandString);
                }
            }

            using var shell = progIdKey.CreateSubKey(@"shell\open\command");
            shell?.SetValue(
                null,
                QuotePath(exePath) + " \"%1\"",
                RegistryValueKind.String);
        }
    }

    private static string BuildProgId(ResourceFileType resource) =>
        ApplicationName + "." + resource.ProgIdPart + ".1";

    /// <summary>
    /// Раскладывает иконку приложения в пользовательский каталог и возвращает
    /// ПУТЬ к ней.
    ///
    /// Один ICO на ВСЕ типы: отдельных специконок для внутренних расширений не
    /// рисуется (по требованию автора), и это же правило распространяется на
    /// добавленные позже типы — новый ресурс получит иконку приложения сам, без
    /// правки этого кода.
    ///
    /// Файл лежит в %LOCALAPPDATA%, а не рядом с exe, потому что ассоциация
    /// пользователя должна указывать на стабильный путь: exe мог быть перенесён
    /// или удалён, и ссылка на него в реестре вела бы в никуда. Значение пишется
    /// как REG_EXPAND_SZ, поэтому конкретный профиль в реестр не зашивается.
    /// </summary>
    private static string EnsureIconPackage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Не удалось определить LocalAppData.");

        var directory = Path.Combine(localAppData, IconDirectoryRelative);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, ApplicationIconFileName);

        // Иконка берётся готовым файлом, если он есть, и только иначе собирается
        // из логотипов. Причина: собирающая способность зависит от наличия
        // PNG рядом с приложением, а зарегистрированная ассоциация обязана
        // указывать на УЖЕ существующий файл — ссылка в реестр на путь, который
        // не удалось создать, выглядит как сломанная иконка у всех файлов.
        if (!File.Exists(target))
        {
            var bytes = AppIconService.ToIcoBytes();

            // Иконка приложения недоступна — регистрацию завершаем, но БЕЗ иконки:
            // ассоциация важнее оформления, и отказ регистрировать всё из-за
            // отсутствующего логотипа был бы непропорционален.
            if (bytes is null)
            {
                AppLogger.Warn("File associations: иконка приложения недоступна.",
                    $"expected={target}");
                return target;
            }

            // Запись через временный файл: оболочка может держать иконку открытой,
            // и перезапись «на месте» иногда падает с «файл занят». Перемещение
            // подменяет файл одним действием.
            var temp = target + ".tmp";

            try
            {
                File.WriteAllBytes(temp, bytes);
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

        return target;
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
