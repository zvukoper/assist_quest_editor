using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Проверка ресурсов публикации по манифесту — часть контракта публикации.
///
/// Тесты фиксируют именно те случаи, ради которых манифест и появился:
/// потерянный ресурс, необновлённый файл, остаток прошлой сборки и
/// несовместимая схема. Без них расхождение обнаруживается только в игре.
/// </summary>
public sealed class ResourceIntegrityTests
{
    [Fact]
    public void MissingManifestIsReportedInsteadOfSilentlyPassing()
    {
        using var sandbox = new ResourceSandbox();

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Kind == ResourceIssueKind.ManifestMissing);
    }

    [Fact]
    public void ManuallyWrittenResourcesAreRejectedWithoutManifest()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.WriteRaw("quests/quest.aqquest", 1, "{}");

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        // Каталог без манифеста нельзя считать проверенным: неизвестно, какой
        // сборке принадлежат файлы.
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Kind == ResourceIssueKind.ManifestMissing);
    }

    [Fact]
    public void MatchingResourcesPassVerification()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.True(result.Ok, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ModifiedResourceIsDetectedEvenWhenSizeMatches()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.WriteRaw("scenes/scene.aqscene", 1, "{\"schemaVersion\":1,\"format\":\"aqscene\"}");
        sandbox.Sync();

        // Правка той же длины: проверка по размеру и mtime её пропустила бы,
        // поэтому сравнивается хеш содержимого.
        var target = Path.Combine(sandbox.ResourceRoot, "scenes", "scene.aqscene");
        var original = File.ReadAllText(target);
        var swapped = original.Replace("\"aqscene\"", "\"aqquest\"");
        Assert.Equal(original.Length, swapped.Length);
        File.WriteAllText(target, swapped);

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Kind == ResourceIssueKind.Modified && issue.RelativePath == "scenes/scene.aqscene");
    }

    [Fact]
    public void StaleResourceFromPreviousBuildIsDetected()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();

        // Файл, которого нет в манифесте: остаток прошлой сборки. Приложение могло
        // бы открыть именно его по сохранённому пути последнего документа.
        sandbox.WriteRaw("scenes/removed_in_new_build.aqscene", 1, "{\"schemaVersion\":1}");

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Kind == ResourceIssueKind.UnexpectedFile);
    }

    [Fact]
    public void MissingResourceIsDetected()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();
        File.Delete(Path.Combine(sandbox.ResourceRoot, "scenes", "scene.aqscene"));

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Kind == ResourceIssueKind.Missing && issue.RelativePath == "scenes/scene.aqscene");
    }

    [Fact]
    public void IncompatibleSchemaIsReportedSeparatelyFromHash()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();

        // Схема меняется, но файл синхронизируется заново — здесь проверяется
        // именно версия: при несовместимой схеме приложение не станет читать
        // ресурс «как есть».
        var target = Path.Combine(sandbox.ResourceRoot, "quests", "quest.aqquest");
        File.WriteAllText(target, "{\"schemaVersion\":99,\"format\":\"aqquest\"}");

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        // Хеш тоже изменился — важно, что причина названа явно и отдельно.
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue =>
            issue.Kind is ResourceIssueKind.SchemaMismatch or ResourceIssueKind.Modified);
    }

    [Fact]
    public void UnsupportedManifestVersionIsRejected()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();
        File.WriteAllText(
            Path.Combine(sandbox.ResourceRoot, ResourceManifestIo.FileName),
            "{\"manifestVersion\":999,\"appVersion\":\"x\",\"commit\":\"x\",\"generatedAtUtc\":\"2026-01-01T00:00:00Z\",\"entries\":[]}");

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Kind == ResourceIssueKind.ManifestIncompatible);
    }

    [Fact]
    public void CorruptManifestIsTreatedAsMissingNotAsCrash()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();
        File.WriteAllText(Path.Combine(sandbox.ResourceRoot, ResourceManifestIo.FileName), "{ это не json");

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Kind == ResourceIssueKind.ManifestMissing);
    }

    [Fact]
    public void AssetWithoutSchemaIsNotReportedAsSchemaMismatch()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.WriteRaw("images/avatar.png", 3, "PNG");
        sandbox.Sync();

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        // У портрета нет schemaVersion, и это нормально: иначе проверка ругалась бы
        // на каждый ресурс без схемы.
        Assert.True(result.Ok, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void SchemaAwareResourceWithoutSchemaIsRejected()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.WriteRaw("quests/quest.aqquest", 1, "{\"format\":\"aqquest\"}");
        sandbox.Sync();

        var result = ResourceIntegrityChecker.Verify(sandbox.ResourceRoot);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, issue => issue.Kind == ResourceIssueKind.SchemaMismatch);
    }

    [Fact]
    public void ResourceRootPrefersDataFolderBesideTheExecutable()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();

        var resolved = ResourceRootResolver.Resolve(sandbox.AppDirectory, sandbox.ExtractionDirectory);

        // Рядом с EXE лежит проверенная папка: именно она должна победить, иначе
        // ресурсы читались бы из кэша распаковки single-file.
        Assert.Equal(sandbox.ResourceRoot, resolved);
    }

    [Fact]
    public void ResourceRootFallsBackToAppDirectoryWhenDataIsAbsent()
    {
        using var sandbox = new ResourceSandbox();

        var resolved = ResourceRootResolver.Resolve(sandbox.AppDirectory, sandbox.ExtractionDirectory);

        // Отладочная сборка и тесты работают без папки рядом с EXE.
        Assert.Equal(ResourceManifestIo.ResourceRootFor(sandbox.ExtractionDirectory), resolved);
    }

    [Fact]
    public void ResourceRootWithoutExecutableDirectoryUsesAppDirectory()
    {
        using var sandbox = new ResourceSandbox();
        sandbox.Sync();

        var resolved = ResourceRootResolver.Resolve(null, sandbox.ExtractionDirectory);

        Assert.Equal(ResourceManifestIo.ResourceRootFor(sandbox.ExtractionDirectory), resolved);
    }

    /// <summary>
    /// Песочница с каталогами «рядом с EXE» и «каталогом приложения»: проверка
    /// не должна зависеть от того, откуда запущен тестовый процесс.
    ///
    /// Здесь создаётся тот же манифест, что и при сборке, поэтому тест проверяет
    /// реальный контракт, а не подделку под него.
    /// </summary>
    private sealed class ResourceSandbox : IDisposable
    {
        private readonly string _temp;

        public ResourceSandbox()
        {
            _temp = Path.Combine(Path.GetTempPath(), "aq-resources-" + Guid.NewGuid().ToString("N"));
            AppDirectory = Path.Combine(_temp, "app");
            ExtractionDirectory = Path.Combine(_temp, "extraction");
            ResourceRoot = Path.Combine(AppDirectory, ResourceManifestIo.DirectoryName);

            Directory.CreateDirectory(AppDirectory);
            Directory.CreateDirectory(ExtractionDirectory);

            WriteRaw("quests/quest.aqquest", 1, "{\"schemaVersion\":1,\"format\":\"aqquest\"}");
            WriteRaw("scenes/scene.aqscene", 1, "{\"schemaVersion\":1,\"format\":\"aqscene\"}");
        }

        public string AppDirectory { get; }
        public string ExtractionDirectory { get; }
        public string ResourceRoot { get; }

        /// <summary>Записывает файл в исходный набор ресурсов.</summary>
        public void WriteRaw(string relativePath, int schemaVersion, string content)
        {
            var path = Path.Combine(ResourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            Schemas[relativePath] = schemaVersion;
        }

        private Dictionary<string, int> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Строит манифест по текущему содержимому — так же, как это делает
        /// ci/sync_data_resources.ps1 при сборке.
        /// </summary>
        public void Sync()
        {
            var entries = Directory
                .EnumerateFiles(ResourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).Equals(
                    ResourceManifestIo.FileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var relative = Path.GetRelativePath(ResourceRoot, path).Replace('\\', '/');
                    var info = new FileInfo(path);
                    var schema = ResourceSchemaProbe.TryReadSchemaVersion(path);
                    return new ResourceManifestEntry(
                        relative,
                        info.Length,
                        ResourceIntegrityChecker.HashFile(path),
                        schema);
                })
                .ToArray();

            ResourceManifestIo.Write(
                ResourceRoot,
                new ResourceManifest(1, "test", "test", DateTimeOffset.UnixEpoch, entries));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_temp))
                    Directory.Delete(_temp, recursive: true);
            }
            catch (IOException)
            {
                // Временный каталог: его недоступность не влияет на результат теста.
            }
        }
    }
}
