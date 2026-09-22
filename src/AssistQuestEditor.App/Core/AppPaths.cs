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
}
