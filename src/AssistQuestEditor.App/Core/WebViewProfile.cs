using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Профиль WebView2, привязанный к отпечатку сборки.
///
/// Правило отпечатка живёт в <see cref="WebBuildStamp"/> (Domain) и покрыто
/// тестами; здесь — только работа с диском конкретного процесса.
///
/// Порядок выбора отпечатка:
///   1) файл <c>web-build.stamp</c> рядом с EXE — его пишет <c>compile.ps1</c>
///      при публикации, и он один для всей поставки;
///   2) если файла нет (отладочный запуск, <c>dotnet run</c>), отпечаток
///      считается по содержимому каталога <c>Web</c> прямо сейчас. Именно этот
///      случай и был источником дефекта: обычная отладка не чистила профиль,
///      поэтому WebView2 отдавал старые скрипты из кеша.
/// </summary>
public static class WebViewProfile
{
    private static string? _cachedStamp;

    /// <summary>
    /// Отпечаток текущего запуска. Считается один раз: каталог <c>Web</c>
    /// содержит десятки файлов, а значение не меняется в пределах процесса —
    /// подмена страниц на диске во время работы не должна менять профиль,
    /// иначе WebView2 откроет вторую копию хранилища под живым процессом.
    /// </summary>
    public static string CurrentStamp =>
        _cachedStamp ??= ResolveStamp();

    /// <summary>Каталог профиля для отпечатка текущего запуска.</summary>
    public static string CurrentProfilePath =>
        WebBuildStamp.ProfilePathFor(AppPaths.WebViewUserDataRoot, CurrentStamp);

    private static string ResolveStamp()
    {
        // Файл отпечатка ищется рядом с EXE, а не в AppContext.BaseDirectory.
        // Приложение собрано как single-file, поэтому BaseDirectory — это
        // невидимый кэш распаковки в %TEMP%, а файл поставки лежит рядом с
        // исполняемым файлом. Тот же довод, по которому рядом с EXE лежит папка
        // data (см. ResourceRootResolver).
        var stampFile = WebBuildStamp.FindStampFile(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath),
            AppContext.BaseDirectory);

        if (stampFile is not null && File.Exists(stampFile))
        {
            try
            {
                var value = File.ReadAllText(stampFile).Trim();
                if (value.Length > 0)
                {
                    AppLogger.Info("WebView2: отпечаток сборки прочитан из файла.",
                        $"path={stampFile}; stamp={value}");
                    return value;
                }
            }
            catch (IOException ex)
            {
                AppLogger.Warn("WebView2: не удалось прочитать отпечаток сборки.", ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Warn("WebView2: нет доступа к отпечатку сборки.", ex.Message);
            }
        }

        var webDirectory = Path.Combine(AppContext.BaseDirectory, "Web");
        var computed = WebBuildStamp.ComputeForDirectory(webDirectory, VersionInfo.NumericVersion);

        AppLogger.Info("WebView2: отпечаток сборки вычислен по содержимому Web.",
            $"directory={webDirectory}; stamp={computed}");

        return computed;
    }

    /// <summary>
    /// Готовит профиль к запуску: создаёт каталог под текущий отпечаток и
    /// убирает профили прошлых сборок.
    ///
    /// Уборка нужна потому, что привязка к отпечатку оставляет по каталогу на
    /// каждую сборку. Удаляются только те, что сейчас НЕ используются: каталог
    /// работающих профилей занят процессом WebView2, и попытка его удалить
    /// молча не удалась бы. Отказ удалить чужой каталог не должен мешать
    /// запуску: холодная загрузка важнее порядка на диске.
    /// </summary>
    public static void Prepare()
    {
        var profilePath = CurrentProfilePath;

        try
        {
            Directory.CreateDirectory(profilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Профиль создаст сам WebView2. Ошибка здесь не смертельна, поэтому
            // о ней сообщаем и продолжаем: иначе один занятый каталог запретил
            // бы запускать приложение.
            AppLogger.Warn("WebView2: не удалось создать каталог профиля.", ex.Message);
            return;
        }

        RemoveStaleProfiles(profilePath);
    }

    private static void RemoveStaleProfiles(string currentPath)
    {
        var root = AppPaths.WebViewUserDataRoot;

        if (!Directory.Exists(root))
        {
            return;
        }

        var removed = 0;
        var kept = 0;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(
                    Path.GetFullPath(directory),
                    Path.GetFullPath(currentPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                kept++;
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException)
            {
                // Каталог занят работающим WebView2 — это нормальный случай,
                // когда открыто второе окно другого процесса.
                kept++;
            }
            catch (UnauthorizedAccessException)
            {
                kept++;
            }
        }

        AppLogger.Info("WebView2: профиль подготовлен.",
            $"stamp={CurrentStamp}; path={currentPath}; removed={removed}; kept={kept}");
    }
}
