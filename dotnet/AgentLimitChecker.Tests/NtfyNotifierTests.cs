using System.Net;
using System.Text;
using AgentLimitChecker.Core.Notifications;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Tests;

public sealed class NtfyNotifierTests
{
    private const string CodexDefaultId = "/home/me/.codex";
    private const string CodexReviewId = "/home/me/.codex-review";
    private const long SampleNow = 1_800_000_000_000;

    [Fact]
    public void CollectResetEvents_RespectsFiveHourAndWeeklyOptInSettings()
    {
        var snapshot = MakeSnapshot(
            Usage(fiveHour: Limit(SampleNow + 60_000), weekly: Limit(SampleNow + 7 * 86_400_000),
                weeklyScoped: [Scoped(null, "Fable", SampleNow + 6 * 86_400_000)]),
            [Codex(CodexDefaultId, "Codex", Usage(weekly: Limit(SampleNow + 5 * 86_400_000)))]);

        var events = NtfyResetNotifier.CollectResetEvents(snapshot, new NtfySettings { NotifyWeekly = true });

        Assert.Equal(new[]
        {
            $"codex:{CodexDefaultId}:weekly",
            "claude:weeklyScoped:Fable",
            "claude:weekly",
        }, events.Select(item => item.Key));
    }

    [Fact]
    public void CollectResetEvents_KeepsTwoCodexAccountsApartAndLabelsThem()
    {
        var snapshot = MakeSnapshot(null,
        [
            Codex(CodexDefaultId, "Codex (.codex)", Usage(fiveHour: Limit(SampleNow + 60_000))),
            Codex(CodexReviewId, "Codex (.codex-review)", Usage(fiveHour: Limit(SampleNow + 120_000))),
        ]);

        var events = NtfyResetNotifier.CollectResetEvents(snapshot, new NtfySettings { NotifyFiveHour = true });

        Assert.Equal(new[] { $"codex:{CodexDefaultId}:fiveHour", $"codex:{CodexReviewId}:fiveHour" }, events.Select(item => item.Key));
        Assert.Equal(new[] { "Codex (.codex)", "Codex (.codex-review)" }, events.Select(item => item.ServiceLabel));
    }

    [Fact]
    public void CollectResetEvents_KeepsPlainCodexLabelForSingleAccount()
    {
        var snapshot = SampleSnapshot(SampleNow);
        var events = NtfyResetNotifier.CollectResetEvents(snapshot, new NtfySettings { NotifyFiveHour = true });
        var codex = Assert.Single(events, item => item.ServiceId.StartsWith("codex:", StringComparison.Ordinal));

        Assert.Equal("Codex", codex.ServiceLabel);
        Assert.StartsWith("Codex の5時間リセット時刻です。", NtfyResetNotifier.BuildResetMessage(codex).Message);
    }

    [Fact]
    public void CollectResetEvents_UsesDisplayNameCarriedByTheSnapshot()
    {
        var snapshot = MakeSnapshot(null, [Codex(CodexDefaultId, "Codex Sub", Usage(fiveHour: Limit(SampleNow + 1000)))]);
        var events = NtfyResetNotifier.CollectResetEvents(snapshot, new NtfySettings { NotifyFiveHour = true });
        var codex = Assert.Single(events);

        Assert.Equal("Codex Sub", codex.ServiceLabel);
        Assert.StartsWith("Codex Sub の5時間リセット時刻です。", NtfyResetNotifier.BuildResetMessage(codex).Message);
    }

    [Fact]
    public void CollectResetEvents_KeysScopedWeeklyEventsByStableIdAndDisambiguatesRepeatedLabels()
    {
        var snapshot = MakeSnapshot(Usage(weeklyScoped:
        [
            Scoped("claude-fable-5", "Fable", SampleNow + 6 * 86_400_000),
            Scoped(null, "スコープ", SampleNow + 6 * 86_400_000),
            Scoped(null, "スコープ", SampleNow + 5 * 86_400_000),
        ]));

        var keys = NtfyResetNotifier.CollectResetEvents(snapshot, new NtfySettings { NotifyWeekly = true })
            .Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "claude:weeklyScoped:claude-fable-5",
            "claude:weeklyScoped:スコープ",
            "claude:weeklyScoped:スコープ#1",
        }, keys);
    }

    [Fact]
    public void NormalizeTopicUrl_AcceptsTopicUrlsAndRejectsMissingTopics()
    {
        Assert.Equal("https://ntfy.sh/agent_limit_checker", NtfyResetNotifier.NormalizeTopicUrl("https://ntfy.sh/agent_limit_checker"));
        Assert.Equal("https://ntfy.sh/agent_limit_checker", NtfyResetNotifier.NormalizeTopicUrl("https://ntfy.sh/agent_limit_checker/"));
        Assert.Equal("", NtfyResetNotifier.NormalizeTopicUrl("https://ntfy.sh/"));
        Assert.Equal("", NtfyResetNotifier.NormalizeTopicUrl("not a url"));
    }

    [Fact]
    public void BuildResetMessage_IncludesServiceResetWindowAndNtfyMetadata()
    {
        var resetsAt = DateTimeOffset.Parse("2026-06-12T18:30:00+09:00").ToUnixTimeMilliseconds();
        var message = NtfyResetNotifier.BuildResetMessage(new NtfyResetNotifier.NotificationEvent(
            "key", "codex:/home/me/.codex", "Codex", "fiveHour", "fiveHour", "5時間", resetsAt));

        Assert.Equal("Agent Limit Checker", message.Title);
        Assert.Matches("Codex の5時間リセット時刻です。\\n\\d{4}/\\d{2}/\\d{2} ", message.Message);
        Assert.Equal("default", message.Priority);
        Assert.Equal("hourglass", message.Tags);
    }

    [Fact]
    public async Task SendNtfyMessage_PostsTextWithNtfyHeadersToTheTopicUrl()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"msg-1\",\"topic\":\"agent_limit_checker\"}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var sender = new NtfyMessageSender(httpClient);

        var result = await sender.SendAsync(
            new NtfySettings { TopicUrl = "https://ntfy.sh/agent_limit_checker", AccessToken = "tk_secret" },
            new NtfyMessage("Title", "Body", "default", "hourglass"));

        Assert.Equal(new NtfySendResult("msg-1", "agent_limit_checker"), result);
        Assert.Equal("https://ntfy.sh/agent_limit_checker", handler.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("text/plain; charset=utf-8", handler.ContentType);
        Assert.Equal("Title", handler.Headers["Title"]);
        Assert.Equal("default", handler.Headers["Priority"]);
        Assert.Equal("hourglass", handler.Headers["Tags"]);
        Assert.Equal("Bearer tk_secret", handler.Authorization);
        Assert.Equal("Body", handler.Body);
        Assert.Equal("agent-limit-checker/1.0", handler.Headers["User-Agent"]);
    }

    [Fact]
    public async Task SendNtfyMessage_MarksRateLimitResponsesRetryableWithoutExposingResponseBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{\"error\":\"private server response\",\"id\":\"msg-2\"}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var sender = new NtfyMessageSender(httpClient);

        var error = await Assert.ThrowsAsync<NtfyException>(() => sender.SendAsync(
            new NtfySettings { TopicUrl = "https://ntfy.sh/agent_limit_checker" },
            new NtfyMessage("Title", "Body")));

        Assert.Equal("ntfy_api_error", error.Code);
        Assert.Equal(429, error.Status);
        Assert.True(error.Retryable);
        Assert.Equal("msg-2", error.RequestId);
        Assert.DoesNotContain("private server response", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NtfyResetNotifier_SendsOneNotificationPerResetTimestamp()
    {
        var clock = new FakeClock(SampleNow);
        var sent = new List<NtfyMessage>();
        var notifier = MakeNotifier(clock, () => Settings(new NtfySettings
        {
            TopicUrl = "https://ntfy.sh/agent_limit_checker", NotifyFiveHour = true,
        }), async (_, message, _) => { sent.Add(message); return new NtfySendResult("msg-3", null); });
        var snapshot = MakeSnapshot(Usage(fiveHour: Limit(SampleNow + 1000)));

        notifier.Update(snapshot);
        Assert.Single(clock.ActiveTimers);
        Assert.Equal(1000, clock.ActiveTimers[0].Delay);
        clock.SetNow(SampleNow + 1000);
        clock.Run(clock.ActiveTimers[0]);
        await FlushNotifier();

        Assert.Single(sent);
        Assert.Equal("Agent Limit Checker", sent[0].Title);
        notifier.Update(snapshot);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task ResetCreditExpiryWarnings_ScheduleFiveHoursBeforeExpiryPerAccount()
    {
        var clock = new FakeClock(SampleNow);
        var sent = new List<NtfyMessage>();
        var expiry = SampleNow + 8 * 60 * 60 * 1000;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), async (_, message, _) =>
        {
            sent.Add(message);
            return new NtfySendResult($"msg-{sent.Count}", null);
        });
        var snapshot = MakeSnapshot(null,
        [
            CreditAccount(CodexDefaultId, "Codex Main", 2, expiry),
            CreditAccount(CodexReviewId, "Codex Review", 1, expiry),
        ]);

        notifier.Update(snapshot);
        Assert.Equal(new[] { 3 * 60 * 60 * 1000, 3 * 60 * 60 * 1000 }, clock.ActiveTimers.Select(timer => timer.Delay));
        notifier.Update(snapshot);
        Assert.Equal(2, clock.ActiveTimers.Count);
        clock.SetNow(SampleNow + 3 * 60 * 60 * 1000);
        foreach (var timer in clock.ActiveTimers.ToArray()) clock.Run(timer);
        await FlushNotifier();

        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, message => message.Message.Contains("Codex Main", StringComparison.Ordinal));
        Assert.Contains(sent, message => message.Message.Contains("Codex Review", StringComparison.Ordinal));
        Assert.Contains(sent, message => message.Message.Contains("残り: 2 回", StringComparison.Ordinal));
        notifier.Update(snapshot);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task ResetCreditExpiryWarning_FiresImmediatelyWhenFiveHourLeadTimeHasPassed()
    {
        var clock = new FakeClock(SampleNow);
        var sent = new List<NtfyMessage>();
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), async (_, message, _) =>
        {
            sent.Add(message);
            return new NtfySendResult("msg-1", null);
        });
        notifier.Update(MakeSnapshot(null,
        [
            CreditAccount(CodexDefaultId, "Codex Main", 2, SampleNow + 2 * 60 * 60 * 1000),
            CreditAccount("/home/me/.codex-no-credits", "No Credits", 0, SampleNow + 60 * 60 * 1000),
            CreditAccount("/home/me/.codex-now", "Expired", 1, SampleNow),
            CreditAccount("/home/me/.codex-past", "Past", 1, SampleNow - 1000),
            CreditAccount("/home/me/.codex-unknown", "Unknown", 1, null),
        ]));

        Assert.Single(clock.ActiveTimers);
        Assert.Equal(0, clock.ActiveTimers[0].Delay);
        clock.Run(clock.ActiveTimers[0]);
        await FlushNotifier();
        Assert.Single(sent);
    }

    [Fact]
    public async Task ResetCreditExpiryWarning_RechecksCappedLongTimerBeforeDueTime()
    {
        var clock = new FakeClock(SampleNow);
        var sends = 0;
        var maxTimerDelay = int.MaxValue;
        var expiry = SampleNow + 5 * 60 * 60 * 1000L + maxTimerDelay + 5000;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), (_, _, _) =>
        {
            sends++;
            return Task.FromResult(new NtfySendResult("msg-1", null));
        });
        notifier.Update(MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Codex", 1, expiry)]));
        var cappedTimer = Assert.Single(clock.ActiveTimers);
        Assert.Equal(maxTimerDelay, cappedTimer.Delay);
        clock.SetNow(SampleNow + maxTimerDelay);
        clock.Run(cappedTimer);
        await FlushNotifier();

        Assert.Equal(0, sends);
        Assert.Equal(5000, Assert.Single(clock.ActiveTimers).Delay);
        clock.SetNow(SampleNow + maxTimerDelay + 5000);
        clock.Run(clock.ActiveTimers[0]);
        await FlushNotifier();
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task SamePendingResetCreditExpiry_RefreshesCountAndAccountName()
    {
        var clock = new FakeClock(SampleNow);
        var sent = new List<NtfyMessage>();
        var expiry = SampleNow + 6 * 60 * 60 * 1000;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), async (_, message, _) =>
        {
            sent.Add(message);
            return new NtfySendResult("msg-1", null);
        });
        notifier.Update(MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Old Name", 1, expiry)]));
        var originalTimer = Assert.Single(clock.ActiveTimers);
        notifier.Update(MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Updated Name", 3, expiry)]));

        Assert.Same(originalTimer, Assert.Single(clock.ActiveTimers));
        clock.SetNow(SampleNow + 60 * 60 * 1000);
        clock.Run(originalTimer);
        await FlushNotifier();
        Assert.Single(sent);
        Assert.Contains("Updated Name", sent[0].Message, StringComparison.Ordinal);
        Assert.Contains("3 回", sent[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetCreditExpiryChangesRemovalDepletionAndOptOut_CancelPendingTimers()
    {
        var clock = new FakeClock(SampleNow);
        var isEnabled = true;
        var notifier = MakeNotifier(clock, () => Settings(new NtfySettings { NotifyResetCreditsExpiry = isEnabled }),
            (_, _, _) => Task.FromResult(new NtfySendResult("msg-1", null)));
        NotificationSnapshot For(double? expiresAt, double count = 1) => MakeSnapshot(null,
            expiresAt is null ? [] : [CreditAccount(CodexDefaultId, "Codex", count, expiresAt)]);
        var firstExpiry = SampleNow + 8 * 60 * 60 * 1000;
        var changedExpiry = SampleNow + 9 * 60 * 60 * 1000;

        notifier.Update(For(firstExpiry));
        var firstTimer = Assert.Single(clock.ActiveTimers);
        notifier.Update(For(changedExpiry));
        Assert.True(firstTimer.Cleared);
        var changedTimer = Assert.Single(clock.ActiveTimers);
        notifier.Update(For(null));
        Assert.True(changedTimer.Cleared);
        Assert.Empty(clock.ActiveTimers);

        notifier.Update(For(firstExpiry));
        var usedTimer = Assert.Single(clock.ActiveTimers);
        notifier.Update(For(firstExpiry, 0));
        Assert.True(usedTimer.Cleared);
        Assert.Empty(clock.ActiveTimers);

        notifier.Update(For(firstExpiry));
        var optedOutTimer = Assert.Single(clock.ActiveTimers);
        isEnabled = false;
        notifier.Update(For(firstExpiry));
        Assert.True(optedOutTimer.Cleared);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task ResetCreditExpiryWarning_StopsAtExactExpiryTime()
    {
        var clock = new FakeClock(SampleNow);
        var sends = 0;
        var expiry = SampleNow + 60 * 60 * 1000;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), (_, _, _) =>
        {
            sends++;
            return Task.FromResult(new NtfySendResult("msg-1", null));
        });
        notifier.Update(MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Codex", 1, expiry)]));
        var timer = Assert.Single(clock.ActiveTimers);
        clock.SetNow(expiry);
        clock.Run(timer);
        await FlushNotifier();

        Assert.Equal(0, sends);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task ResetCreditExpiryRetry_IsNotScheduledWhenDelayReachesExpiry()
    {
        var clock = new FakeClock(SampleNow);
        var sends = 0;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), (_, _, _) =>
        {
            sends++;
            return Task.FromException<NtfySendResult>(new NtfyException("temporary failure", retryable: true));
        });
        notifier.Update(MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Codex", 1, SampleNow + 5000)]));
        clock.Run(Assert.Single(clock.ActiveTimers));
        await FlushNotifier();

        Assert.Equal(1, sends);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task SameInflightResetCreditExpiry_DoesNotDuplicateAndRemovalPreventsRetry()
    {
        var clock = new FakeClock(SampleNow);
        var snapshot = MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Codex", 1, SampleNow + 2 * 60 * 60 * 1000)]);
        var rejectSend = new TaskCompletionSource<NtfySendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), (_, _, _) =>
        {
            sends++;
            return rejectSend.Task;
        });

        notifier.Update(snapshot);
        clock.Run(Assert.Single(clock.ActiveTimers));
        Assert.Equal(1, sends);
        notifier.Update(snapshot);
        Assert.Empty(clock.ActiveTimers);
        notifier.Update(MakeSnapshot(null));
        rejectSend.SetException(new NtfyException("temporary failure", retryable: true));
        await FlushNotifier();

        Assert.Equal(1, sends);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task ResetCreditExpiryRetriesAfterFiveSecondsAndDeduplicatesAfterSuccess()
    {
        var clock = new FakeClock(SampleNow);
        var sends = 0;
        var notifier = MakeNotifier(clock, () => Settings(CreditSettings()), (_, _, _) =>
        {
            sends++;
            return sends == 1
                ? Task.FromException<NtfySendResult>(new NtfyException("temporary failure", retryable: true))
                : Task.FromResult(new NtfySendResult("msg-2", null));
        });
        var snapshot = MakeSnapshot(null, [CreditAccount(CodexDefaultId, "Codex", 1, SampleNow + 60 * 60 * 1000)]);

        notifier.Update(snapshot);
        clock.Run(Assert.Single(clock.ActiveTimers));
        await FlushNotifier();
        Assert.Equal(1, sends);
        Assert.Equal(5000, Assert.Single(clock.ActiveTimers).Delay);
        clock.SetNow(SampleNow + 5000);
        clock.Run(Assert.Single(clock.ActiveTimers));
        await FlushNotifier();
        Assert.Equal(2, sends);
        notifier.Update(snapshot);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task OrdinaryResetRetry_SucceedsAtThirtyMinuteGraceBoundary()
    {
        var clock = new FakeClock(SampleNow);
        var sends = 0;
        var notifier = MakeNotifier(clock, () => Settings(new NtfySettings
        {
            TopicUrl = "https://ntfy.sh/agent_limit_checker", NotifyFiveHour = true,
        }), (_, _, _) =>
        {
            sends++;
            return sends == 1
                ? Task.FromException<NtfySendResult>(new NtfyException("temporary failure", retryable: true))
                : Task.FromResult(new NtfySendResult("msg-2", null));
        });
        var snapshot = MakeSnapshot(Usage(fiveHour: Limit(SampleNow)));

        notifier.Update(snapshot);
        clock.SetNow(SampleNow + 30 * 60 * 1000 - 5000);
        clock.Run(Assert.Single(clock.ActiveTimers));
        await FlushNotifier();
        Assert.Equal(1, sends);
        Assert.Equal(5000, Assert.Single(clock.ActiveTimers).Delay);
        clock.SetNow(SampleNow + 30 * 60 * 1000);
        clock.Run(Assert.Single(clock.ActiveTimers));
        await FlushNotifier();
        Assert.Equal(2, sends);
        notifier.Update(snapshot);
        Assert.Empty(clock.ActiveTimers);
    }

    [Fact]
    public async Task NtfyResetNotifier_DoesNotLogExceptionDetailsFromSender()
    {
        var clock = new FakeClock(SampleNow);
        var log = new List<string>();
        var settings = Settings(new NtfySettings { TopicUrl = "https://ntfy.sh/agent_limit_checker", NotifyFiveHour = true,
            AccessToken = "secret-token" });
        var notifier = new NtfyResetNotifier(() => settings,
            (_, _, _) => Task.FromException<NtfySendResult>(new NtfyException("server response with secret-token", 500, true)),
            clock.Now, clock.SetTimer, log.Add, log.Add, log.Add);
        notifier.Update(MakeSnapshot(Usage(fiveHour: Limit(SampleNow))));
        clock.Run(Assert.Single(clock.ActiveTimers));
        await FlushNotifier();

        Assert.DoesNotContain(log, line => line.Contains("secret-token", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("server response", StringComparison.Ordinal));
    }

    private static NtfyResetNotifier MakeNotifier(
        FakeClock clock,
        Func<AppSettings> getSettings,
        Func<NtfySettings, NtfyMessage, CancellationToken, Task<NtfySendResult>> sendMessage) =>
        new(getSettings, sendMessage, clock.Now, clock.SetTimer, _ => { }, _ => { }, _ => { });

    private static AppSettings Settings(NtfySettings ntfy) => new() { Ntfy = ntfy };

    private static NtfySettings CreditSettings() => new()
    {
        TopicUrl = "https://ntfy.sh/agent_limit_checker",
        NotifyResetCreditsExpiry = true,
    };

    private static NotificationSnapshot SampleSnapshot(long now) => MakeSnapshot(
        Usage(Limit(now + 60_000), Limit(now + 7 * 86_400_000), [Scoped(null, "Fable", now + 6 * 86_400_000)]),
        [Codex(CodexDefaultId, "Codex", Usage(Limit(now + 120_000), Limit(now + 5 * 86_400_000)))]);

    private static NotificationSnapshot MakeSnapshot(
        UsageSnapshot? claude = null,
        IReadOnlyList<NotificationServiceSnapshot>? accounts = null) => new(
            claude is null ? null : new NotificationServiceSnapshot("claude", "Claude Code", true, claude),
            accounts ?? []);

    private static NotificationServiceSnapshot Codex(string id, string name, UsageSnapshot usage) =>
        new(id, name, true, usage);

    private static NotificationServiceSnapshot CreditAccount(string id, string name, double count, double? expiry) =>
        Codex(id, name, Usage(resetCredits: new ResetCreditBalance(count, expiry)));

    private static UsageSnapshot Usage(
        RateLimit? fiveHour = null,
        RateLimit? weekly = null,
        IReadOnlyList<WeeklyScopedLimit>? weeklyScoped = null,
        ResetCreditBalance? resetCredits = null) => new(
            fiveHour, weekly, weeklyScoped ?? [], null, resetCredits, null);

    private static RateLimit Limit(double? resetAt) => new(0.2, resetAt);

    private static WeeklyScopedLimit Scoped(string? id, string label, double resetAt) => new(id, label, 0.2, resetAt);

    private static async Task FlushNotifier()
    {
        await Task.Yield();
        await Task.Yield();
        await Task.Yield();
    }

    private sealed class FakeClock(long initialNow)
    {
        private long currentNow = initialNow;
        private readonly List<FakeTimer> timers = [];
        public long Now() => currentNow;
        public void SetNow(long value) => currentNow = value;
        public IDisposable SetTimer(Action callback, int delay)
        {
            var timer = new FakeTimer(callback, delay);
            timers.Add(timer);
            return timer;
        }
        public IReadOnlyList<FakeTimer> ActiveTimers => timers.Where(timer => !timer.Cleared && !timer.Fired).ToArray();
        public void Run(FakeTimer timer) => timer.Run();
    }

    private sealed class FakeTimer(Action callback, int delay) : IDisposable
    {
        public int Delay { get; } = delay;
        public bool Cleared { get; private set; }
        public bool Fired { get; private set; }
        public void Run()
        {
            if (Cleared || Fired) return;
            Fired = true;
            callback();
        }
        public void Dispose() => Cleared = true;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            foreach (var header in request.Headers) Headers[header.Key] = string.Join(",", header.Value);
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
