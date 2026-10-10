using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Core.Updates;

public sealed record UpdateMonitorSnapshot(bool Checking, UpdateInfo? Available, UpdateCheckResult? Result)
{
    public string? MenuLabel => Available is { } update ? $"アップデートがあります(v{update.VersionText})" : null;
    public string StatusText => Checking ? "確認しています…" : Result?.Kind switch
    {
        UpdateCheckKind.Available => $"新しいバージョン {Available?.VersionText} があります。",
        UpdateCheckKind.UpToDate => "最新版です。",
        UpdateCheckKind.Failed => "確認できませんでした。ネットワーク接続を確認して再試行してください。",
        _ => "",
    };
}

public sealed class UpdateMonitor : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly object gate = new();
    private readonly IShellRuntime runtime;
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> check;
    private readonly CancellationTokenSource cancellation = new();
    private readonly HashSet<string> notifiedVersions = new(StringComparer.Ordinal);
    private IDisposable? timer;
    private bool started;
    private bool enabled;
    private bool disposed;
    private UpdateMonitorSnapshot snapshot = new(false, null, null);

    public UpdateMonitor(IShellRuntime runtime, Func<CancellationToken, Task<UpdateCheckResult>> check)
    {
        this.runtime = runtime;
        this.check = check;
    }

    public event Action<UpdateMonitorSnapshot>? Changed;
    public event Action<UpdateInfo>? UpdateFound;
    public UpdateMonitorSnapshot Snapshot { get { lock (gate) return snapshot; } }

    public Task StartAsync(bool checkAutomatically)
    {
        lock (gate)
        {
            if (started || disposed) return Task.CompletedTask;
            started = true;
            SetAutomaticChecks(checkAutomatically);
        }
        return checkAutomatically ? CheckAsync() : Task.CompletedTask;
    }

    public void SetAutomaticChecks(bool value)
    {
        lock (gate)
        {
            if (disposed || enabled == value) return;
            enabled = value;
            timer?.Dispose();
            timer = value ? runtime.Schedule(CheckInterval, CheckAutomaticallyAsync, repeat: true) : null;
        }
    }

    private Task CheckAutomaticallyAsync()
    {
        lock (gate) return enabled && !disposed ? CheckAsync() : Task.CompletedTask;
    }

    public async Task CheckAsync()
    {
        lock (gate)
        {
            if (disposed || snapshot.Checking) return;
            snapshot = snapshot with { Checking = true };
        }
        Changed?.Invoke(Snapshot);
        UpdateCheckResult result;
        try { result = await check(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception ex) { result = new(UpdateCheckKind.Failed, null, ex.Message); }
        UpdateInfo? notification = null;
        lock (gate)
        {
            if (disposed) return;
            var available = result.Kind == UpdateCheckKind.Available ? result.Update
                : result.Kind == UpdateCheckKind.Failed ? snapshot.Available : null;
            snapshot = new(false, available, result);
            if (result.Kind == UpdateCheckKind.Available && available is not null && notifiedVersions.Add(available.VersionText))
                notification = available;
        }
        Changed?.Invoke(Snapshot);
        if (notification is not null) UpdateFound?.Invoke(notification);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer?.Dispose();
            cancellation.Cancel();
        }
    }
}
