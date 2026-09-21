using System.Reflection;

namespace AssistQuestEditor.App;

public static class BuildInfo
{
    private static readonly IReadOnlyDictionary<string, string> Metadata =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Key))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static string RepositoryRoot => Get("AssistQuestRepositoryRoot");
    public static string RepositoryUrl => Get("AssistQuestRepositoryUrl");
    public static string RepositoryBranch => Get("AssistQuestRepositoryBranch");
    public static string RepositoryCommit => Get("AssistQuestRepositoryCommit");

    public static bool HasRepositoryContext => !string.IsNullOrWhiteSpace(RepositoryRoot);

    private static string Get(string key) => Metadata.TryGetValue(key, out var value) ? value : string.Empty;
}
