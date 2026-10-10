using System.Net;
using System.Text.Json.Nodes;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Claude;

namespace AgentLimitChecker.Tests;

public sealed class ClaudeProviderTests
{
    private static JsonNode? J(string text) => JsonNode.Parse(text);
    private static JsonObject O(string text) => (JsonObject)J(text)!;
    private static double? N(JsonNode? node) => ClaudeValues.Number(node);
    private static void Same(JsonNode? expected, JsonNode? actual) => Assert.True(JsonNode.DeepEquals(expected, actual));

    [Fact] public void NormalizeExpiresAt_NormalizesOAuthTimestamps()
    {
        Assert.Equal(1779632820000, ClaudeValues.NormalizeExpiresAt(J("1779632820")));
        Assert.Equal(1779632820450, ClaudeValues.NormalizeExpiresAt(J("1779632820450")));
        Assert.Null(ClaudeValues.NormalizeExpiresAt(J("\"bad\"")));
    }
    [Fact] public void ShouldRefresh_OnlyAfterActualExpiry()
    {
        const double now = 1779600000000;
        foreach (var delta in new[] { 0, -1, 30000, 120000 })
            Assert.Equal(delta <= 0, ClaudeValues.ShouldRefresh(new JsonObject { ["expiresAt"] = now + delta }, now));
        Assert.False(ClaudeValues.ShouldRefresh(new JsonObject(), now));
    }
    [Fact] public void ParseBucket_KeepsZeroWithoutReset() => Assert.Equal(new RateLimit(0, null), ClaudeValues.ParseBucket(J("""{"utilization":0,"resets_at":null}""")));
    [Fact] public void ParseBucket_ParsesMicrosecondReset() => Assert.Equal(new RateLimit(.03, 1779636600338), ClaudeValues.ParseBucket(J("""{"utilization":3,"resets_at":"2026-05-24T15:30:00.338620+00:00"}""")));
    [Fact] public void ParseBucket_HidesMissingUtilization()
    {
        foreach (var text in new[] { "null", "{}", """{"utilization":null,"resets_at":"2026-01-01T00:00:00Z"}""" }) Assert.Null(ClaudeValues.ParseBucket(J(text)));
    }
    [Fact] public void ParseCloudCredit_ReadsCloudSessionBucket() => Assert.Equal(new CloudCreditBalance(250, 1.771358, 248.228642, 1.771358 / 250,
        DateTimeOffset.Parse("2026-11-05T07:59:00+00:00").ToUnixTimeMilliseconds(), null), ClaudeValues.ParseCloudCredit(J("""{"iguana_necktie":{"utilization":0.7085432,"resets_at":"2026-11-05T07:59:00+00:00","limit_dollars":250,"used_dollars":1.771358,"remaining_dollars":248.228642,"locked_reason":null}}""")));
    [Fact] public void ParseCloudCredit_HidesMissingOrInvalidLimit()
    {
        foreach (var text in new[] { "null", "{}", """{"iguana_necktie":null}""", """{"iguana_necktie":{"limit_dollars":null}}""" }) Assert.Null(ClaudeValues.ParseCloudCredit(J(text)));
    }
    [Fact] public void ParseCloudCredit_DerivesUsed() => Assert.Equal(new CloudCreditBalance(100, 28, 72, .28, null, null), ClaudeValues.ParseCloudCredit(J("""{"iguana_necktie":{"limit_dollars":100,"remaining_dollars":72,"resets_at":"invalid"}}""")));
    [Fact] public void ParseCloudCredit_DerivesRemaining() => Assert.Equal(new CloudCreditBalance(100, 28, 72, .28, null, null), ClaudeValues.ParseCloudCredit(J("""{"iguana_necktie":{"limit_dollars":100,"used_dollars":28}}""")));
    [Fact] public void ParseCloudCredit_RejectsMissingBalances() => Assert.Null(ClaudeValues.ParseCloudCredit(J("""{"iguana_necktie":{"limit_dollars":100}}""")));
    [Fact] public void ParseWeeklyScoped_ReadsModelCaps()
    {
        var actual = ClaudeValues.ParseWeeklyScoped(J("""{"seven_day_sonnet":null,"limits":[{"kind":"session","group":"session","percent":49,"resets_at":"2026-07-02T11:29:59+00:00"},{"kind":"weekly_all","group":"weekly","percent":17,"resets_at":"2026-07-08T10:00:00+00:00"},{"kind":"weekly_scoped","group":"weekly","percent":32,"resets_at":"2026-07-08T09:59:59+00:00","scope":{"model":{"id":null,"display_name":"Fable"}}}]}"""));
        Assert.Equal([new WeeklyScopedLimit(null, "Fable", .32, DateTimeOffset.Parse("2026-07-08T09:59:59+00:00").ToUnixTimeMilliseconds())], actual);
    }
    [Fact] public void ParseWeeklyScoped_KeepsStableId()
    {
        var actual = Assert.Single(ClaudeValues.ParseWeeklyScoped(J("""{"limits":[{"kind":"weekly_scoped","group":"weekly","percent":32,"resets_at":"2026-07-08T09:59:59+00:00","scope":{"model":{"id":"claude-fable-5","display_name":"Fable"}}}]}""")));
        Assert.Equal("claude-fable-5", actual.Id); Assert.Equal("Fable", actual.Label);
    }
    [Fact] public void ParseWeeklyScoped_SkipsNonNumericPercent() => Assert.Empty(ClaudeValues.ParseWeeklyScoped(J("""{"limits":[{"kind":"weekly_scoped","group":"weekly","percent":null,"scope":{"model":{"display_name":"Fable"}}}]}""")));
    [Fact] public void ParseWeeklyScoped_FallsBackToLegacy()
    {
        Assert.Equal([new WeeklyScopedLimit(null, "Sonnet", .08, DateTimeOffset.Parse("2026-07-08T10:00:00+00:00").ToUnixTimeMilliseconds())], ClaudeValues.ParseWeeklyScoped(J("""{"seven_day_sonnet":{"utilization":8,"resets_at":"2026-07-08T10:00:00+00:00"}}""")));
        Assert.Empty(ClaudeValues.ParseWeeklyScoped(J("{}")));
    }
    [Fact] public void ParseWeeklyScoped_FallsBackWithoutScopedEntries()
    {
        Assert.Equal([new WeeklyScopedLimit(null, "Sonnet", .08, DateTimeOffset.Parse("2026-07-08T10:00:00+00:00").ToUnixTimeMilliseconds())], ClaudeValues.ParseWeeklyScoped(J("""{"limits":[{"kind":"session","group":"session","percent":49},{"kind":"weekly_all","group":"weekly","percent":17}],"seven_day_sonnet":{"utilization":8,"resets_at":"2026-07-08T10:00:00+00:00"}}""")));
        Assert.Empty(ClaudeValues.ParseWeeklyScoped(J("""{"limits":[]}""")));
    }
    [Fact] public void ParseCredits_ReadsSpendBalance() => Assert.Equal(new CreditBalance(25.99, "USD", false), ClaudeValues.ParseCredits(J("""{"spend":{"balance":{"amount_minor":2599,"currency":"USD","exponent":2}}}""")));
    [Fact] public void ParseCredits_HidesWithoutPositiveBalance()
    {
        foreach (var text in new[] { "null", "{}", """{"spend":{}}""", """{"spend":{"balance":null}}""", """{"spend":{"balance":{"amount_minor":0,"currency":"USD","exponent":2}}}""", """{"spend":{"balance":{"currency":"USD"}}}""", """{"spend":{"balance":{"amount_minor":-100,"currency":"USD","exponent":2}}}""", """{"spend":{"balance":5}}""", """{"spend":{"balance":"5"}}""" }) Assert.Null(ClaudeValues.ParseCredits(J(text)));
    }
    [Fact] public void ParseCredits_DefaultsCurrency() => Assert.Equal(new CreditBalance(5, "USD", false), ClaudeValues.ParseCredits(J("""{"spend":{"balance":{"amount_minor":500,"exponent":2}}}""")));
    [Fact] public void ExtractPlanLabel_ReadsTier()
    {
        foreach (var (tier, label) in new[] { ("default_claude_max_5x", "Max 5x"), ("default_claude_max_20x", "Max 20x"), ("default_claude_pro", "Pro"), ("default_claude_teams", "Team") })
            Assert.Equal(label, ClaudeValues.ExtractPlanLabel(new JsonObject { ["rateLimitTier"] = tier }));
    }
    [Fact] public void ExtractPlanLabel_FallsBackToSubscription()
    {
        Assert.Equal("Max", ClaudeValues.ExtractPlanLabel(J("""{"subscriptionType":"max"}""")));
        Assert.Equal("Pro", ClaudeValues.ExtractPlanLabel(J("""{"rateLimitTier":"something_weird","subscriptionType":"pro"}""")));
        Assert.Null(ClaudeValues.ExtractPlanLabel(J("{}"))); Assert.Null(ClaudeValues.ExtractPlanLabel(null));
    }
    [Fact] public void MergeRefreshResponse_PreservesMetadata()
    {
        var result = ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old-access","refreshToken":"old-refresh","subscriptionType":"max","rateLimitTier":"standard"}"""), J("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"scope":["profile"]}"""), 1000000);
        Assert.True(ClaudeValues.Text(result["accessToken"]) == "new-access"); Assert.True(ClaudeValues.Text(result["refreshToken"]) == "new-refresh");
        Assert.Equal(4600000, N(result["expiresAt"])); Assert.Equal("max", ClaudeValues.Text(result["subscriptionType"])); Assert.Equal("standard", ClaudeValues.Text(result["rateLimitTier"])); Same(J("[\"profile\"]"), result["scopes"]);
    }
    [Fact] public void MergeRefreshResponse_SplitsScopeString()
    {
        var result = ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old","refreshToken":"old-refresh","scopes":["user:inference"]}"""), J("""{"access_token":"new","scope":"user:inference user:profile"}"""), 1000000);
        Same(J("[\"user:inference\",\"user:profile\"]"), result["scopes"]);
    }
    [Fact] public void MergeRefreshResponse_KeepsScopesWhenOmitted()
    {
        var result = ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old","refreshToken":"old-refresh","scopes":["user:inference"]}"""), J("""{"access_token":"new"}"""), 1000000);
        Same(J("[\"user:inference\"]"), result["scopes"]);
    }
    [Fact] public void MergeRefreshResponse_RecordsRefreshTokenExpiry()
    {
        Assert.Equal(1060000, N(ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old","refreshToken":"old-refresh","refreshTokenExpiresAt":5}"""), J("""{"access_token":"new","refresh_token_expires_in":60}"""), 1000000)["refreshTokenExpiresAt"]));
        Assert.Equal(1779632820000, N(ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old","refreshToken":"old-refresh"}"""), J("""{"access_token":"new","refresh_token_expires_at":1779632820}"""), 1000000)["refreshTokenExpiresAt"]));
        Assert.Equal(42, N(ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old","refreshToken":"old-refresh","refreshTokenExpiresAt":42}"""), J("""{"access_token":"new"}"""), 1000000)["refreshTokenExpiresAt"]));
    }
    [Fact] public void MergeRefreshResponse_KeepsRefreshTokenWhenOmitted()
    {
        var result = ClaudeValues.MergeRefreshResponse(O("""{"accessToken":"old","refreshToken":"old-refresh"}"""), J("""{"access_token":"new"}"""), 1000000);
        Assert.True(ClaudeValues.Text(result["refreshToken"]) == "old-refresh");
    }
    [Fact] public void RefreshConfig_DefaultsAndOverrides()
    {
        Assert.Equal(("https://platform.claude.com/v1/oauth/token", "9d1c250a-e61b-44d9-88ed-5944d1962f5e"), ClaudeProvider.RefreshConfig(new Dictionary<string, string>()));
        Assert.Equal(("https://example.test/token", "client-x"), ClaudeProvider.RefreshConfig(new Dictionary<string, string> { ["CLAUDE_OAUTH_TOKEN_ENDPOINT"] = "https://example.test/token", ["CLAUDE_OAUTH_CLIENT_ID"] = "client-x" }));
    }
    [Fact] public void IsFatalRefreshError_SeparatesTransientFailures()
    {
        Assert.True(ClaudeProvider.IsFatalRefreshError(new ClaudeProviderException("claude_http_error", "", 400)));
        foreach (var code in new[] { "claude_unauthorized", "claude_refresh_token_missing", "claude_refresh_expired" }) Assert.True(ClaudeProvider.IsFatalRefreshError(new(code, "")));
        foreach (var code in new[] { "claude_rate_limited", "claude_network", "claude_timeout", "claude_refresh_invalid" }) Assert.False(ClaudeProvider.IsFatalRefreshError(new(code, "")));
        foreach (var status in new[] { 500, 503 }) Assert.False(ClaudeProvider.IsFatalRefreshError(new ClaudeProviderException("claude_http_error", "", status)));
        Assert.False(ClaudeProvider.IsFatalRefreshError(null));
    }
    [Fact] public void DescribeRefreshFailure_OnlyExposesKnownCodesOrStatus()
    {
        foreach (var (body, status, expected) in new[] { ("{\"error\":\"invalid_client\"}", 400, "invalid_client"), ("{\"error\":\"invalid_grant\",\"error_description\":\"refresh token expired\"}", 400, "invalid_grant"), ("{\"error\":\"weird\\nline\",\"error_description\":\"secret\"}", 400, "status 400"), ("<html>nope</html>", 401, "status 401") })
            Assert.Equal(expected, ClaudeProvider.DescribeRefreshFailure(new ClaudeProviderException("claude_http_error", "", status) { OAuthError = ClaudeProvider.ParseOAuthErrorBody(body) }));
        Assert.Null(ClaudeProvider.DescribeRefreshFailure(new("claude_refresh_expired", ""))); Assert.Null(ClaudeProvider.DescribeRefreshFailure(null));
    }

    private const double Now = 1779600000000;
    private const string UsageBody = """{"five_hour":{"utilization":12,"resets_at":"2026-07-02T11:29:59+00:00"},"seven_day":{"utilization":5,"resets_at":"2026-07-08T10:00:00+00:00"}}""";
    private static JsonObject Fixture(bool expired = false) => new()
    {
        ["accessToken"] = "old-access", ["refreshToken"] = "old-refresh", ["expiresAt"] = Now + (expired ? -1000 : 3600000),
        ["refreshTokenExpiresAt"] = Now + 30 * 86400000d, ["scopes"] = new JsonArray("user:inference"), ["subscriptionType"] = "max", ["rateLimitTier"] = "default_claude_max_5x"
    };
    private sealed class Call
    {
        internal string Url { get; init; } = "";
        internal string Method { get; init; } = "";
        internal string? Authorization { get; init; }
        internal string Body { get; init; } = "";
        internal Dictionary<string, string> Headers { get; init; } = new();
        internal bool IsUsage => Url.Contains("/oauth/usage", StringComparison.Ordinal);
    }
    private sealed class Handler(Func<Call, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Call> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = new Call { Url = request.RequestUri!.ToString(), Method = request.Method.Method, Authorization = request.Headers.Authorization?.ToString(),
                Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase) };
            Calls.Add(call);
            return respond(call);
        }
    }
    private static HttpResponseMessage Response(int status = 200, string body = UsageBody) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private sealed class Harness : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Environment.CurrentDirectory, ".cache", "claude-tests", Guid.NewGuid().ToString("N"));
        internal string FilePath => Path.Combine(DirectoryPath, ".credentials.json");
        internal Handler Transport { get; }
        internal ClaudeProvider Provider { get; }
        private readonly HttpClient client;
        internal Harness(JsonObject oauth, Func<Call, HttpResponseMessage>? respond = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            Write(new JsonObject { ["claudeAiOauth"] = oauth.DeepClone() });
            Transport = new Handler(respond ?? (_ => Response()));
            client = new HttpClient(Transport);
            Provider = new ClaudeProvider(client, () => new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = DirectoryPath }, () => Now);
        }
        internal JsonObject Read() => O(File.ReadAllText(FilePath));
        internal void Write(JsonObject raw) => File.WriteAllText(FilePath, raw.ToJsonString());
        internal JsonObject Oauth => (JsonObject)Read()["claudeAiOauth"]!;
        public void Dispose() { Provider.Dispose(); client.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    [Fact] public async Task Fetch_RefreshesExpiredTokenAndPreservesMetadata()
    {
        var oauth = Fixture(true); oauth["unknownField"] = "keep-me";
        using var h = new Harness(oauth, call => call.IsUsage ? Response() : Response(200, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":28800,"scope":"user:inference user:profile"}"""));
        var raw = h.Read(); raw["schemaVersion"] = 1; h.Write(raw);
        var usage = await h.Provider.FetchAsync(); Assert.Equal("Max 5x", usage.Plan); Assert.Equal(.12, usage.FiveHour!.Utilization);
        var post = Assert.Single(h.Transport.Calls, c => !c.IsUsage); Assert.Equal("POST", post.Method);
        var form = post.Body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("refresh_token", form["grant_type"]); Assert.True(form["refresh_token"] == "old-refresh"); Assert.Equal(ClaudeProvider.RefreshConfig(new Dictionary<string, string>()).ClientId, form["client_id"]);
        Assert.True(Assert.Single(h.Transport.Calls, c => c.IsUsage).Authorization == "Bearer new-access");
        Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "new-access"); Assert.True(ClaudeValues.Text(h.Oauth["refreshToken"]) == "new-refresh");
        Same(J("[\"user:inference\",\"user:profile\"]"), h.Oauth["scopes"]); Assert.Equal("keep-me", ClaudeValues.Text(h.Oauth["unknownField"])); Assert.Equal(1, N(h.Read()["schemaVersion"]));
    }
    [Fact] public async Task Fetch_401RetriesCliCredentialsWithoutPosting()
    {
        Harness? harness = null;
        using var h = new Harness(Fixture(), call =>
        {
            if (!call.IsUsage) return Response(200, "{}");
            if (call.Authorization == "Bearer old-access")
            {
                var next = Fixture(); next["accessToken"] = "cli-access"; next["refreshToken"] = "cli-refresh";
                harness!.Write(new JsonObject { ["claudeAiOauth"] = next }); return Response(401, """{"error":"invalid_token"}""");
            }
            return Response();
        }); harness = h;
        Assert.Equal(.12, (await h.Provider.FetchAsync()).FiveHour!.Utilization); Assert.DoesNotContain(h.Transport.Calls, c => !c.IsUsage);
        Assert.Equal(2, h.Transport.Calls.Count); Assert.True(h.Transport.Calls[1].Authorization == "Bearer cli-access"); Assert.True(ClaudeValues.Text(h.Oauth["refreshToken"]) == "cli-refresh");
    }
    [Fact] public async Task Fetch_401RefreshesWhenFileUnchanged()
    {
        using var h = new Harness(Fixture(), call => !call.IsUsage ? Response(200, """{"access_token":"refreshed-access","expires_in":28800}""") : call.Authorization == "Bearer old-access" ? Response(401, """{"error":"invalid_token"}""") : Response());
        Assert.Equal(.12, (await h.Provider.FetchAsync()).FiveHour!.Utilization); Assert.Single(h.Transport.Calls, c => !c.IsUsage);
        var calls = h.Transport.Calls.Where(c => c.IsUsage).ToArray(); Assert.Equal(2, calls.Length); Assert.True(calls[1].Authorization == "Bearer refreshed-access");
        Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "refreshed-access"); Assert.True(ClaudeValues.Text(h.Oauth["refreshToken"]) == "old-refresh");
    }
    [Fact] public async Task Fetch_InvalidGrantAsksForLoginWithCause()
    {
        using var h = new Harness(Fixture(true), call => call.IsUsage ? Response() : Response(400, """{"error":"invalid_grant","error_description":"refresh token expired"}"""));
        var error = await Assert.ThrowsAsync<ProviderException>(() => h.Provider.FetchAsync()); Assert.Equal("claude_unauthorized", error.Code); Assert.Contains("invalid_grant", error.Message); Assert.Contains("claude login", error.Message);
        Assert.DoesNotContain(h.Transport.Calls, c => c.IsUsage); Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "old-access");
    }
    [Fact] public async Task Fetch_Token5xxRemainsTransient()
    {
        using var h = new Harness(Fixture(true), call => call.IsUsage ? Response() : Response(503, "upstream unavailable"));
        var error = await Assert.ThrowsAsync<ClaudeProviderException>(() => h.Provider.FetchAsync()); Assert.Equal("claude_http_error", error.Code); Assert.Equal(503, error.Status); Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "old-access");
    }
    [Fact] public async Task Fetch_TokenNetworkFailureRemainsTransient()
    {
        using var h = new Harness(Fixture(true), call => call.IsUsage ? Response() : throw new HttpRequestException("getaddrinfo ENOTFOUND"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => h.Provider.FetchAsync()); Assert.Equal("claude_network", error.Code); Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "old-access");
    }
    [Fact] public async Task Fetch_ExpiredRefreshTokenAvoidsNetwork()
    {
        var oauth = Fixture(true); oauth["refreshTokenExpiresAt"] = Now - 1000;
        using var h = new Harness(oauth);
        var error = await Assert.ThrowsAsync<ProviderException>(() => h.Provider.FetchAsync()); Assert.Equal("claude_unauthorized", error.Code); Assert.Empty(h.Transport.Calls); Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "old-access");
    }
    [Fact] public async Task Http_HeadersAndUnexpiredTokenAvoidRefresh()
    {
        using var h = new Harness(Fixture());
        var before = File.ReadAllText(h.FilePath);
        await h.Provider.FetchAsync();
        var call = Assert.Single(h.Transport.Calls);
        Assert.Equal(ClaudeProvider.UsageUrl, call.Url); Assert.Equal("GET", call.Method);
        Assert.Equal("oauth-2025-04-20", call.Headers["anthropic-beta"]);
        Assert.Equal("application/json", call.Headers["Accept"]);
        Assert.Equal("agent-limit-checker/0.1.0", call.Headers["User-Agent"]);
        Assert.True(before == File.ReadAllText(h.FilePath));
        Assert.Equal(TimeSpan.FromSeconds(12), ClaudeProvider.RequestTimeout);
    }
    [Fact] public async Task Http_ClassifiesErrorsAndSanitizesUntrustedDetails()
    {
        foreach (var (status, body, code) in new[] { (302, "private-response", "claude_redirect_refused"), (200, "private-response", "claude_decode_failed"), (403, "private-response", "claude_http_error"), (401, "private-response", "claude_unauthorized") })
        {
            using var h = new Harness(Fixture(), _ => Response(status, body));
            var error = await Assert.ThrowsAnyAsync<ProviderException>(() => h.Provider.FetchUsageAsync("test-token"));
            Assert.Equal(code, error.Code); Assert.True(!error.ToString().Contains(body, StringComparison.Ordinal));
        }
        using var network = new Harness(Fixture(), _ => throw new HttpRequestException("private-response"));
        var failure = await Assert.ThrowsAsync<ProviderException>(() => network.Provider.FetchUsageAsync("test-token"));
        Assert.Equal("claude_network", failure.Code); Assert.True(!failure.ToString().Contains("private-response", StringComparison.Ordinal));
        using var timeout = new Harness(Fixture(), _ => throw new TaskCanceledException("private-response"));
        Assert.Equal("claude_timeout", (await Assert.ThrowsAsync<ProviderException>(() => timeout.Provider.FetchUsageAsync("test-token"))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timeout.Provider.FetchUsageAsync("test-token", cancel.Token));
    }
    [Fact] public async Task Http_RetryAfterMatchesNumericSecondsAndIgnoresDates()
    {
        foreach (var (raw, expected) in new[] { ("90", "約 2 分後に再試行します。"), ("1", "約 1 分後に再試行します。"), ("0", "次回ポーリングまで待機します。"), ("Wed, 21 Oct 2026 07:28:00 GMT", "次回ポーリングまで待機します。"), ("", "次回ポーリングまで待機します。") })
        {
            using var h = new Harness(Fixture(), _ => { var response = Response(429, "private-response"); if (raw.Length > 0) response.Headers.TryAddWithoutValidation("Retry-After", raw); return response; });
            var error = await Assert.ThrowsAsync<ClaudeProviderException>(() => h.Provider.FetchAsync());
            Assert.Equal("claude_rate_limited", error.Code);
            Assert.Equal("Anthropic API のレート制限に達しました (429)。" + expected, error.Message);
            if (raw == "90") Assert.Equal(90, error.RetryAfter);
            if (raw == "") Assert.Null(error.RetryAfter);
            if (raw.StartsWith("Wed", StringComparison.Ordinal)) Assert.True(double.IsNaN(error.RetryAfter!.Value));
        }
    }
    [Fact] public async Task Credentials_ReportsMissingInvalidAndAbsentTokensSafely()
    {
        using var h = new Harness(Fixture());
        File.Delete(h.FilePath);
        Assert.Equal("claude_credentials_missing", (await Assert.ThrowsAsync<ProviderException>(() => h.Provider.FetchAsync())).Code);
        File.WriteAllText(h.FilePath, "private-credential");
        var error = await Assert.ThrowsAsync<ProviderException>(() => h.Provider.FetchAsync());
        Assert.Equal("claude_credentials_invalid", error.Code); Assert.True(!error.ToString().Contains("private-credential", StringComparison.Ordinal));
        foreach (var raw in new[] { "null", "{}", "{\"claudeAiOauth\":{\"accessToken\":0}}" })
        {
            File.WriteAllText(h.FilePath, raw);
            Assert.Equal("claude_credentials_missing", (await Assert.ThrowsAsync<ProviderException>(() => h.Provider.FetchAsync())).Code);
        }
        var configured = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "  custom  " };
        Assert.Equal(Path.Combine("custom", ".credentials.json"), ClaudeProvider.CredentialsPath(configured));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"), ClaudeProvider.CredentialsPath(new Dictionary<string, string>()));
    }
    [Fact] public async Task Refresh_SkipsPostWhenCliAlreadyChangedFile()
    {
        using var h = new Harness(Fixture(true));
        var old = await h.Provider.ReadCredentialsAsync();
        var next = Fixture(); next["accessToken"] = "cli-access";
        h.Write(new JsonObject { ["claudeAiOauth"] = next });
        var recovered = await h.Provider.RefreshAccessTokenAsync(old);
        Assert.True(recovered.AccessToken == "cli-access"); Assert.Empty(h.Transport.Calls);
    }
    [Fact] public async Task WriteCredentials_PreservesLatestUnknownFieldsAndUsesAtomicReplacement()
    {
        using var h = new Harness(Fixture());
        var old = await h.Provider.ReadCredentialsAsync();
        var latest = h.Read(); latest["newField"] = "latest"; h.Write(latest);
        var oauth = Fixture(); oauth["accessToken"] = "new-access";
        var result = await h.Provider.WriteCredentialsAsync(old.Raw, oauth, old.AccessToken);
        Assert.Equal("latest", ClaudeValues.Text(result.Raw["newField"])); Assert.True(result.AccessToken == "new-access");
        Assert.Single(Directory.GetFiles(h.DirectoryPath));
        File.WriteAllText(h.FilePath, "broken");
        await h.Provider.WriteCredentialsAsync(old.Raw, oauth, old.AccessToken);
        Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "new-access");
        Assert.Single(Directory.GetFiles(h.DirectoryPath));
    }
    [Fact] public async Task Refresh_MissingTokenAvoidsPost()
    {
        var noToken = Fixture(true); noToken.Remove("refreshToken");
        using var missing = new Harness(noToken);
        Assert.Equal("claude_unauthorized", (await Assert.ThrowsAsync<ProviderException>(() => missing.Provider.FetchAsync())).Code);
        Assert.Empty(missing.Transport.Calls);
    }
    [Fact] public void Merge_CamelCaseAbsoluteExpiryAndScopeFallback()
    {
        var old = O("{\"accessToken\":\"old\",\"scopes\":\"profile inference\",\"expiresAt\":42}");
        var merged = ClaudeValues.MergeRefreshResponse(old, J("{\"accessToken\":\"new\",\"expiresAt\":1779632820,\"expiresIn\":60,\"scope\":\"  \"}"), Now);
        Assert.Equal(1779632820000, N(merged["expiresAt"])); Same(J("[\"profile\",\"inference\"]"), merged["scopes"]);
        Same(J("42"), ClaudeValues.ResolveExpiry(J("-1"), J("0"), J("42"), Now));
        Assert.Equal(1000, ClaudeValues.NormalizeExpiresAt(J("\"0x1\"")));
        Assert.Equal(1000, ClaudeValues.NormalizeExpiresAt(J("[1]")));
        Assert.Null(ClaudeValues.NormalizeExpiresAt(J("\"infinity\"")));
        Assert.Equal("claude_refresh_invalid", Assert.Throws<ProviderException>(() => ClaudeValues.MergeRefreshResponse(old, J("{}"), Now)).Code);
    }
    [Fact] public async Task Fetch_CliRetry401ThenRefreshUsesLatestRefreshToken()
    {
        Harness? harness = null;
        using var h = new Harness(Fixture(), call =>
        {
            if (call.IsUsage && call.Authorization == "Bearer old-access")
            {
                var oauth = Fixture(); oauth["accessToken"] = "cli-access"; oauth["refreshToken"] = "cli-refresh";
                harness!.Write(new JsonObject { ["claudeAiOauth"] = oauth }); return Response(401);
            }
            return call.IsUsage ? call.Authorization == "Bearer cli-access" ? Response(401) : Response()
                : Response(200, "{\"access_token\":\"recovered-access\"}");
        }); harness = h;
        Assert.Equal(.12, (await h.Provider.FetchAsync()).FiveHour!.Utilization);
        Assert.True(Assert.Single(h.Transport.Calls, c => !c.IsUsage).Body.Contains("refresh_token=cli-refresh", StringComparison.Ordinal));
        Assert.Equal(3, h.Transport.Calls.Count(c => c.IsUsage));
    }
    [Fact] public async Task Fetch_CliUpdateAfterFatalRefreshUsesNewCredentials()
    {
        Harness? harness = null;
        using var h = new Harness(Fixture(true), call =>
        {
            if (call.IsUsage) return Response();
            var next = Fixture(); next["accessToken"] = "cli-access";
            harness!.Write(new JsonObject { ["claudeAiOauth"] = next });
            return Response(401, "{\"error\":\"invalid_client\",\"error_description\":\"private-response\"}");
        }); harness = h;
        Assert.Equal(.12, (await h.Provider.FetchAsync()).FiveHour!.Utilization);
        Assert.True(Assert.Single(h.Transport.Calls, c => c.IsUsage).Authorization == "Bearer cli-access");
    }
    [Fact] public async Task Fetch_CliUpdateDuringPostWins()
    {
        Harness? harness = null;
        using var h = new Harness(Fixture(true), call =>
        {
            if (call.IsUsage) return Response();
            var oauth = Fixture(); oauth["accessToken"] = "cli-access"; oauth["refreshToken"] = "cli-refresh";
            harness!.Write(new JsonObject { ["cliOnlyField"] = "written-by-cli", ["claudeAiOauth"] = oauth });
            return Response(200, """{"access_token":"mine-access","expires_in":28800}""");
        }); harness = h;
        Assert.Equal(.12, (await h.Provider.FetchAsync()).FiveHour!.Utilization); Assert.True(Assert.Single(h.Transport.Calls, c => c.IsUsage).Authorization == "Bearer cli-access");
        Assert.True(ClaudeValues.Text(h.Oauth["accessToken"]) == "cli-access"); Assert.True(ClaudeValues.Text(h.Oauth["refreshToken"]) == "cli-refresh"); Assert.Equal("written-by-cli", ClaudeValues.Text(h.Read()["cliOnlyField"]));
    }
}

