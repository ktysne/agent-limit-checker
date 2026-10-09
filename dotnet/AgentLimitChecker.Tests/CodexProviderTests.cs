using System.Text.Json;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Codex;

namespace AgentLimitChecker.Tests;

public sealed class CodexProviderTests
{
    internal static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    [Fact] public void ExtractPlanLabel_ReadsTopLevelPlanType()
    {
        Assert.Equal("Plus", CodexUsageParser.ExtractPlanLabel(Json("""{"rateLimits":{"planType":"plus"}}""")));
        Assert.Equal("Pro", CodexUsageParser.ExtractPlanLabel(Json("""{"rateLimits":{"planType":"pro"}}""")));
    }
    [Fact] public void ExtractPlanLabel_FallsBackToSortedProfiles() => Assert.Equal("Team", CodexUsageParser.ExtractPlanLabel(Json("""{"rateLimitsByLimitId":{"beta":{"planType":"pro"},"alpha":{"planType":"team"}}}""")));
    [Fact] public void ExtractPlanLabel_ReturnsNullWithoutPlanType()
    {
        foreach (var text in new[] { "null", "{}", """{"rateLimits":{}}""" }) Assert.Null(CodexUsageParser.ExtractPlanLabel(Json(text)));
    }
    [Fact] public void ParseCredits_SurfacesPositivePurchasedBalance()
    {
        var credits = CodexUsageParser.ParseCredits(Json("""{"rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"115.9354600000"}}}"""));
        Assert.NotNull(credits); Assert.Null(credits.Currency); Assert.False(credits.Unlimited); Assert.Equal(115.93546, credits.Amount!.Value, 9);
    }
    [Fact] public void ParseCredits_HidesWithoutCreditsOrZeroOrMissingNode()
    {
        foreach (var text in new[] { """{"rateLimits":{"credits":{"hasCredits":false,"balance":"10"}}}""", """{"rateLimits":{"credits":{"hasCredits":true,"balance":"0"}}}""", """{"rateLimits":{}}""", "{}", "null" }) Assert.Null(CodexUsageParser.ParseCredits(Json(text)));
    }
    [Fact] public void ParseCredits_ReportsUnlimitedDistinctly() => Assert.Equal(new CreditBalance(null, null, true), CodexUsageParser.ParseCredits(Json("""{"rateLimits":{"credits":{"hasCredits":true,"unlimited":true,"balance":null}}}""")));
    [Fact] public void ParseCredits_FallsBackToSortedProfiles() => Assert.Equal(5, CodexUsageParser.ParseCredits(Json("""{"rateLimitsByLimitId":{"beta":{"credits":{"hasCredits":true,"unlimited":false,"balance":"10"}},"alpha":{"credits":{"hasCredits":true,"unlimited":false,"balance":"5"}}}}"""))!.Amount);
    [Fact] public void ParseCredits_PrefersTopLevelOverProfile() => Assert.Equal(42, CodexUsageParser.ParseCredits(Json("""{"rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"42"}},"rateLimitsByLimitId":{"alpha":{"credits":{"hasCredits":true,"unlimited":false,"balance":"5"}}}}"""))!.Amount);
    [Fact] public void ParseCredits_SkipsCreditlessNode() => Assert.Equal(7, CodexUsageParser.ParseCredits(Json("""{"rateLimits":{"credits":{"hasCredits":false,"balance":"0"}},"rateLimitsByLimitId":{"alpha":{"credits":{"hasCredits":true,"unlimited":false,"balance":"7"}}}}"""))!.Amount);
    [Fact] public void ParseCredits_HidesNegativeOrNonNumericBalance()
    {
        foreach (var balance in new[] { "\"-5\"", "null", "\"oops\"" }) Assert.Null(CodexUsageParser.ParseCredits(Json("{\"rateLimits\":{\"credits\":{\"hasCredits\":true,\"balance\":" + balance + "}}}")));
    }
    [Fact] public void ParseResetCredits_ReportsCountAndEarliestExpiration() => Assert.Equal(new ResetCreditBalance(2, 1791153838000), CodexUsageParser.ParseResetCredits(Json("""{"rateLimitResetCredits":{"availableCount":2,"credits":[{"status":"available","expiresAt":1791153838,"title":"Full reset (Weekly + 5 hr)"},{"status":"available","expiresAt":1792694262}]}}""")));
    [Fact] public void ParseResetCredits_HidesMissingCount()
    {
        foreach (var text in new[] { "null", "{}", """{"rateLimitResetCredits":null}""", """{"rateLimitResetCredits":{"availableCount":0}}""", """{"rateLimitResetCredits":{"availableCount":1e999}}""" }) Assert.Null(CodexUsageParser.ParseResetCredits(Json(text)));
    }
    [Fact] public void ParseResetCredits_ReturnsCountWithoutExpirationDetails() => Assert.Equal(new ResetCreditBalance(2, null), CodexUsageParser.ParseResetCredits(Json("""{"rateLimitResetCredits":{"availableCount":2,"credits":null}}""")));
    [Fact] public void ParseResetCredits_IgnoresNonAvailableAndNonExpiring() => Assert.Equal(new ResetCreditBalance(3, 1792694262000), CodexUsageParser.ParseResetCredits(Json("""{"rateLimitResetCredits":{"availableCount":3,"credits":[{"status":"redeemed","expiresAt":100},{"status":"available","expiresAt":null},{"status":"available","expiresAt":1792694262}]}}""")));
    [Fact] public void AuthFilePath_FollowsGivenHome()
    {
        var home = Path.Combine("tmp", "custom-codex-home");
        Assert.Equal(Path.Combine(home, "auth.json"), CodexProvider.CodexAuthFile(home));
        Assert.Equal(Path.Combine(home, "auth.json"), CodexProvider.AuthFilePath(home));
    }
    [Fact] public void AuthFilePath_FallsBackToConfiguredDefault()
    {
        var old = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine("tmp", "custom-codex-home"));
            Assert.Equal(Path.Combine("tmp", "custom-codex-home", "auth.json"), CodexProvider.CodexAuthFile());
            Assert.Equal(Path.Combine("tmp", "custom-codex-home", "auth.json"), CodexProvider.AuthFilePath());
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", old); }
    }
    [Fact] public void IsRestartableError_TriggersOnExitedServer() => Assert.True(CodexUsageParser.IsRestartableError(new("codex_process_exited", "codex app-server が終了しました (exit null)")));
    [Fact] public void IsRestartableError_TriggersOnAuthRpcErrors()
    {
        foreach (var message in new[] { "Codex RPC エラー: failed to fetch codex rate limits: GET https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized; { \"error\": { \"message\": \"Your authentication token has been invalidated. Please try signing in again.\", \"code\": \"token_invalidated\", \"status\": 401 } }", "unauthorized", "not signed in" }) Assert.True(CodexUsageParser.IsRestartableError(new("codex_rpc_error", message)));
        Assert.True(CodexUsageParser.IsRestartableError(new("codex_rpc_error", "redacted", true)));
    }
    [Fact] public void IsRestartableError_LeavesNonAuthErrorsAlone()
    {
        Assert.False(CodexUsageParser.IsRestartableError(new("codex_rpc_error", "account/rateLimits/read のレスポンスに result がありません")));
        Assert.False(CodexUsageParser.IsRestartableError(new("codex_timeout", "RPC account/rateLimits/read がタイムアウトしました")));
        Assert.False(CodexUsageParser.IsRestartableError(null));
    }
    [Fact] public void MakeCodexRpcError_RedactsAuthDetailsButPreservesRestartability()
    {
        var error = CodexUsageParser.MakeCodexRpcError(Json("""{"message":"failed to fetch codex rate limits: GET https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized; { \"access_token\": \"redacted-token\", \"error\": { \"code\": \"token_invalidated\" } }"}"""));
        Assert.Equal("codex_rpc_error", error.Code); Assert.True(error.Restartable); Assert.True(CodexUsageParser.IsRestartableError(error));
        Assert.Equal("Codex の認証が失効しています。ログインし直してください。", error.Message);
        Assert.DoesNotContain("redacted-token", error.ToString());
    }
    [Fact] public void MakeCodexRpcError_DetectsNestedAuthDetails()
    {
        var error = CodexUsageParser.MakeCodexRpcError(Json("""{"message":"request failed","data":{"status":401,"error":{"code":"token_invalidated"}}}"""));
        Assert.True(error.Restartable); Assert.True(CodexUsageParser.IsRestartableError(error));
        Assert.Equal("Codex の認証が失効しています。ログインし直してください。", error.Message);
    }
    [Fact] public void MakeCodexRpcError_RedactsNonAuthDetails()
    {
        var error = CodexUsageParser.MakeCodexRpcError(Json("""{"message":"unexpected upstream payload","data":{"responseBody":"{\"access_token\":\"redacted-token\"}"}}"""));
        Assert.Equal("codex_rpc_error", error.Code); Assert.False(error.Restartable);
        Assert.Equal("Codex RPC エラー: rate limit の取得に失敗しました", error.Message);
        Assert.DoesNotContain("redacted-token", error.ToString());
    }
    [Fact] public void PickWindow_PreservesPriorityAndConvertsSecondsToMilliseconds()
    {
        var dto = Json("""{"rateLimits":{"primary":{"windowDurationMins":300,"usedPercent":140,"resetsAt":100},"secondary":{"windowDurationMins":10080,"usedPercent":-5,"resetsAt":200}},"rateLimitsByLimitId":{"alpha":{"primary":{"windowDurationMins":300,"usedPercent":5,"resetsAt":300}}}}""");
        Assert.Equal(new RateLimit(1.4, 100000), CodexUsageParser.PickWindow(dto, 300));
        Assert.Equal(new RateLimit(0, 200000), CodexUsageParser.PickWindow(dto, 10080));
        Assert.Null(CodexUsageParser.PickWindow(Json("{}"), 300));
        Assert.Null(CodexUsageParser.PickWindow(Json("""{"rateLimits":{"primary":{"windowDurationMins":300,"usedPercent":"5","resetsAt":100}}}"""), 300));
    }
}
