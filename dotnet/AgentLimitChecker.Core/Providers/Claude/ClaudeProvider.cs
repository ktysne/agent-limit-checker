using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentLimitChecker.Core.Providers.Claude;

public sealed class ClaudeProviderException(string code, string message, int? status = null, double? retryAfter = null)
    : ProviderException(code, message)
{
    public int? Status { get; } = status;
    public double? RetryAfter { get; } = retryAfter;
    internal string? OAuthError { get; init; }
}

public sealed class ClaudeProvider : IDisposable
{
    internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private const string UnauthorizedMessage = "Anthropic から認証エラー (401)。`claude login` で再ログインしてください。";
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Func<IReadOnlyDictionary<string, string>> environment;
    private readonly Func<double> now;
    private bool disposed;

    public ClaudeProvider() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }),
        CliPaths.CurrentEnvironment, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true) { }
    internal ClaudeProvider(HttpClient client, Func<IReadOnlyDictionary<string, string>> environment, Func<double> now,
        bool ownsClient = false)
    {
        this.client = client;
        this.environment = environment;
        this.now = now;
        this.ownsClient = ownsClient;
    }
    public static string CredentialsPath(IReadOnlyDictionary<string, string>? env = null)
    {
        env ??= CliPaths.CurrentEnvironment();
        var dir = CliPaths.Get(env, "CLAUDE_CONFIG_DIR")?.Trim();
        return Path.Combine(string.IsNullOrEmpty(dir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : dir, ".credentials.json");
    }
    internal static (string Endpoint, string ClientId) RefreshConfig(IReadOnlyDictionary<string, string>? env = null)
    {
        env ??= CliPaths.CurrentEnvironment();
        var endpoint = CliPaths.Get(env, "CLAUDE_OAUTH_TOKEN_ENDPOINT");
        var id = CliPaths.Get(env, "CLAUDE_OAUTH_CLIENT_ID");
        return (string.IsNullOrEmpty(endpoint) ? "https://platform.claude.com/v1/oauth/token" : endpoint,
            string.IsNullOrEmpty(id) ? "9d1c250a-e61b-44d9-88ed-5944d1962f5e" : id);
    }
    internal sealed class Credentials(JsonObject raw, JsonObject oauth, string accessToken)
    {
        internal JsonObject Raw { get; } = raw;
        internal JsonObject Oauth { get; } = oauth;
        internal string AccessToken { get; } = accessToken;
    }
    internal async Task<Credentials> ReadCredentialsAsync()
    {
        string raw;
        try { raw = await File.ReadAllTextAsync(CredentialsPath(environment())).ConfigureAwait(false); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new ProviderException("claude_credentials_missing", "Claude Code の認証情報が見つかりません。`claude login` を実行してください。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ProviderException("claude_credentials_unreadable", "認証ファイルを読めません: ファイルへのアクセスに失敗しました。"); }
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); }
        catch (JsonException) { throw new ProviderException("claude_credentials_invalid", "認証ファイルの JSON が不正です: JSON の解析に失敗しました。"); }
        var oauth = ClaudeValues.Get(parsed, "claudeAiOauth") as JsonObject;
        var token = ClaudeValues.Text(ClaudeValues.Get(oauth, "accessToken"));
        if (parsed is not JsonObject obj || oauth == null || string.IsNullOrEmpty(token))
            throw new ProviderException("claude_credentials_missing", "アクセストークンが見つかりません。`claude login` で再ログインしてください。");
        return new(obj, oauth, token);
    }
    internal async Task<Credentials?> ReadFreshCredentialsIfChangedAsync(string previousAccessToken)
    {
        var latest = await ReadCredentialsAsync().ConfigureAwait(false);
        return latest.AccessToken != previousAccessToken ? latest : null;
    }
    internal async Task WriteCredentialsFileAsync(JsonObject next)
    {
        var target = CredentialsPath(environment());
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".credentials.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, next.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n").ConfigureAwait(false);
            File.Move(temp, target, true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    internal async Task<Credentials> WriteCredentialsAsync(JsonObject fallbackRaw, JsonObject oauth, string previousAccessToken)
    {
        Credentials? latest;
        try { latest = await ReadCredentialsAsync().ConfigureAwait(false); }
        catch (ProviderException) { latest = null; }
        // CLI が POST 中に更新した token を優先し、回転済みの token を上書きしない。
        if (latest != null && latest.AccessToken != previousAccessToken) return latest;
        var next = (JsonObject)(latest?.Raw ?? fallbackRaw).DeepClone();
        next["claudeAiOauth"] = oauth.DeepClone();
        await WriteCredentialsFileAsync(next).ConfigureAwait(false);
        return new(next, (JsonObject)next["claudeAiOauth"]!, ClaudeValues.Text(oauth["accessToken"])!);
    }
    internal static string? ParseOAuthErrorBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            var code = ClaudeValues.Text(ClaudeValues.Get(JsonNode.Parse(body), "error"));
            return code is "invalid_request" or "invalid_client" or "invalid_grant" or "unauthorized_client" or "unsupported_grant_type" or "invalid_scope" ? code : null;
        }
        catch (JsonException) { return null; }
    }
    internal static bool IsFatalRefreshError(ProviderException? error) => error?.Code switch
    {
        "claude_http_error" => error is ClaudeProviderException { Status: 400 },
        "claude_unauthorized" or "claude_refresh_token_missing" or "claude_refresh_expired" => true,
        _ => false
    };
    internal static string? DescribeRefreshFailure(ProviderException? error) => error is ClaudeProviderException http
        ? http.OAuthError ?? (http.Status is { } status ? $"status {status}" : null) : null;
    internal async Task<JsonNode?> HttpJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) throw new ClaudeProviderException("claude_redirect_refused", $"予期しないリダイレクト (status {status})");
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (status == 200)
            {
                try { return JsonNode.Parse(body); }
                catch (JsonException) { throw new ProviderException("claude_decode_failed", "レスポンスのJSONを解釈できません: JSON の解析に失敗しました。"); }
            }
            if (status == 429)
            {
                var raw = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
                var retry = string.IsNullOrEmpty(raw) ? (double?)null : ClaudeValues.CoerceNumber(JsonValue.Create(raw)) ?? double.NaN;
                var text = retry is { } r && r != 0 && !double.IsNaN(r)
                    ? $"約 {Math.Max(1, Math.Floor(r / 60 + 0.5)).ToString(CultureInfo.InvariantCulture)} 分後に再試行します。" : "次回ポーリングまで待機します。";
                throw new ClaudeProviderException("claude_rate_limited", $"Anthropic API のレート制限に達しました (429)。{text}", retryAfter: retry);
            }
            // 応答本文は例外に保持せず、UI に出せる固定 OAuth エラーコードだけを残す。
            throw new ClaudeProviderException(status == 401 ? "claude_unauthorized" : "claude_http_error",
                status == 401 ? UnauthorizedMessage : $"Anthropic API エラー (status {status})", status)
                { OAuthError = ParseOAuthErrorBody(body[..Math.Min(500, body.Length)]) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException("claude_timeout", "通信がタイムアウトしました。"); }
        catch (HttpRequestException) { throw new ProviderException("claude_network", "ネットワークエラー: 通信に失敗しました。"); }
        catch (IOException) { throw new ProviderException("claude_network", "ネットワークエラー: 通信に失敗しました。"); }
    }
    internal async Task<UsageSnapshot> FetchUsageAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", "agent-limit-checker/0.1.0");
        var json = await HttpJsonAsync(request, cancellationToken).ConfigureAwait(false);
        return new(ClaudeValues.ParseBucket(ClaudeValues.Get(json, "five_hour")), ClaudeValues.ParseBucket(ClaudeValues.Get(json, "seven_day")),
            ClaudeValues.ParseWeeklyScoped(json), ClaudeValues.ParseCredits(json), null, null, ClaudeValues.ParseCloudCredit(json));
    }
    internal async Task<Credentials> RefreshAccessTokenAsync(Credentials credentials, CancellationToken cancellationToken = default)
    {
        var token = ClaudeValues.Text(credentials.Oauth["refreshToken"]);
        if (string.IsNullOrEmpty(token)) throw new ProviderException("claude_refresh_token_missing", "refresh token が見つかりません。`claude login` で再ログインしてください。");
        if (ClaudeValues.NormalizeExpiresAt(credentials.Oauth["refreshTokenExpiresAt"]) is { } expiry && expiry <= now())
            throw new ProviderException("claude_refresh_expired", "refresh token の期限が切れています。`claude login` で再ログインしてください。");
        var reread = await ReadFreshCredentialsIfChangedAsync(credentials.AccessToken).ConfigureAwait(false);
        if (reread != null) return reread;
        var config = RefreshConfig(environment());
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", "agent-limit-checker/0.1.0");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = token, ["client_id"] = config.ClientId });
        var json = await HttpJsonAsync(request, cancellationToken).ConfigureAwait(false);
        var oauth = ClaudeValues.MergeRefreshResponse(credentials.Oauth, json, now());
        return await WriteCredentialsAsync(credentials.Raw, oauth, credentials.AccessToken).ConfigureAwait(false);
    }
    private async Task<Credentials> TryRecoverCredentialsAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        try { return await RefreshAccessTokenAsync(credentials, cancellationToken).ConfigureAwait(false); }
        catch (ProviderException error) when (IsFatalRefreshError(error))
        {
            var reread = await ReadFreshCredentialsIfChangedAsync(credentials.AccessToken).ConfigureAwait(false);
            if (reread != null) return reread;
            var detail = DescribeRefreshFailure(error);
            throw new ProviderException("claude_unauthorized", UnauthorizedMessage + (detail == null ? "" : $" (refresh 失敗: {detail})"));
        }
    }
    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var credentials = await ReadCredentialsAsync().ConfigureAwait(false);
        if (ClaudeValues.ShouldRefresh(credentials.Oauth, now())) credentials = await TryRecoverCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
        try { return (await FetchUsageAsync(credentials.AccessToken, cancellationToken).ConfigureAwait(false)) with { Plan = ClaudeValues.ExtractPlanLabel(credentials.Oauth) }; }
        catch (ProviderException error) when (error.Code == "claude_unauthorized")
        {
            var reread = await ReadFreshCredentialsIfChangedAsync(credentials.AccessToken).ConfigureAwait(false);
            if (reread != null)
            {
                try { return (await FetchUsageAsync(reread.AccessToken, cancellationToken).ConfigureAwait(false)) with { Plan = ClaudeValues.ExtractPlanLabel(reread.Oauth) }; }
                catch (ProviderException retryError) when (retryError.Code == "claude_unauthorized") { credentials = reread; }
            }
            var recovered = await TryRecoverCredentialsAsync(credentials, cancellationToken).ConfigureAwait(false);
            return (await FetchUsageAsync(recovered.AccessToken, cancellationToken).ConfigureAwait(false)) with { Plan = ClaudeValues.ExtractPlanLabel(recovered.Oauth) };
        }
    }
    public void Shutdown() { }
    public void Dispose() { disposed = true; if (ownsClient) client.Dispose(); }
}
