namespace AgentLimitChecker.Core.Shell;

public interface IAutoLaunchRegistry
{
    string? GetRunValue(string valueName);
    void SetRunValue(string valueName, string value);
    void DeleteRunValue(string valueName);
    byte[]? GetStartupApprovedValue(string valueName);
    void DeleteStartupApprovedValue(string valueName);
}

public static class AutoLaunchPolicy
{
    public const string DefaultValueName = "com.agent-limit-checker.app";

    public static string BuildValue(string executablePath) => $"\"{executablePath}\" --hidden";

    public static bool IsEnabled(string? runValue, byte[]? startupApprovedValue) =>
        runValue is not null && (startupApprovedValue is null || startupApprovedValue.Length != 12 ||
            startupApprovedValue[0] % 2 == 0);
}

public static class AutoLaunchStartup
{
    public static void Initialize(bool autoLaunchEnabled, bool debugBuild, RegistryAutoLaunchService service,
        Action<string> log)
    {
        if (debugBuild)
        {
            log("debug build: skip auto-launch re-registration");
            return;
        }

        if (autoLaunchEnabled) service.RegisterCurrentExecutable();
    }
}
