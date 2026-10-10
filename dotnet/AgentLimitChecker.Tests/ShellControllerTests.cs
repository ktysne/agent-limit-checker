using System.Text.Json.Nodes;
using AgentLimitChecker.Core.Notifications;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Claude;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Settings;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class ShellControllerTests
{
    private static UsageSnapshot Usage(double utilization = .5) => new(new(utilization, 2000000), null, [], null, null, "Pro");

    [Fact]
    public void InitialSnapshotExposesDefaultHomeSettingsVersionThemeAndEmptyLoginProgress()
    {
        using var h = new Harness();
        var snapshot = h.Controller.Snapshot;
        Assert.Null(snapshot.Claude);
        Assert.Empty(snapshot.CodexAccounts);
        Assert.Equal(h.Default, snapshot.CodexDefaultAccount);
        Assert.Equal("4.0.0", snapshot.AppVersion);
        Assert.Equal("light", snapshot.Theme);
        Assert.False(snapshot.IsPolling);
        Assert.Equal(0, snapshot.FetchedAt);
        Assert.False(snapshot.LoginInProgress.Claude);
        Assert.Empty(snapshot.LoginInProgress.Codex);
    }

    [Fact]
    public async Task MissingHomesProduceLoginTargetAndDecoratedMissingResult()
    {
        using var h = new Harness();
        h.Homes = [];
        await h.Controller.RefreshNowAsync();
        var account = Assert.Single(h.Controller.Snapshot.CodexAccounts);
        Assert.Equal(h.Default, account.Account);
        Assert.False(account.Result.Ok);
        Assert.Equal("codex_home_missing", account.Result.Error!.Code);
        Assert.Equal("Codex", account.DisplayName);
        Assert.Equal("Codex", account.DefaultName);
        Assert.Null(account.CustomName);
    }

    [Fact]
    public async Task FetchesRunInParallelAndOneFailureDoesNotStopOtherAccounts()
    {
        using var h = new Harness();
        var claude = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Task<UsageSnapshot> Fetch(Task<UsageSnapshot> result)
        {
            if (Interlocked.Increment(ref count) == 3) started.SetResult();
            return result;
        }
        h.Homes = [h.Default, h.Other];
        h.ClaudeFetch = () => Fetch(claude.Task);
        h.CodexFetch = home => Fetch(home == h.Default.Home ? first.Task : second.Task);
        var refresh = h.Controller.RefreshNowAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Controller.Snapshot.IsPolling);
        var duplicate = h.Controller.RefreshNowAsync();
        Assert.False(duplicate.IsCompleted);
        Assert.Equal(3, count);
        claude.SetException(new ClaudeProviderException("claude_rate_limited", "rate limited", retryAfter: 42));
        first.SetException(new ProviderException("codex_timeout", "timeout"));
        second.SetResult(Usage(.9));
        await duplicate;
        Assert.Equal(3, count);
        await refresh;
        var snapshot = h.Controller.Snapshot;
        Assert.False(snapshot.IsPolling);
        Assert.Equal(42d, snapshot.Claude!.Error!.RetryAfter);
        Assert.False(snapshot.CodexAccounts[0].Result.Ok);
        Assert.True(snapshot.CodexAccounts[1].Result.Ok);
        Assert.Equal(h.Runtime.NowMilliseconds, snapshot.FetchedAt);
        Assert.Equal(.9, TrayPresentation.CodexUtilization(snapshot));
    }

    [Fact]
    public async Task DisappearingHomeStopsOnlyItsServer()
    {
        using var h = new Harness();
        h.Homes = [h.Default, h.Other];
        await h.Controller.RefreshNowAsync();
        h.Homes = [h.Default];
        await h.Controller.RefreshNowAsync();
        Assert.Equal(new[] { h.Other.Home }, h.StoppedHomes);
    }

    [Fact]
    public async Task RenamingDecoratesSnapshotTrayAndNotificationsAndRejectsUnknownLabels()
    {
        using var h = new Harness();
        h.Homes = [h.Default, h.Other];
        await h.Controller.RefreshNowAsync();
        h.Controller.SetAccountName(h.Other.Label, "レビュー用");
        var account = h.Controller.Snapshot.CodexAccounts[1];
        Assert.Equal("レビュー用", account.DisplayName);
        Assert.Equal("レビュー用", account.CustomName);
        Assert.Equal("Codex (.codex-review)", account.DefaultName);
        Assert.Contains("レビュー用: 50%", TrayPresentation.Tooltip(h.Controller.Snapshot));
        Assert.Equal("レビュー用", h.Notifications[^1].CodexAccounts[1].DisplayName);
        h.Controller.SetAccountName("../../unknown", "wrong");
        Assert.False(h.Settings.Load().CodexAccountNames.ContainsKey("../../unknown"));
        h.Controller.SetAccountName(h.Other.Label, "");
        Assert.Null(h.Controller.Snapshot.CodexAccounts[1].CustomName);
        Assert.Equal(account.DefaultName, h.Controller.Snapshot.CodexAccounts[1].DisplayName);
    }

    [Fact]
    public async Task PollingSettingsRestartTimerWithoutImmediateFetchAndUpdateNotifications()
    {
        using var h = new Harness();
        await h.Controller.StartAsync();
        var timer = Assert.Single(h.Runtime.Timers);
        Assert.True(timer.Repeat);
        Assert.Equal(TimeSpan.FromSeconds(300), timer.Delay);
        h.Controller.SetPollingInterval(60);
        Assert.True(timer.Disposed);
        Assert.Equal(60, h.Controller.Snapshot.Settings.PollingIntervalSec);
        Assert.Equal(TimeSpan.FromSeconds(60), h.Runtime.Timers[^1].Delay);
        Assert.Equal(2, h.Notifications.Count);
        h.Controller.SetPollingInterval(17);
        Assert.Equal(2, h.Runtime.Timers.Count);
        await h.Runtime.Timers[^1].FireAsync();
        Assert.Equal(3, h.Notifications.Count);
    }

    [Fact]
    public async Task NotificationUpdatesFollowFetchIntervalRenameAndNtfySettings()
    {
        using var h = new Harness();
        await h.Controller.RefreshNowAsync();
        Assert.True(h.Notifications[0].Claude!.IsAvailable);
        Assert.Equal("claude", h.Notifications[0].Claude!.ServiceId);
        Assert.Equal(h.Default.Id, h.Notifications[0].CodexAccounts[0].ServiceId);
        h.Controller.SetNtfySettings(new JsonObject { ["notifyFiveHour"] = true, ["topicUrl"] = "https://ntfy.sh/test", ["unexpected"] = true });
        Assert.True(h.Settings.Load().Ntfy.NotifyFiveHour);
        Assert.False(h.Settings.Load().AdditionalProperties.ContainsKey("unexpected"));
        Assert.Equal(2, h.Notifications.Count);
        h.Controller.SetAutoLaunch(true);
        Assert.True(h.Controller.Snapshot.AutoLaunchEnabled);
        Assert.True(h.Settings.Load().AutoLaunch);
        Assert.Equal(2, h.Notifications.Count);
    }

    [Fact]
    public async Task CodexLoginUsesSelectedHomeAndUnknownIdFallsBackToDiscoveredDefault()
    {
        using var h = new Harness();
        h.Homes = [h.Default, h.Other];
        await h.Controller.RefreshNowAsync();
        Assert.True(await h.Controller.OpenLoginAsync("codex", h.Other.Id));
        Assert.Equal(h.Other, h.Launcher.Starts[0].Account);
        Assert.True(h.Controller.Snapshot.LoginInProgress.Codex[h.Other.Id]);
        Assert.Equal(h.Default, h.Controller.ResolveCodexLoginAccount("../../arbitrary"));
        Assert.Equal(h.Default, h.Controller.ResolveCodexLoginAccount(null));
        Assert.True(await h.Controller.OpenLoginAsync("codex", h.Default.Id));
        Assert.Equal(2, h.Controller.Snapshot.LoginInProgress.Codex.Count);
    }

    [Fact]
    public async Task ChangedCredentialsStopAccountServerRefreshAndRequestDetails()
    {
        using var h = new Harness();
        await h.Controller.OpenLoginAsync("codex", h.Default.Id);
        var timer = Assert.Single(h.Runtime.Timers);
        Assert.Equal(ShellController.LoginWatchInterval, timer.Delay);
        var order = new List<string>();
        h.Controller.SnapshotChanged += s => { if (s.FetchedAt != 0 && !s.IsPolling) order.Add("fetched"); };
        h.Controller.ShowDetailsRequested += () => order.Add("details");
        h.Runtime.Signature = "changed";
        await timer.FireAsync();
        Assert.True(timer.Disposed);
        Assert.Equal(new[] { h.Default.Home }, h.StoppedHomes);
        Assert.Equal(new[] { "fetched", "details" }, order);
        Assert.Empty(h.Controller.Snapshot.LoginInProgress.Codex);
        Assert.Single(h.Notifications);
    }

    [Fact]
    public async Task LoginCompletedDuringRefreshFetchesAgainAfterTheStaleRefreshAndThenRequestsDetails()
    {
        using var h = new Harness();
        await h.Controller.OpenLoginAsync("claude");
        var timer = Assert.Single(h.Runtime.Timers);
        var stale = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        h.ClaudeFetch = () => Interlocked.Increment(ref fetches) == 1 ? stale.Task : Task.FromResult(Usage(.2));
        var order = new List<string>();
        h.Controller.ShowDetailsRequested += () => order.Add($"details:{Volatile.Read(ref fetches)}");
        var running = h.Controller.RefreshNowAsync();
        h.Runtime.Signature = "changed";
        var tick = timer.FireAsync();
        Assert.False(tick.IsCompleted);
        stale.SetException(new ProviderException("claude_unauthorized", "stale"));
        await running;
        await tick;
        Assert.Equal(2, fetches);
        Assert.Equal(["details:2"], order);
        Assert.True(h.Controller.Snapshot.Claude!.Ok);
    }

    [Fact]
    public async Task MissingCredentialsDoNotCompleteAndWatcherTimesOutAtFiveMinutes()
    {
        using var h = new Harness();
        h.Runtime.Signature = null;
        await h.Controller.OpenLoginAsync("claude");
        h.Runtime.NowMilliseconds += 299999;
        await h.Runtime.Timers[^1].FireAsync();
        Assert.True(h.Controller.Snapshot.LoginInProgress.Claude);
        h.Runtime.NowMilliseconds++;
        await h.Runtime.Timers[^1].FireAsync();
        Assert.False(h.Controller.Snapshot.LoginInProgress.Claude);
        Assert.Empty(h.Notifications);
        Assert.Contains("[login] watch timed out", h.Logs);
    }

    [Fact]
    public async Task CredentialsCreatedFromMissingBaselineCompleteEvenAtDeadline()
    {
        using var h = new Harness();
        h.Runtime.Signature = null;
        await h.Controller.OpenLoginAsync("claude");
        h.Runtime.NowMilliseconds += 300000;
        h.Runtime.Signature = "created";
        await h.Runtime.Timers[^1].FireAsync();
        Assert.False(h.Controller.Snapshot.LoginInProgress.Claude);
        Assert.Single(h.Notifications);
    }

    [Fact]
    public async Task RestartingSameWatcherDoesNotCancelOtherAccount()
    {
        using var h = new Harness();
        h.Homes = [h.Default, h.Other];
        await h.Controller.RefreshNowAsync();
        await h.Controller.OpenLoginAsync("codex", h.Other.Id);
        var otherTimer = h.Runtime.Timers[^1];
        await h.Controller.OpenLoginAsync("codex", h.Default.Id);
        var previous = h.Runtime.Timers[^1];
        await h.Controller.OpenLoginAsync("codex", h.Default.Id);
        Assert.True(previous.Disposed);
        Assert.False(otherTimer.Disposed);
        Assert.Equal(2, h.Controller.Snapshot.LoginInProgress.Codex.Count);
    }

    [Fact]
    public async Task SupersededTickCannotCompleteAfterSignatureAwait()
    {
        using var h = new Harness();
        await h.Controller.OpenLoginAsync("claude");
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runtime.ReadSignature = _ => read.Task;
        var tick = h.Runtime.Timers[^1].FireAsync();
        h.Runtime.ReadSignature = null;
        await h.Controller.OpenLoginAsync("claude");
        read.SetResult("changed");
        await tick;
        Assert.True(h.Controller.Snapshot.LoginInProgress.Claude);
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task SupersededBaselineCannotInstallAnOldWatcher()
    {
        using var h = new Harness();
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Runtime.ReadSignature = _ => read.Task;
        var login = h.Controller.OpenLoginAsync("claude");
        h.Runtime.ReadSignature = null;
        await h.Controller.OpenLoginAsync("claude");
        read.SetResult("old");
        await login;
        Assert.Single(h.Runtime.Timers);
    }

    [Theory]
    [InlineData("claude_unauthorized", true)]
    [InlineData("claude_credentials_missing", true)]
    [InlineData("claude_rate_limited", false)]
    [InlineData("claude_refresh_expired", false)]
    [InlineData("codex_rpc_error", false)]
    public void ReauthPendingMatchesOnlyElectronAuthCodes(string code, bool pending) => Assert.Equal(pending, ShellController.IsClaudeAuthError(code));

    [Theory]
    [InlineData(true, false, 600001, 0, true)]
    [InlineData(true, false, 600000, 0, false)]
    [InlineData(true, true, 600001, 0, false)]
    [InlineData(false, false, 600001, 0, false)]
    [InlineData(true, false, 1200000, 600001, false)]
    public void ReauthRequiresPendingNoWatcherAndStrictCooldown(bool pending, bool watcher, long now, long last, bool expected) =>
        Assert.Equal(expected, ShellController.CanStartReauth(pending, watcher, now, last));

    [Fact]
    public async Task PollingOnlyRecordsReauthAndOpeningDetailsStartsSilentLoginWithCooldown()
    {
        using var h = new Harness();
        h.ClaudeFetch = () => throw new ProviderException("claude_unauthorized", "expired");
        await h.Controller.RefreshNowAsync();
        Assert.Empty(h.Launcher.Starts);
        await h.Controller.OnDetailsOpenedAsync();
        Assert.True(Assert.Single(h.Launcher.Starts).Silent);
        await h.Controller.OnDetailsOpenedAsync();
        Assert.Single(h.Launcher.Starts);
        h.Runtime.NowMilliseconds += 300000;
        await h.Runtime.Timers[^1].FireAsync();
        await h.Controller.OnDetailsOpenedAsync();
        Assert.Single(h.Launcher.Starts);
        h.Runtime.NowMilliseconds += 300001;
        await h.Controller.OnDetailsOpenedAsync();
        Assert.Equal(2, h.Launcher.Starts.Count);
    }

    [Fact]
    public async Task SuccessfulPollClearsPendingReauthAndMissingCliStillConsumesCooldown()
    {
        using var h = new Harness();
        h.ClaudeFetch = () => throw new ProviderException("claude_credentials_missing", "missing");
        await h.Controller.RefreshNowAsync();
        h.Launcher.Success = false;
        await h.Controller.OnDetailsOpenedAsync();
        Assert.False(h.Controller.Snapshot.LoginInProgress.Claude);
        await h.Controller.OnDetailsOpenedAsync();
        Assert.Single(h.Launcher.Starts);
        h.ClaudeFetch = () => Task.FromResult(Usage());
        await h.Controller.RefreshNowAsync();
        h.Runtime.NowMilliseconds += 600001;
        await h.Controller.OnDetailsOpenedAsync();
        Assert.Single(h.Launcher.Starts);
    }

    [Fact]
    public async Task QuitCancelsTimersStopsProvidersAndIgnoresInFlightFetch()
    {
        using var h = new Harness();
        await h.Controller.StartAsync();
        await h.Controller.OpenLoginAsync("claude");
        var read = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.ClaudeFetch = () => read.Task;
        var refresh = h.Controller.RefreshNowAsync();
        h.Controller.Dispose();
        h.Controller.Dispose();
        read.SetResult(Usage());
        await refresh;
        Assert.All(h.Runtime.Timers, timer => Assert.True(timer.Disposed));
        Assert.Contains(null, h.StoppedHomes);
        Assert.Equal(1, h.ClaudeShutdowns);
        Assert.Equal(1, h.NotificationDisposals);
        Assert.Single(h.Notifications);
        Assert.False(await h.Controller.OpenLoginAsync("claude"));
    }

    [Fact]
    public async Task UnexpectedProviderExceptionDoesNotExposeItsMessageToSnapshotOrLogs()
    {
        using var h = new Harness();
        h.ClaudeFetch = () => throw new InvalidOperationException("token_secret");
        await h.Controller.RefreshNowAsync();
        Assert.Equal("unknown", h.Controller.Snapshot.Claude!.Error!.Code);
        Assert.DoesNotContain("token_secret", h.Controller.Snapshot.Claude.Error.Message);
        Assert.DoesNotContain(h.Logs, line => line.Contains("token_secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeFingerprintUsesFileMetadataAndReturnsNullForMissingFiles()
    {
        using var directory = new ProviderTestDirectory();
        var runtime = new ShellRuntime(_ => { });
        var file = Path.Combine(directory.Root, "credentials.json");
        Assert.Null(await runtime.FileSignatureAsync(file));
        File.WriteAllText(file, "first");
        var baseline = await runtime.FileSignatureAsync(file);
        Assert.NotNull(baseline);
        Assert.Equal(baseline, await runtime.FileSignatureAsync(file));
        File.WriteAllText(file, "updated credential");
        Assert.NotEqual(baseline, await runtime.FileSignatureAsync(file));
        File.Delete(file);
        Assert.Null(await runtime.FileSignatureAsync(file));
    }

    private sealed class Harness : IDisposable
    {
        private readonly ProviderTestDirectory directory = new();
        public SettingsStore Settings { get; }
        public FakeRuntime Runtime { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public ShellController Controller { get; }
        public CodexAccount Default { get; } = new("default", ".codex", "C:/test/.codex", "C:/test/.codex/auth.json", true);
        public CodexAccount Other { get; } = new("other", ".codex-review", "C:/test/.codex-review", "C:/test/.codex-review/auth.json", false);
        public IReadOnlyList<CodexAccount> Homes { get; set; }
        public Func<Task<UsageSnapshot>> ClaudeFetch { get; set; } = () => Task.FromResult(Usage());
        public Func<string, Task<UsageSnapshot>> CodexFetch { get; set; } = _ => Task.FromResult(Usage());
        public List<string?> StoppedHomes { get; } = [];
        public List<NotificationSnapshot> Notifications { get; } = [];
        public List<string> Logs { get; } = [];
        public int ClaudeShutdowns { get; private set; }
        public int NotificationDisposals { get; private set; }

        public Harness()
        {
            Homes = [Default];
            Settings = new SettingsStore(Path.Combine(directory.Root, "settings.json"));
            Controller = new(Settings, new ShellProviders
            {
                FetchClaude = () => ClaudeFetch(), FetchCodex = home => CodexFetch(home), DiscoverHomes = () => Homes,
                DefaultAccount = () => Default, ClaudeCredentialsFile = "C:/test/claude/.credentials.json",
                ShutdownCodex = home => StoppedHomes.Add(home), ShutdownClaude = () => ClaudeShutdowns++
            }, Runtime, Launcher, new SettingsAutoLaunchService(Settings), snapshot => Notifications.Add(snapshot),
                () => NotificationDisposals++, "4.0.0", Logs.Add);
        }

        public void Dispose() { Controller.Dispose(); directory.Dispose(); }
    }

    private sealed class FakeLauncher : ILoginLauncher
    {
        public bool Success { get; set; } = true;
        public List<(string Target, CodexAccount? Account, bool Silent)> Starts { get; } = [];
        public bool Start(string target, CodexAccount? account, bool silent) { Starts.Add((target, account, silent)); return Success; }
    }

    private sealed class FakeRuntime : IShellRuntime
    {
        public long NowMilliseconds { get; set; } = 1000000;
        public string? Signature { get; set; } = "baseline";
        public Func<string, Task<string?>>? ReadSignature { get; set; }
        public List<FakeTimer> Timers { get; } = [];
        public Task<string?> FileSignatureAsync(string file) => ReadSignature?.Invoke(file) ?? Task.FromResult(Signature);
        public IDisposable Schedule(TimeSpan delay, Func<Task> callback, bool repeat = false)
        {
            var timer = new FakeTimer(delay, callback, repeat);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class FakeTimer(TimeSpan delay, Func<Task> callback, bool repeat) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;
        public bool Repeat { get; } = repeat;
        public bool Disposed { get; private set; }
        public Task FireAsync() => callback();
        public void Dispose() => Disposed = true;
    }
}
