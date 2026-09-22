using System.Security.Cryptography;

namespace AssistQuestEditor.Domain;

public enum ResourceIssueKind
{
    /// <summary>Файл из манифеста отсутствует в каталоге приложения.</summary>
    Missing,

    /// <summary>Содержимое не совпадает с манифестом: файл изменён или остался от прошлой сборки.</summary>
    Modified,

    /// <summary>Версия схемы не совпадает с записанной в манифесте.</summary>
    SchemaMismatch,

    /// <summary>Манифеста нет: каталог ресурсов нельзя считать проверенным.</summary>
    ManifestMissing,

    /// <summary>Манифест создан другой (несовместимой) версией формата.</summary>
    ManifestIncompatible,

    /// <summary>Не удалось прочитать файл (например, нет доступа).</summary>
    Unreadable,

    /// <summary>В каталоге есть файл, которого нет в манифесте (остаток прошлой сборки).</summary>
    UnexpectedFile
}

/// <summary>Найденная проблема с одним ресурсом публикации.</summary>
public sealed record ResourceIssue(
    ResourceIssueKind Kind,
    string Message,
    string? RelativePath = null);

/// <summary>Итог проверки ресурсной папки.</summary>
public sealed record ResourceIntegrityResult(
    bool Ok,
    IReadOnlyList<ResourceIssue> Issues,
    int CheckedFiles)
{
    public string Summary =>
        Ok
            ? $"Ресурсы публикации совпадают с манифестом: файлов {CheckedFiles}."
            : $"Найдено проблем с ресурсами: {Issues.Count} (проверено файлов {CheckedFiles}).";
}

/// <summary>
/// Проверка ресурсной папки публикации по манифесту.
///
/// Зачем отдельный контракт, а не просто «сверить mtime»: сборщик ресурсов
/// запускается отдельной задачей и может оставить в каталоге файл от прошлой
/// сборки. Такой файл выглядит как актуальный, но содержит старую или
/// несовместимую схему. Хеш содержимого плюс версия схемы ловят это до запуска
/// приложения, а не в момент, когда квест уже открыт.
///
/// Проверка нужна и на этапе публикации (блокирует сборку), и при запуске
/// (сообщает игроку, что ресурсы расходятся с executable).
/// </summary>
public static class ResourceIntegrityChecker
{
    /// <summary>
    /// Проверяет каталог ресурсов по манифесту.
    ///
    /// Проверяется ресурсный каталог, а не корень приложения: манифест лежит
    /// внутри него, и все пути в манифесте относительны ему же.
    /// </summary>
    public static ResourceIntegrityResult Verify(string resourceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);

        var issues = new List<ResourceIssue>();

        if (!Directory.Exists(resourceRoot))
        {
            issues.Add(new ResourceIssue(
                ResourceIssueKind.ManifestMissing,
                $"Каталог ресурсов не найден: {resourceRoot}."));
            return new ResourceIntegrityResult(false, issues, 0);
        }

        var manifest = ResourceManifestIo.Read(resourceRoot);

        if (manifest is null)
        {
            issues.Add(new ResourceIssue(
                ResourceIssueKind.ManifestMissing,
                $"Манифест ресурсов не найден или повреждён: {ResourceManifestIo.FileName}. " +
                "Каталог публикации нельзя считать проверенным."));
            return new ResourceIntegrityResult(false, issues, 0);
        }

        if (manifest.ManifestVersion != ResourceManifestIo.SupportedVersion)
        {
            issues.Add(new ResourceIssue(
                ResourceIssueKind.ManifestIncompatible,
                $"Версия манифеста {manifest.ManifestVersion} не поддерживается " +
                $"(ожидается {ResourceManifestIo.SupportedVersion})."));
            return new ResourceIntegrityResult(false, issues, 0);
        }

        // Лишний файл от прошлой сборки не должен оставаться в публикации: он не
        // попадёт в манифест, но будет выглядеть как рабочий ресурс, а приложение
        // может открыть именно его (например, по сохранённому пути последнего файла).
        var expected = new HashSet<string>(
            manifest.Entries.Select(entry => Normalize(entry.Path)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(resourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Normalize(Path.GetRelativePath(resourceRoot, path));
            if (relative.Equals(ResourceManifestIo.FileName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!expected.Contains(relative))
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.UnexpectedFile,
                    $"Лишний ресурс, которого нет в манифесте: {relative}.",
                    relative));
            }
        }

        foreach (var entry in manifest.Entries)
        {
            var fullPath = Path.Combine(resourceRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.Missing,
                    $"Отсутствует ресурс из манифеста: {entry.Path}.",
                    entry.Path));
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != entry.Length)
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.Modified,
                    $"Размер ресурса {entry.Path} не совпадает с манифестом " +
                    $"({info.Length} вместо {entry.Length}).",
                    entry.Path));
                continue;
            }

            string? actualHash;
            try
            {
                actualHash = HashFile(fullPath);
            }
            catch (IOException)
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.Unreadable,
                    $"Не удалось прочитать ресурс: {entry.Path}.",
                    entry.Path));
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.Unreadable,
                    $"Нет доступа к ресурсу: {entry.Path}.",
                    entry.Path));
                continue;
            }

            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.Modified,
                    $"Содержимое ресурса {entry.Path} не совпадает с манифестом: " +
                    "файл изменён или остался от другой сборки.",
                    entry.Path));
                continue;
            }

            // Проверка схемы отдельно от хеша: она объясняет причину понятнее,
            // чем «содержимое не совпало», и переживает пересохранение файла.
            //
            // Требование предъявляется к ресурсам со схемой: у картинки схемы нет
            // (schemaVersion null), и это не ошибка.
            var schemaAware = ResourceSchemaProbe.IsSchemaAware(fullPath);
            var actualSchema = ResourceSchemaProbe.TryReadSchemaVersion(fullPath);

            if (!schemaAware)
            {
                if (actualSchema is not null)
                {
                    issues.Add(new ResourceIssue(
                        ResourceIssueKind.SchemaMismatch,
                        $"Ресурс {entry.Path} содержит схему {actualSchema}, хотя схема для него не ожидается.",
                        entry.Path));
                }
            }
            else if (actualSchema is null)
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.SchemaMismatch,
                    $"У ресурса {entry.Path} нет версии схемы, а для этого типа она обязательна" +
                    (entry.SchemaVersion is null ? "." : $" (ожидается {entry.SchemaVersion})."),
                    entry.Path));
            }
            else if (entry.SchemaVersion is null || actualSchema != entry.SchemaVersion)
            {
                issues.Add(new ResourceIssue(
                    ResourceIssueKind.SchemaMismatch,
                    $"Схема ресурса {entry.Path} — {actualSchema}, а манифест ожидает " +
                    $"{entry.SchemaVersion?.ToString() ?? "<нет>"}.",
                    entry.Path));
            }
        }

        return new ResourceIntegrityResult(issues.Count == 0, issues, manifest.Entries.Count);
    }

    /// <summary>Проверяет ресурсы приложения: <c>&lt;app&gt;/data</c> рядом с исполняемым файлом.</summary>
    public static ResourceIntegrityResult VerifyAppResources(string appDirectory) =>
        Verify(ResourceManifestIo.ResourceRootFor(appDirectory));

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');


    /// <summary>Хеш содержимого файла: единственная надёжная проверка «тот же файл».</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Человекочитаемый отчёт построчно: итог и каждая проблема отдельной строкой.
    ///
    /// Отдельный метод нужен потому, что проверка запускается ещё и из сборки, а
    /// GUI-приложение не имеет консоли: stdout там не увидеть. Тот же текст
    /// записывается в файл, который читает MSBuild, поэтому отчёт формируется в
    /// одном месте, а не собирается заново в скрипте.
    /// </summary>
    public static IReadOnlyList<string> FormatReport(ResourceIntegrityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string> { result.Summary };
        lines.AddRange(result.Issues.Select(issue => $"[{issue.Kind}] {issue.Message}"));
        return lines;
    }

    /// <summary>
    /// Тот же отчёт с указанием проверенного каталога.
    ///
    /// Путь добавляется явно: без него непонятно, какую именно папку проверили,
    /// а расхождение «манифест есть, а проверка его не видит» иначе не отличить
    /// от настоящей потери ресурсов.
    /// </summary>
    public static IReadOnlyList<string> FormatReport(ResourceIntegrityResult result, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);

        var lines = new List<string>(FormatReport(result)) { $"Проверенный каталог: {resourceRoot}" };
        return lines;
    }
}
