using System.Diagnostics;
using System.Text;

namespace AssistQuestEditor.App;

public sealed record GitLogPushResult(bool Success, string Message, string Details);

public static class GitLogPublisher
{
    public static GitLogPushResult PublishLogs()
    {
        var repoRoot = FindRepositoryRoot();
        if (repoRoot is null)
        {
            var context = string.IsNullOrWhiteSpace(BuildInfo.RepositoryRoot)
                ? "В сборке не сохранён путь репозитория."
                : $"Сохранённый путь сборки: {BuildInfo.RepositoryRoot}";
            var identity = string.Join("; ", new[]
            {
                $"URL: {BuildInfo.RepositoryUrl}",
                $"ветка: {BuildInfo.RepositoryBranch}",
                $"commit: {BuildInfo.RepositoryCommit}"
            }.Where(value => !value.EndsWith(": unknown", StringComparison.OrdinalIgnoreCase)));
            return new(false, "Не найден корень Git-репозитория.", context + (string.IsNullOrWhiteSpace(identity) ? string.Empty : " " + identity));
        }

        var logsPath = Path.Combine(repoRoot, "MemoryAI", "LOGS");
        if (!Directory.Exists(logsPath))
            return new(false, "Каталог MemoryAI/LOGS не найден.", logsPath);

        var initial = RunGit(repoRoot, "diff", "--cached", "--name-only");
        if (!initial.Success)
            return new(false, "Не удалось проверить индекс Git.", initial.Output);

        var initialFiles = initial.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeGitPath)
            .ToArray();
        if (initialFiles.Length > 0)
            return new(false, "Перед загрузкой LOGS в Git уже есть подготовленные изменения. Сначала очистите staging.", string.Join(Environment.NewLine, initialFiles));

        var add = RunGit(repoRoot, "add", "-A", "-f", "--", "MemoryAI/LOGS");
        if (!add.Success)
            return new(false, "Не удалось подготовить MemoryAI/LOGS.", add.Output);

        var unstageReadme = RunGit(repoRoot, "restore", "--staged", "--", "MemoryAI/LOGS/README.md");
        if (!unstageReadme.Success && !unstageReadme.Output.Contains("pathspec", StringComparison.OrdinalIgnoreCase))
            return new(false, "Не удалось исключить README.md из коммита.", unstageReadme.Output);

        var staged = RunGit(repoRoot, "diff", "--cached", "--name-only", "--", "MemoryAI/LOGS");
        if (!staged.Success)
            return new(false, "Не удалось прочитать подготовленные файлы.", staged.Output);

        var files = staged.Output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeGitPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Any(path => path.Equals("MemoryAI/LOGS/README.md", StringComparison.OrdinalIgnoreCase)))
            return new(false, "README.md неожиданно попал в staging.", string.Join(Environment.NewLine, files));

        if (files.Length == 0)
            return new(true, "Новых логов для отправки нет.", "Коммит не создавался.");

        if (files.Any(path => !IsLogsPath(path)))
            return new(false, "После staging обнаружены изменения вне MemoryAI/LOGS.", string.Join(Environment.NewLine, files));

        var commit = RunGit(repoRoot, "commit", "-m", "New logs");
        if (!commit.Success)
            return new(false, "Не удалось создать коммит New logs.", commit.Output);

        var push = RunGit(repoRoot, "push");
        if (!push.Success)
            return new(false, "Коммит создан, но push не выполнен.", commit.Output + Environment.NewLine + push.Output);

        return new(true, "Логи успешно отправлены в GitHub.", commit.Output + Environment.NewLine + push.Output);
    }

    private static string NormalizeGitPath(string value) => value.Trim().Replace('\\', '/');

    private static bool IsLogsPath(string path) =>
        path.StartsWith("MemoryAI/LOGS/", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("MemoryAI/LOGS/README.md", StringComparison.OrdinalIgnoreCase);

    private static string? FindRepositoryRoot()
    {
        if (!string.IsNullOrWhiteSpace(BuildInfo.RepositoryRoot) && IsGitRepository(BuildInfo.RepositoryRoot))
            return BuildInfo.RepositoryRoot;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (IsGitRepository(current.FullName))
                return current.FullName;
            current = current.Parent;
        }

        return null;
    }

    private static bool IsGitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path);
        return Directory.Exists(Path.Combine(fullPath, ".git")) || File.Exists(Path.Combine(fullPath, ".git"));
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "Never";

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return new(false, "Не удалось запустить git.");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var combined = string.Join(
                Environment.NewLine,
                new[] { output.Trim(), error.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return new(process.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return new(false, ex.ToString());
        }
    }

    private sealed record GitCommandResult(bool Success, string Output);
}
