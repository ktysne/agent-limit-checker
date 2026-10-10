using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Core.Notifications;

public sealed record NtfyMessage(string Title, string Message, string? Priority = null, string? Tags = null);

public sealed record NtfySendResult(string? Id, string? Topic);

public sealed class NtfyException(string message, int? status = null, bool retryable = false, string? requestId = null)
    : Exception(message)
{
    public string Code { get; } = "ntfy_api_error";
    public int? Status { get; } = status;
    public bool Retryable { get; } = retryable;
    public string? RequestId { get; } = requestId;
}

public interface INtfyMessageSender
{
    Task<NtfySendResult> SendAsync(NtfySettings settings, NtfyMessage message, CancellationToken cancellationToken = default);
}

public sealed class NtfyMessageSender : INtfyMessageSender, IDisposable
{
    private const string UserAgent = "agent-limit-checker/1.0";
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public NtfyMessageSender(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        ownsClient = httpClient is null;
    }

    public async Task<NtfySendResult> SendAsync(
        NtfySettings settings,
        NtfyMessage message,
        CancellationToken cancellationToken = default)
    {
        var topicUrl = NtfyResetNotifier.NormalizeTopicUrl(settings.TopicUrl);
        if (topicUrl.Length == 0) throw new NtfyException("ntfy topic URL is not configured");

        using var request = new HttpRequestMessage(HttpMethod.Post, topicUrl)
        {
            Content = new StringContent(message.Message ?? "", Encoding.UTF8, "text/plain"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Title", message.Title ?? "");
        if (!string.IsNullOrEmpty(message.Priority)) request.Headers.TryAddWithoutValidation("Priority", message.Priority);
        if (!string.IsNullOrEmpty(message.Tags)) request.Headers.TryAddWithoutValidation("Tags", message.Tags);
        var token = settings.AccessToken.Trim();
        if (token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new NtfyException("ntfy request failed", retryable: true);
        }

        using (response)
        {
            var payload = await TryReadPayloadAsync(response, cancellationToken).ConfigureAwait(false);
            var requestId = ReadString(payload, "id");
            if (response.IsSuccessStatusCode)
            {
                return new NtfySendResult(requestId, ReadString(payload, "topic"));
            }

            var status = (int)response.StatusCode;
            var retryable = status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests || status == 0;
            throw new NtfyException($"ntfy API error (status {status})", status, retryable, requestId);
        }
    }

    public void Dispose()
    {
        if (ownsClient) httpClient.Dispose();
    }

    private static async Task<JsonElement?> TryReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? payload, string propertyName)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
