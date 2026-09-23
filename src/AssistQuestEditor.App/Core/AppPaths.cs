using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Расположение ресурсов в запущенном приложении.
///
/// Само правило выбора живёт в <see cref="ResourceRootResolver"/> (Domain), чтобы
/// его можно было покрыть тестами: здесь только источник путей конкретного
/// процесса.
/// </summary>
public static class AppPaths
{
    /// <summary>Каталог ресурсов, из которого читает приложение.</summary>
    public static string ResourceRoot =>
        ResourceRootResolver.Resolve(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath),
            AppContext.BaseDirectory);

    /// <summary>
    /// Постоянное пользовательское хранилище кампаний и квестов.
    /// Оно не зависит от каталога публикации и переживает обновление EXE.
    /// </summary>
    public static string UserQuestRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppContext.BaseDirectory;

            return Path.Combine(localAppData, "Assist Quest Editor", "quests");
        }
    }

    /// <summary>
    /// Постоянная пользовательская библиотека Location Resources. Локации
    /// переиспользуются между квестами и поэтому не принадлежат одному Campaign.
    /// </summary>
    public static string UserLocationRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppContext.BaseDirectory;

            return Path.Combine(localAppData, "Assist Quest Editor", "locations");
        }
    }

    public static string SimulationSaveRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppContext.BaseDirectory;

            return Path.Combine(localAppData, "Assist Quest Editor", "saves");
        }
    }
}
