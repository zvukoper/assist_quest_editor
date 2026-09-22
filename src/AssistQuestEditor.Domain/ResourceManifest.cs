using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssistQuestEditor.Domain;

/// <summary>Одна запись манифеста: файл ресурса в публикации.</summary>
public sealed record ResourceManifestEntry(
    string Path,
    long Length,
    string Sha256,
    int? SchemaVersion);

/// <summary>
/// Манифест ресурсов публикации.
///
/// Нужен, чтобы отличать актуальные файлы от остатков прошлых сборок и от
/// несовместимых схем. Одного времени изменения мало: копирование и распаковка
/// сохраняют mtime исходника, поэтому файл с тем же временем может содержать
/// другой код. Хеш содержимого — единственная надёжная проверка.
/// </summary>
public sealed record ResourceManifest(
    int ManifestVersion,
    string AppVersion,
    string Commit,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ResourceManifestEntry> Entries);

public static class ResourceManifestIo
{
    /// <summary>Имя файла манифеста внутри каталога ресурсов.</summary>
    public const string FileName = "data-manifest.json";

    /// <summary>
    /// Имя каталога ресурсов рядом с исполняемым файлом.
    ///
    /// Ресурсы лежат папкой, а не только внутри single-file: файл рядом с EXE
    /// виден человеку и поддаётся замене, тогда как включённый в single-file
    /// ресурс живёт в системном кэше распаковки и не проверяется глазами.
    /// </summary>
    public const string DirectoryName = "data";

    /// <summary>Допустимая версия манифеста; иная версия считается несовместимой.</summary>
    public const int SupportedVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Каталог ресурсов приложения: <c>&lt;app&gt;/data</c>.</summary>
    public static string ResourceRootFor(string appDirectory) =>
        Path.Combine(appDirectory, DirectoryName);

    public static string PathFor(string resourceRoot) =>
        Path.Combine(resourceRoot, FileName);

    public static ResourceManifest? Read(string resourceRoot)
    {
        var path = PathFor(resourceRoot);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResourceManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            // Повреждённый манифест не должен ронять приложение: считаем, что его нет.
            return null;
        }
    }

    public static void Write(string resourceRoot, ResourceManifest manifest)
    {
        var path = PathFor(resourceRoot);
        var json = JsonSerializer.Serialize(manifest, Options) + "\n";
        File.WriteAllText(path, json);
    }
}
