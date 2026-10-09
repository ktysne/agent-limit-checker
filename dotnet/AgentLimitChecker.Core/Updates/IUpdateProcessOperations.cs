namespace AgentLimitChecker.Core.Updates;

public enum UpdateLaunchOutcome
{
    LaunchFailed,
    Signaled,
    ExitedWithoutSignal,
    Survived,
}
public sealed record UpdateLaunchResult(UpdateLaunchOutcome Outcome, string? Detail = null);
public interface IUpdateProcessOperations
{
    IDisposable? TryAcquireApplierLock(TimeSpan timeout);
    bool WaitForInstanceProcessesToExit(string installExecutablePath, TimeSpan timeout);
    UpdateLaunchResult LaunchAndWaitForStartup(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string startupEventName,
        TimeSpan survivalTimeout);
    bool LaunchDetached(string executablePath, string workingDirectory);
    TimeSpan Elapsed { get; }

    void Sleep(TimeSpan duration);
}
public interface IUpdateApplyReporter
{
    void Log(string message);

    void ShowError(string message);
}
