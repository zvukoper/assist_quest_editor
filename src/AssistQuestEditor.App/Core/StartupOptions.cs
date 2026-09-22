namespace AssistQuestEditor.App;

/// <summary>Разбор режимов запуска приложения.</summary>
public static class StartupOptions
{
    public const string CiTestArgument = "-citest";

    public static bool IsCiTest(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, CiTestArgument, StringComparison.OrdinalIgnoreCase));

    public static bool IsApplicationSwitch(string arg) =>
        arg.StartsWith("--", StringComparison.Ordinal) ||
        string.Equals(arg, CiTestArgument, StringComparison.OrdinalIgnoreCase);
}
