using System.Text.Json.Nodes;
using AgentLimitChecker.Core.Settings;
using AgentLimitChecker.Core.Shell;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public sealed class UpdateMonitorTests
{
    private static UpdateCheckResult Available(string version = "5.0.0") => new(UpdateCheckKind.Available,
        new UpdateInfo(Version.Parse(version), version, new Uri("https://ktysne.info/agent-limit-checker/update.zip")), null);

    [Fact]
    public async Task StartupAndDailyChecksNotifyEachVersionOnce()
    {
        var runtime = new Runtime();
        var count = 0;
        using var monitor = new UpdateMonitor(runtime, _ => { count++; return Task.FromResult(Available()); });
        var notifications = new List<UpdateInfo>();
        monitor.UpdateFound += notifications.Add;
        await monitor.StartAsync(true);
        Assert.Equal(1, count);
        Assert.Equal(TimeSpan.FromHours(24), runtime.Delay);
        Assert.True(runtime.Repeat);
        await runtime.Callback!();
        Assert.Equal(2, count);
        Assert.Single(notifications);
        Assert.Equal("アップデートがあります(v5.0.0)", monitor.Snapshot.MenuLabel);
        await monitor.StartAsync(true);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task DisabledAutomaticChecksStillAllowManualChecks()
    {
        var runtime = new Runtime();
        var count = 0;
        using var monitor = new UpdateMonitor(runtime, _ => { count++; return Task.FromResult(Available()); });
        await monitor.StartAsync(false);
        Assert.Null(runtime.Callback);
        Assert.Equal(0, count);
        await monitor.CheckAsync();
        Assert.Equal(1, count);
        monitor.SetAutomaticChecks(true);
        await runtime.Callback!();
        monitor.SetAutomaticChecks(false);
        Assert.True(runtime.Timer!.Disposed);
        await runtime.Callback!();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ConcurrentChecksAreCoalescedAndDisposalCancelsTheRequest()
    {
        var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var count = 0;
        var monitor = new UpdateMonitor(new Runtime(), ct => { count++; token = ct; return pending.Task; });
        var first = monitor.CheckAsync();
        Assert.True(monitor.Snapshot.Checking);
        Assert.Equal("確認しています…", monitor.Snapshot.StatusText);
        await monitor.CheckAsync();
        Assert.Equal(1, count);
        monitor.Dispose();
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(Available());
        await first;
        Assert.Null(monitor.Snapshot.Available);
    }

    [Fact]
    public async Task FailedChecksKeepTheKnownUpdateAndUpToDateClearsIt()
    {
        var result = Available();
        using var monitor = new UpdateMonitor(new Runtime(), _ => Task.FromResult(result));
        await monitor.CheckAsync();
        result = new(UpdateCheckKind.Failed, null, "offline");
        await monitor.CheckAsync();
        Assert.NotNull(monitor.Snapshot.MenuLabel);
        Assert.Contains("再試行", monitor.Snapshot.StatusText);
        result = new(UpdateCheckKind.UpToDate, null, null);
        await monitor.CheckAsync();
        Assert.Null(monitor.Snapshot.MenuLabel);
        Assert.Equal("最新版です。", monitor.Snapshot.StatusText);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"checkForUpdatesOnStartup\":true}", true)]
    [InlineData("{\"checkForUpdatesOnStartup\":false}", false)]
    [InlineData("{\"checkForUpdatesOnStartup\":null}", true)]
    public void UpdateSettingIsNormalizedWithEnabledDefault(string json, bool expected)
    {
        var settings = SettingsStore.Normalize(JsonNode.Parse(json));
        Assert.Equal(expected, settings.CheckForUpdatesOnStartup);
        Assert.DoesNotContain("checkForUpdatesOnStartup", settings.AdditionalProperties.Keys);
    }

    [Fact]
    public void UpdateSettingIsSavedAndUnknownElectronKeysArePreserved()
    {
        using var directory = new ProviderTestDirectory();
        var path = Path.Combine(directory.Root, "settings.json");
        var store = new SettingsStore(path);
        store.Save(new JsonObject { ["electronOnly"] = "kept", ["checkForUpdatesOnStartup"] = false });
        var loaded = new SettingsStore(path).Load();
        Assert.False(loaded.CheckForUpdatesOnStartup);
        Assert.Equal("kept", loaded.AdditionalProperties["electronOnly"].GetString());
    }

    private sealed class Runtime : IShellRuntime
    {
        public long NowMilliseconds => 0;
        public TimeSpan Delay { get; private set; }
        public bool Repeat { get; private set; }
        public Func<Task>? Callback { get; private set; }
        public Scheduled? Timer { get; private set; }
        public IDisposable Schedule(TimeSpan delay, Func<Task> callback, bool repeat = false)
        {
            Delay = delay; Callback = callback; Repeat = repeat;
            return Timer = new Scheduled();
        }
        public Task<string?> FileSignatureAsync(string file) => Task.FromResult<string?>(null);
    }

    private sealed class Scheduled : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
