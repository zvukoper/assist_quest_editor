namespace AssistQuestEditor.Domain;

/// <summary>
/// Выбор каталога ресурсов приложения.
///
/// В single-file публикации базовый каталог приложения — это кэш распаковки в
/// %TEMP%\.net\..., куда содержимое EXE извлекается при каждом запуске. Такой
/// каталог не виден человеку, не заменяется по частям и накапливает копии
/// прошлых сборок: ресурс может быть подставлен из устаревшего извлечения, и
/// заметить это в игре невозможно.
///
/// Поэтому ресурсы публикуются отдельной папкой data рядом с EXE: она собирается
/// синхронизацией по манифесту, а значит её содержимое заведомо принадлежит
/// текущей сборке.
///
/// Порядок выбора:
///   1) &lt;каталог EXE&gt;/data, если в нём есть манифест (проверенная публикация);
///   2) &lt;базовый каталог&gt;/data — запуск из сборки, отладка и распаковка
///      single-file, где папки рядом с EXE может не быть.
///
/// Второй вариант обязателен: из него работают доменные тесты и отладочный запуск.
/// </summary>
public static class ResourceRootResolver
{
    /// <summary>
    /// Определяет каталог ресурсов. <paramref name="executableDirectory"/> —
    /// каталог самого EXE (для single-file это не базовый каталог приложения).
    /// </summary>
    public static string Resolve(string? executableDirectory, string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var beside = ResourceManifestIo.ResourceRootFor(executableDirectory);
            if (File.Exists(ResourceManifestIo.PathFor(beside)))
                return beside;
        }

        return ResourceManifestIo.ResourceRootFor(appBaseDirectory);
    }

    /// <summary>Каталог самого EXE; null, если путь процесса недоступен.</summary>
    public static string? ExecutableDirectory(string? processPath) =>
        string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
}
