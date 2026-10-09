using System.Text.Json.Nodes;
using AgentLimitChecker.Core.Notifications;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Claude;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Core.Shell;

public sealed class ShellController : IDisposable
{
    public static readonly TimeSpan LoginWatchInterval = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan LoginWatchTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AutoLoginCooldown = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly SettingsStore settings;
    private readonly ShellProviders providers;
    private readonly IShellRuntime runtime;
    private readonly ILoginLauncher launcher;
    private readonly IAutoLaunchService autoLaunch;
    private readonly Action<NotificationSnapshot> notify;
    private readonly Action disposeNotifications;
    private readonly Action<string> log;
    private readonly Dictionary<string, LoginWatch> watchers = new(StringComparer.Ordinal);
    private readonly string version;
    private ServiceResult? claude;
    private IReadOnlyList<AccountResult> accounts = [];
    private HashSet<string> knownHomes = [];
    private long fetchedAt;
    private long lastAutoLoginAt;
    private bool reauthPending;
    private bool polling;
    private Task? refreshTask;
    private bool disposed;
    private IDisposable? pollTimer;
    private string theme = "light";

    public event Action<ShellSnapshot>? SnapshotChanged;
    public event Action? ShowDetailsRequested;

    public ShellController(SettingsStore settings, ShellProviders providers, IShellRuntime runtime,
        ILoginLauncher launcher, IAutoLaunchService autoLaunch, Action<NotificationSnapshot> notify,
        Action disposeNotifications, string version, Action<string>? log = null)
    {
        this.settings = settings; this.providers = providers; this.runtime = runtime; this.launcher = launcher;
        this.autoLaunch = autoLaunch; this.notify = notify; this.disposeNotifications = disposeNotifications;
        this.version = version; this.log = log ?? (_ => { });
    }

    public ShellSnapshot Snapshot { get { lock (gate) return BuildSnapshot(); } }

    private ShellSnapshot BuildSnapshot() => new(claude, accounts, providers.DefaultAccount(), fetchedAt,
        settings.Load(), autoLaunch.IsEnabled, polling, theme, version,
        new(watchers.ContainsKey("claude"), watchers.Keys.Where(k => k.StartsWith("codex:", StringComparison.Ordinal))
            .ToDictionary(k => k[6..], _ => true, StringComparer.Ordinal)));

    private void Publish() => SnapshotChanged?.Invoke(BuildSnapshot());

    public async Task StartAsync()
    {
        await RefreshNowAsync().ConfigureAwait(false);
        lock (gate) { if (!disposed) RestartPolling(); }
    }

    // 取得中に呼ばれたら新しい取得は始めず、進行中の取得の完了を待って返る。
    public async Task RefreshNowAsync()
    {
        Task? running = null;
        TaskCompletionSource? done = null;
        lock (gate)
        {
            if (disposed) return;
            if (polling) running = refreshTask;
            else
            {
                polling = true;
                done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                refreshTask = done.Task;
                Publish();
            }
        }
        if (done is null)
        {
            if (running is not null) await running.ConfigureAwait(false);
            return;
        }
        try { await RunRefreshAsync().ConfigureAwait(false); }
        finally { done.SetResult(); }
    }

    private async Task RunRefreshAsync()
    {
        try
        {
            // 探索とプロバイダの同期部分も UI スレッドから離す。
            var discovered = await Task.Run(providers.DiscoverHomes).ConfigureAwait(false);
            var claudeTask = SettleAsync(providers.FetchClaude);
            var codexTasks = discovered.Select(a => SettleAsync(() => providers.FetchCodex(a.Home))).ToArray();
            await Task.WhenAll(codexTasks.Prepend(claudeTask)).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed) return;
                var homes = discovered.Select(a => a.Home).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var home in knownHomes.Except(homes)) StopCodex(home);
                knownHomes = homes;
                claude = claudeTask.Result;
                var results = discovered.Select((a, i) => (a, codexTasks[i].Result)).ToArray();
                if (results.Length == 0) results = [(providers.DefaultAccount(), new(false, Error: new("codex_home_missing",
                    "Codex のホームディレクトリが見つかりません (~/.codex*)")))];
                accounts = Decorate(results);
                fetchedAt = runtime.NowMilliseconds;
                reauthPending = IsClaudeAuthError(claude.Error?.Code);
                polling = false;
                notify(BuildSnapshot().ToNotificationSnapshot());
                Publish();
            }
        }
        catch
        {
            lock (gate)
            {
                if (disposed) return;
                log("[poll] refresh failed");
                polling = false;
                Publish();
            }
        }
    }

    private async Task<ServiceResult> SettleAsync(Func<Task<UsageSnapshot>> fetch)
    {
        try { return new(true, await Task.Run(fetch).ConfigureAwait(false)); }
        catch (Exception error)
        {
            var code = (error as ProviderException)?.Code ?? "unknown";
            log("[poll] provider failed " + code);
            return new(false, Error: new(code, error is ProviderException ? error.Message : "利用状況の取得に失敗しました。",
                (error as ClaudeProviderException)?.RetryAfter));
        }
    }

    private IReadOnlyList<AccountResult> Decorate(IReadOnlyList<(CodexAccount Account, ServiceResult Result)> results)
    {
        var list = results.Select(r => r.Account).ToArray();
        var names = settings.Load().CodexAccountNames;
        var overrides = names.ToDictionary(p => p.Key, p => (object?)p.Value);
        return results.Select(r => new AccountResult(r.Account, r.Result,
            CodexHomes.AccountDisplayName(r.Account, list, overrides),
            names.TryGetValue(r.Account.Label, out var custom) && custom.Length > 0 ? custom : null,
            CodexHomes.AccountDisplayName(r.Account, list))).ToArray();
    }

    private void RestartPolling()
    {
        pollTimer?.Dispose();
        pollTimer = runtime.Schedule(TimeSpan.FromSeconds(settings.Load().PollingIntervalSec), RefreshNowAsync, repeat: true);
    }

    public void SetPollingInterval(int seconds)
    {
        if (seconds is not (30 or 60 or 120 or 300 or 600)) return;
        lock (gate)
        {
            if (disposed) return;
            settings.Save(new JsonObject { ["pollingIntervalSec"] = seconds });
            RestartPolling();
            notify(BuildSnapshot().ToNotificationSnapshot());
            Publish();
        }
    }

    public void SetAutoLaunch(bool enabled)
    {
        lock (gate)
        {
            if (disposed) return;
            autoLaunch.SetEnabled(enabled);
            if (settings.Load().AutoLaunch != enabled) settings.Save(new JsonObject { ["autoLaunch"] = enabled });
            Publish();
        }
    }

    public void SetNtfySettings(JsonObject partial)
    {
        lock (gate)
        {
            if (disposed) return;
            var patch = new JsonObject();
            foreach (var key in new[] { "topicUrl", "accessToken", "notifyFiveHour", "notifyWeekly", "notifyResetCreditsExpiry" })
                if (partial.ContainsKey(key)) patch[key] = partial[key]?.DeepClone();
            settings.Save(new JsonObject { ["ntfy"] = patch });
            notify(BuildSnapshot().ToNotificationSnapshot());
            Publish();
        }
    }

    public void SetAccountName(string label, string name)
    {
        lock (gate)
        {
            if (disposed || accounts.All(a => a.Account.Label != label)) return;
            settings.Save(new JsonObject { ["codexAccountNames"] = new JsonObject { [label] = name } });
            accounts = Decorate(accounts.Select(a => (a.Account, a.Result)).ToArray());
            notify(BuildSnapshot().ToNotificationSnapshot());
            Publish();
        }
    }

    public void SetTheme(string value)
    {
        lock (gate) { if (!disposed) { theme = value == "dark" ? "dark" : "light"; Publish(); } }
    }

    public CodexAccount ResolveCodexLoginAccount(string? id)
    {
        lock (gate)
        {
            var fallback = providers.DefaultAccount();
            return accounts.FirstOrDefault(a => a.Account.Id == id)?.Account
                ?? accounts.FirstOrDefault(a => a.Account.Id == fallback.Id)?.Account ?? fallback;
        }
    }

    public static bool IsClaudeAuthError(string? code) => code is "claude_unauthorized" or "claude_credentials_missing";
    public static bool CanStartReauth(bool pending, bool watching, long nowMs, long lastAttemptMs) =>
        pending && !watching && nowMs - lastAttemptMs > AutoLoginCooldown.TotalMilliseconds;

    public async Task OnDetailsOpenedAsync()
    {
        lock (gate)
        {
            if (disposed || !CanStartReauth(reauthPending, watchers.ContainsKey("claude"), runtime.NowMilliseconds, lastAutoLoginAt)) return;
            lastAutoLoginAt = runtime.NowMilliseconds;
        }
        await OpenLoginAsync("claude", silent: true).ConfigureAwait(false);
    }

    public async Task<bool> OpenLoginAsync(string target, string? accountId = null, bool silent = false)
    {
        target = target == "codex" ? "codex" : "claude";
        CodexAccount? account = target == "codex" ? ResolveCodexLoginAccount(accountId) : null;
        LoginWatch watch;
        var key = target == "codex" ? "codex:" + account!.Id : "claude";
        lock (gate)
        {
            if (disposed) return false;
            try { if (!launcher.Start(target, account, silent)) return false; }
            catch { log("[login] failed to spawn"); return false; }
            StopWatcher(key);
            watch = new(account);
            watchers[key] = watch;
            Publish();
        }
        var baseline = await runtime.FileSignatureAsync(account?.AuthFile ?? providers.ClaudeCredentialsFile).ConfigureAwait(false);
        lock (gate)
        {
            if (!IsCurrent(key, watch)) return true;
            watch.Baseline = baseline;
            watch.Deadline = runtime.NowMilliseconds + (long)LoginWatchTimeout.TotalMilliseconds;
            watch.Timer = runtime.Schedule(LoginWatchInterval, () => TickLoginAsync(key, watch));
        }
        return true;
    }

    private async Task TickLoginAsync(string key, LoginWatch watch)
    {
        lock (gate) { if (!IsCurrent(key, watch)) return; }
        var signature = await runtime.FileSignatureAsync(watch.Account?.AuthFile ?? providers.ClaudeCredentialsFile).ConfigureAwait(false);
        var completed = false;
        lock (gate)
        {
            if (!IsCurrent(key, watch)) return;
            if (signature is not null && signature != watch.Baseline)
            {
                StopWatcher(key);
                log("[login] credentials updated");
                if (watch.Account is not null) StopCodex(watch.Account.Home);
                completed = true;
            }
            else if (runtime.NowMilliseconds >= watch.Deadline)
            {
                StopWatcher(key);
                log("[login] watch timed out");
            }
            else
            {
                watch.Timer?.Dispose();
                watch.Timer = runtime.Schedule(LoginWatchInterval, () => TickLoginAsync(key, watch));
            }
            Publish();
        }
        if (!completed) return;
        // ログインの完了より前に始まった取得は古い資格情報で動いているので、その完了を待ってから取り直す。
        Task? stale;
        lock (gate) stale = polling ? refreshTask : null;
        if (stale is not null) await stale.ConfigureAwait(false);
        await RefreshNowAsync().ConfigureAwait(false);
        lock (gate) { if (!disposed) ShowDetailsRequested?.Invoke(); }
    }

    private bool IsCurrent(string key, LoginWatch watch) => !disposed && watchers.GetValueOrDefault(key) == watch;
    private void StopWatcher(string key) { if (watchers.Remove(key, out var watch)) watch.Timer?.Dispose(); }
    private void StopCodex(string? home) { try { providers.ShutdownCodex(home); } catch { log("[codex] shutdown failed"); } }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            polling = false;
            pollTimer?.Dispose();
            foreach (var key in watchers.Keys.ToArray()) StopWatcher(key);
            disposeNotifications();
            StopCodex(null);
            try { providers.ShutdownClaude(); } catch { log("[claude] shutdown failed"); }
        }
    }

    private sealed class LoginWatch(CodexAccount? account)
    {
        public CodexAccount? Account { get; } = account;
        public string? Baseline { get; set; }
        public long Deadline { get; set; }
        public IDisposable? Timer { get; set; }
    }
}
