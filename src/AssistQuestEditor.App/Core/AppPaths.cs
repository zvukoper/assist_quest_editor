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
    /// <summary>Имя пользовательской папки приложения.</summary>
    public const string UserFolderName = "Assist Quest Editor";

    /// <summary>Каталог ресурсов, из которого читает приложение.</summary>
    public static string ResourceRoot =>
        ResourceRootResolver.Resolve(
            ResourceRootResolver.ExecutableDirectory(Environment.ProcessPath),
            AppContext.BaseDirectory);

    /// <summary>
    /// Корень пользовательских данных приложения в Документах.
    ///
    /// Документы, а не LocalApplicationData: пользовательские миры должны быть
    /// видны и переносимы. Если система не отдаёт папку Документов (бывает в
    /// урезанных профилях), используется запасной путь — иначе приложение не
    /// смогло бы работать вообще, а это хуже «некрасивой» папки.
    /// </summary>
    public static string UserRoot
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = AppContext.BaseDirectory;

            return Path.Combine(documents, UserFolderName);
        }
    }

    /// <summary>
    /// Корень миров. Внутри — папки миров, внутри миров — кампании и квесты.
    ///
    /// Единственное место хранения контента: раньше рядом с EXE жила папка
    /// `data`, дублировавшая то же самое, и было непонятно, какая из копий
    /// «настоящая».
    /// </summary>
    public static string WorldsRoot => WorldPaths.WorldsRoot(UserRoot);

    /// <summary>
    /// Каталог, из которого читаются кампании и квесты — то есть корень миров.
    ///
    /// Имя сохранено ради существующих вызовов (CampaignStore, SceneCatalogLoader),
    /// которые обходят каталог РЕКУРСИВНО: раз кампании лежат внутри миров,
    /// тот же обход находит их без изменений. Семантика при этом другая —
    /// раньше это был плоский каталог кампаний, теперь корень дерева миров.
    /// </summary>
    public static string UserQuestRoot => WorldsRoot;

    /// <summary>
    /// Постоянная пользовательская библиотека Location Resources.
    ///
    /// Локации переиспользуются между квестами, поэтому принадлежат не кампании.
    /// Хранятся в пользовательской папке, а не в мире: это инструмент автора, а
    /// не контент мира.
    /// </summary>
    public static string UserLocationRoot => Path.Combine(UserRoot, "locations");

    /// <summary>
    /// Корень сохранений.
    ///
    /// Оставлен для совместимости с уже созданными снимками: новые сохранения
    /// пишутся В МИР (см. <see cref="WorldPaths.SavesFolderPath"/>), но старый
    /// каталог нельзя молча бросить — иначе сохранения игрока «пропадут».
    /// </summary>
    public static string SimulationSaveRoot => Path.Combine(UserRoot, "saves");

    /// <summary>
    /// Технические данные WebView2 (кеш, профиль).
    ///
    /// Остаются в AppData: это не пользовательские данные, их не нужно видеть и
    /// нельзя переносить — профиль привязан к машине.
    /// </summary>
    public static string WebViewUserDataRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = AppContext.BaseDirectory;

            return Path.Combine(localAppData, "AssistQuestEditor", "WebView2");
        }
    }

    /// <summary>
    /// Каталог настроек интерфейса и предпочтений пользователя.
    ///
    /// В пользовательской папке рядом с мирами: псевдоним автора и выбранный мир
    /// — часть пользовательских данных, и держать их в AppData значило бы
    /// терять подпись автора при переустановке.
    /// </summary>
    public static string SettingsDirectory => UserRoot;
}
