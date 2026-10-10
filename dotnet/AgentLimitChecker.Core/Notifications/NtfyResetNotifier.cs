using System.Globalization;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Core.Notifications;

public sealed class NtfyResetNotifier : IDisposable
{
    private const long DueGraceMs = 30 * 60 * 1000;
    private const long ResetCreditWarningMs = 5 * 60 * 60 * 1000;
    private const int RetryDelayMs = 5 * 1000;
    private const int MaxRetryAttempts = 3;
    private const int MaxTimerDelayMs = int.MaxValue;
    private static readonly (string BucketId, string WindowType, string WindowLabel)[] ResetBuckets =
    [
        ("fiveHour", "fiveHour", "5時間"),
        ("weekly", "weekly", "週次"),
    ];

    private readonly object gate = new();
    private readonly Func<AppSettings> getSettings;
    private readonly Func<NtfySettings, NtfyMessage, CancellationToken, Task<NtfySendResult>> sendMessage;
    private readonly Func<long> nowMilliseconds;
    private readonly Func<Action, int, IDisposable> setTimer;
    private readonly Action<string> logInfo;
    private readonly Action<string> logWarn;
    private readonly Action<string> logError;
    private readonly IDisposable? ownedSender;
    private readonly Dictionary<string, PendingEvent> timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> sentEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InFlightEvent> inFlight = new(StringComparer.Ordinal);
    private Dictionary<string, NotificationEvent> desiredEvents = new(StringComparer.Ordinal);
    private bool disposed;

    public NtfyResetNotifier(Func<AppSettings> getSettings, INtfyMessageSender? sender = null, AppLogger? logger = null)
    {
        this.getSettings = getSettings;
        var actualSender = sender ?? new NtfyMessageSender();
        ownedSender = sender is null && actualSender is IDisposable disposable ? disposable : null;
        sendMessage = actualSender.SendAsync;
        nowMilliseconds = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        setTimer = StartTimer;
        var actualLogger = logger ?? AppLogger.CreateDefault();
        logInfo = actualLogger.Info;
        logWarn = actualLogger.Warn;
        logError = actualLogger.Error;
    }

    internal NtfyResetNotifier(
        Func<AppSettings> getSettings,
        Func<NtfySettings, NtfyMessage, CancellationToken, Task<NtfySendResult>> sendMessage,
        Func<long> nowMilliseconds,
        Func<Action, int, IDisposable> setTimer,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string> logError)
    {
        this.getSettings = getSettings;
        this.sendMessage = sendMessage;
        this.nowMilliseconds = nowMilliseconds;
        this.setTimer = setTimer;
        this.logInfo = logInfo;
        this.logWarn = logWarn;
        this.logError = logError;
    }

    public void Update(NotificationSnapshot snapshot)
    {
        lock (gate)
        {
            if (disposed) return;
            var settings = getSettings();
            var config = settings.Ntfy;
            var now = nowMilliseconds();
            var desired = new Dictionary<string, NotificationEvent>(StringComparer.Ordinal);
            if (HasAnyNotificationEnabled(config))
            {
                foreach (var notificationEvent in CollectResetEvents(snapshot, config)
                    .Concat(CollectResetCreditsExpiryEvents(snapshot, config, now)))
                {
                    var eventAt = notificationEvent.Kind == NotificationKind.ResetCreditsExpiry
                        ? notificationEvent.ExpiresAt!.Value
                        : notificationEvent.ResetsAt!.Value;
                    if (sentEvents.TryGetValue(notificationEvent.Key, out var sentAt) && sentAt == eventAt) continue;
                    if (notificationEvent.Kind != NotificationKind.ResetCreditsExpiry &&
                        notificationEvent.ResetsAt < now - DueGraceMs) continue;
                    desired[notificationEvent.Key] = notificationEvent;
                }
            }

            desiredEvents = desired;
            foreach (var notificationEvent in desired.Values)
            {
                var identity = EventIdentity(notificationEvent);
                if (inFlight.TryGetValue(identity, out var sending))
                {
                    sending.Event = notificationEvent;
                    continue;
                }

                if (timers.TryGetValue(notificationEvent.Key, out var current) &&
                    EventIdentity(current.Event) == identity)
                {
                    current.Event = notificationEvent;
                    continue;
                }

                Cancel(notificationEvent.Key);
                Schedule(notificationEvent, 0);
            }

            foreach (var (key, scheduled) in timers.ToArray())
            {
                if (!desired.TryGetValue(key, out var notificationEvent) ||
                    EventIdentity(notificationEvent) != EventIdentity(scheduled.Event)) Cancel(key);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            desiredEvents.Clear();
            foreach (var key in timers.Keys.ToArray()) Cancel(key);
        }
        ownedSender?.Dispose();
    }

    public static string NormalizeTopicUrl(string? value)
    {
        var raw = (value ?? "").Trim().TrimEnd('/');
        if (raw.Length == 0 || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/") return "";
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    internal static IReadOnlyList<NotificationEvent> CollectResetEvents(NotificationSnapshot snapshot, NtfySettings config)
    {
        if (!HasAnyNotificationEnabled(config)) return [];
        var events = new List<NotificationEvent>();
        foreach (var service in ServicesForSnapshot(snapshot))
        {
            if (!service.IsAvailable || service.Usage is not { } usage) continue;
            foreach (var bucket in ResetBuckets)
            {
                if (!IsWindowTypeEnabled(config, bucket.WindowType)) continue;
                var limit = bucket.BucketId == "fiveHour" ? usage.FiveHour : usage.Weekly;
                if (limit?.ResetsAt is not { } resetsAt || !ValidResetAt(resetsAt)) continue;
                events.Add(new NotificationEvent(
                    $"{service.ServiceId}:{bucket.BucketId}", service.ServiceId, service.DisplayName,
                    bucket.BucketId, bucket.WindowType, bucket.WindowLabel, ResetsAt: resetsAt));
            }

            if (!IsWindowTypeEnabled(config, "weekly")) continue;
            var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var scoped in usage.WeeklyScoped)
            {
                if (scoped.ResetsAt is not { } resetsAt || !ValidResetAt(resetsAt)) continue;
                string scopeKey;
                if (!string.IsNullOrEmpty(scoped.Id))
                {
                    scopeKey = scoped.Id;
                }
                else
                {
                    labelCounts.TryGetValue(scoped.Label, out var count);
                    labelCounts[scoped.Label] = count + 1;
                    scopeKey = count == 0 ? scoped.Label : $"{scoped.Label}#{count}";
                }
                events.Add(new NotificationEvent(
                    $"{service.ServiceId}:weeklyScoped:{scopeKey}", service.ServiceId, service.DisplayName,
                    $"weeklyScoped:{scopeKey}", "weekly", $"週次 ({scoped.Label})", ResetsAt: resetsAt));
            }
        }

        return events.OrderBy(notificationEvent => notificationEvent.ResetsAt)
            .ThenBy(notificationEvent => notificationEvent.Key, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<NotificationEvent> CollectResetCreditsExpiryEvents(
        NotificationSnapshot snapshot,
        NtfySettings config,
        long now)
    {
        if (!config.NotifyResetCreditsExpiry) return [];
        return snapshot.CodexAccounts
            .Where(account => account.IsAvailable && account.Usage is not null)
            .Select(account => (Account: account, Credits: account.Usage!.ResetCredits))
            .Where(item => item.Credits is { AvailableCount: > 0 } && double.IsFinite(item.Credits.AvailableCount))
            .Where(item => item.Credits!.NextExpiresAt is { } expiry && ValidResetAt(expiry) && expiry > now)
            .Select(item =>
            {
                var expiresAt = item.Credits!.NextExpiresAt!.Value;
                var serviceId = $"codex:{item.Account.ServiceId}";
                return new NotificationEvent(
                    $"{serviceId}:resetCredits:{NumberText(expiresAt)}", serviceId, item.Account.DisplayName,
                    "resetCredits", "", "", Kind: NotificationKind.ResetCreditsExpiry,
                    AvailableCount: item.Credits.AvailableCount, ExpiresAt: expiresAt,
                    DueAt: expiresAt - ResetCreditWarningMs);
            })
            .OrderBy(notificationEvent => notificationEvent.ExpiresAt)
            .ThenBy(notificationEvent => notificationEvent.Key, StringComparer.Ordinal).ToArray();
    }

    internal static NtfyMessage BuildResetMessage(NotificationEvent notificationEvent)
    {
        return new NtfyMessage(
            "Agent Limit Checker",
            $"{notificationEvent.ServiceLabel} の{notificationEvent.WindowLabel}リセット時刻です。\n{FormatResetTime(notificationEvent.ResetsAt!.Value)}",
            "default",
            "hourglass");
    }

    internal static NtfyMessage BuildResetCreditsExpiryMessage(NotificationEvent notificationEvent)
    {
        return new NtfyMessage(
            "Agent Limit Checker",
            $"{notificationEvent.ServiceLabel} のリセット権の有効期限が5時間以内に到来します。\n残り: {NumberText(notificationEvent.AvailableCount!.Value)} 回\n最も早い期限: {FormatResetTime(notificationEvent.ExpiresAt!.Value)}",
            "default",
            "hourglass");
    }

    private async Task FireAsync(string key, PendingEvent expected)
    {
        NotificationEvent notificationEvent;
        int attempts;
        NtfySettings config;
        InFlightEvent flight;
        lock (gate)
        {
            if (disposed || !timers.TryGetValue(key, out var current) || !ReferenceEquals(current, expected)) return;
            timers.Remove(key);
            current.Timer?.Dispose();
            notificationEvent = current.Event;
            attempts = current.Attempts;
            var now = nowMilliseconds();
            var dueAt = notificationEvent.DueAt ?? notificationEvent.ResetsAt!.Value;
            if (dueAt > now)
            {
                Schedule(notificationEvent, attempts);
                return;
            }

            if (notificationEvent.Kind == NotificationKind.ResetCreditsExpiry && now >= notificationEvent.ExpiresAt)
            {
                sentEvents[key] = notificationEvent.ExpiresAt!.Value;
                return;
            }

            if (notificationEvent.Kind != NotificationKind.ResetCreditsExpiry &&
                now > notificationEvent.ResetsAt!.Value + DueGraceMs)
            {
                sentEvents[key] = notificationEvent.ResetsAt.Value;
                logWarn("[ntfy] skipped stale reset notification");
                return;
            }

            config = getSettings().Ntfy;
            if (!IsEventEnabled(config, notificationEvent)) return;
            if (!HasNtfyTopic(config))
            {
                logWarn("[ntfy] reset notification skipped; topic URL missing");
                return;
            }

            var identity = EventIdentity(notificationEvent);
            if (inFlight.ContainsKey(identity)) return;
            flight = new InFlightEvent(notificationEvent);
            inFlight[identity] = flight;
        }

        // inFlight は gate の外で読まない。別の通知の発火と送信の完了が並行して辞書を書き換えるため。
        var identityForSend = EventIdentity(notificationEvent);
        try
        {
            var message = notificationEvent.Kind == NotificationKind.ResetCreditsExpiry
                ? BuildResetCreditsExpiryMessage(notificationEvent)
                : BuildResetMessage(notificationEvent);
            await sendMessage(config, message, CancellationToken.None).ConfigureAwait(false);
            lock (gate)
            {
                sentEvents[key] = notificationEvent.Kind == NotificationKind.ResetCreditsExpiry
                    ? notificationEvent.ExpiresAt!.Value
                    : notificationEvent.ResetsAt!.Value;
                logInfo("[ntfy] reset notification sent");
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                logError("[ntfy] reset notification failed");
                if (error is NtfyException { Retryable: true } && attempts < MaxRetryAttempts)
                {
                    var retryAt = nowMilliseconds() + RetryDelayMs;
                    if (desiredEvents.TryGetValue(key, out var currentEvent) &&
                        EventIdentity(currentEvent) == identityForSend &&
                        IsEventEnabled(getSettings().Ntfy, currentEvent) &&
                        CanRetryEvent(currentEvent, retryAt))
                    {
                        Schedule(currentEvent, attempts + 1, RetryDelayMs);
                    }
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (inFlight.TryGetValue(identityForSend, out var current) && ReferenceEquals(current, flight))
                    inFlight.Remove(identityForSend);
            }
        }
    }

    private void Schedule(NotificationEvent notificationEvent, int attempts, int? delayOverride = null)
    {
        var dueAt = notificationEvent.DueAt ?? notificationEvent.ResetsAt!.Value;
        var dueDelay = Math.Max(0, dueAt - nowMilliseconds());
        var delay = delayOverride ?? (int)Math.Min(dueDelay, MaxTimerDelayMs);
        var scheduled = new PendingEvent(notificationEvent, attempts);
        timers[notificationEvent.Key] = scheduled;
        scheduled.Timer = setTimer(() => _ = FireAsync(notificationEvent.Key, scheduled), Math.Max(0, delay));
    }

    private void Cancel(string key)
    {
        if (!timers.Remove(key, out var existing)) return;
        existing.Timer?.Dispose();
    }

    private static IEnumerable<NotificationServiceSnapshot> ServicesForSnapshot(NotificationSnapshot snapshot)
    {
        if (snapshot.Claude is { } claude)
            yield return claude with { ServiceId = "claude", DisplayName = "Claude Code" };
        foreach (var account in snapshot.CodexAccounts)
            yield return account with { ServiceId = $"codex:{account.ServiceId}" };
    }

    private static bool IsWindowTypeEnabled(NtfySettings config, string windowType) => windowType switch
    {
        "fiveHour" => config.NotifyFiveHour,
        "weekly" => config.NotifyWeekly,
        _ => false,
    };

    private static bool HasAnyNotificationEnabled(NtfySettings config) =>
        config.NotifyFiveHour || config.NotifyWeekly || config.NotifyResetCreditsExpiry;

    private static bool HasNtfyTopic(NtfySettings config) => NormalizeTopicUrl(config.TopicUrl).Length > 0;

    private static bool ValidResetAt(double value) => double.IsFinite(value) && value > 0;

    private static bool IsEventEnabled(NtfySettings config, NotificationEvent notificationEvent) =>
        notificationEvent.Kind == NotificationKind.ResetCreditsExpiry
            ? config.NotifyResetCreditsExpiry
            : IsWindowTypeEnabled(config, notificationEvent.WindowType);

    private static bool CanRetryEvent(NotificationEvent notificationEvent, long retryAt) =>
        notificationEvent.Kind == NotificationKind.ResetCreditsExpiry
            ? retryAt < notificationEvent.ExpiresAt
            : retryAt <= notificationEvent.ResetsAt + DueGraceMs;

    private static string EventIdentity(NotificationEvent notificationEvent)
    {
        var timestamp = notificationEvent.Kind == NotificationKind.ResetCreditsExpiry
            ? notificationEvent.ExpiresAt!.Value
            : notificationEvent.ResetsAt!.Value;
        return $"{notificationEvent.Key}:{(notificationEvent.Kind == NotificationKind.ResetCreditsExpiry ? "resetCreditsExpiry" : "reset")}:{NumberText(timestamp)}";
    }

    private static string FormatResetTime(double timestamp)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).ToLocalTime()
                .ToString("yyyy/MM/dd HH:mm", CultureInfo.GetCultureInfo("ja-JP"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Invalid Date";
        }
    }

    private static string NumberText(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    private static IDisposable StartTimer(Action callback, int delayMilliseconds) =>
        new Timer(static state => ((Action)state!).Invoke(), callback, delayMilliseconds, Timeout.Infinite);

    internal sealed record NotificationEvent(
        string Key,
        string ServiceId,
        string ServiceLabel,
        string BucketId,
        string WindowType,
        string WindowLabel,
        double? ResetsAt = null,
        NotificationKind Kind = NotificationKind.Reset,
        double? AvailableCount = null,
        double? ExpiresAt = null,
        double? DueAt = null);

    internal enum NotificationKind
    {
        Reset,
        ResetCreditsExpiry,
    }

    private sealed class PendingEvent(NotificationEvent notificationEvent, int attempts)
    {
        public NotificationEvent Event { get; set; } = notificationEvent;
        public int Attempts { get; } = attempts;
        public IDisposable? Timer { get; set; }
    }

    private sealed class InFlightEvent(NotificationEvent notificationEvent)
    {
        public NotificationEvent Event { get; set; } = notificationEvent;
    }
}
