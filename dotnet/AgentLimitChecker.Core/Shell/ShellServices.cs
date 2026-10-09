using System.Text.Json.Nodes;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Claude;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Core.Shell;

public interface IDetailsPresenter
{
    bool Toggle(ShellSnapshot snapshot);
    void Show(ShellSnapshot snapshot);
}

public interface IAutoLaunchService
{
    bool IsEnabled { get; }
    /// <summary>失敗したときは false を返し、状態は変えない。</summary>
    bool SetEnabled(bool enabled);
}

public sealed class RegistryAutoLaunchService(
    IAutoLaunchRegistry registry,
    string executablePath,
    string valueName = AutoLaunchPolicy.DefaultValueName,
    Action<string>? logError = null) : IAutoLaunchService
{
    // レジストリの失敗で起動や設定の操作を止めない。読めなければ無効として扱う。
    public bool IsEnabled
    {
        get
        {
            try { return AutoLaunchPolicy.IsEnabled(registry.GetRunValue(valueName), registry.GetStartupApprovedValue(valueName)); }
            catch (Exception) { logError?.Invoke("[autoLaunch] isEnabled failed"); return false; }
        }
    }

    // 利用者が有効にしたときは、タスク マネージャーで無効にした印も消す。Electron の setLoginItemSettings と同じ扱いである。
    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                registry.SetRunValue(valueName, AutoLaunchPolicy.BuildValue(executablePath));
                registry.DeleteStartupApprovedValue(valueName);
            }
            else registry.DeleteRunValue(valueName);
            return true;
        }
        catch (Exception) { logError?.Invoke("[autoLaunch] setEnabled failed"); return false; }
    }

    // 起動時の登録し直しは exe のパスだけを替え、タスク マネージャーでの無効化は残す。
    public void RegisterCurrentExecutable()
    {
        try { registry.SetRunValue(valueName, AutoLaunchPolicy.BuildValue(executablePath)); }
        catch (Exception) { logError?.Invoke("[autoLaunch] re-registration failed"); }
    }
}

public interface ILoginLauncher
{
    bool Start(string target, CodexAccount? account, bool silent);
}

public interface IShellRuntime
{
    long NowMilliseconds { get; }
    IDisposable Schedule(TimeSpan delay, Func<Task> callback, bool repeat = false);
    Task<string?> FileSignatureAsync(string file);
}

public sealed class ShellRuntime(Action<string> logError) : IShellRuntime
{
    public long NowMilliseconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public IDisposable Schedule(TimeSpan delay, Func<Task> callback, bool repeat = false) =>
        new Timer(async _ =>
        {
            try { await callback().ConfigureAwait(false); }
            catch { logError("[shell] scheduled operation failed"); }
        }, null, delay, repeat ? delay : Timeout.InfiniteTimeSpan);

    public Task<string?> FileSignatureAsync(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return Task.FromResult<string?>(info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : null);
        }
        catch (IOException) { return Task.FromResult<string?>(null); }
        catch (UnauthorizedAccessException) { return Task.FromResult<string?>(null); }
    }
}

public sealed class ShellProviders
{
    public required Func<Task<UsageSnapshot>> FetchClaude { get; init; }
    public required Func<string, Task<UsageSnapshot>> FetchCodex { get; init; }
    public required Func<IReadOnlyList<CodexAccount>> DiscoverHomes { get; init; }
    public required Func<CodexAccount> DefaultAccount { get; init; }
    public required string ClaudeCredentialsFile { get; init; }
    public required Action<string?> ShutdownCodex { get; init; }
    public required Action ShutdownClaude { get; init; }

    public static ShellProviders Create(ClaudeProvider claude, CodexProvider codex) => new()
    {
        FetchClaude = () => claude.FetchAsync(), FetchCodex = home => codex.FetchAsync(home),
        DiscoverHomes = () => CodexHomes.DiscoverCodexHomes(),
        DefaultAccount = () =>
        {
            var home = CodexHomes.DefaultCodexHome();
            return new(CodexHomes.NormalizeHomePath(home), Path.GetFileName(Path.TrimEndingDirectorySeparator(home)),
                home, CodexProvider.AuthFilePath(home), true);
        },
        ClaudeCredentialsFile = ClaudeProvider.CredentialsPath(),
        ShutdownCodex = home => { if (home is null) codex.Dispose(); else codex.Shutdown(home); }, ShutdownClaude = claude.Dispose
    };
}
