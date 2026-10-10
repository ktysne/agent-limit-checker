using System.Text;

namespace AgentLimitChecker.Core.Updates;
public sealed class HttpUpdateManifestFetcher
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const char ByteOrderMark = (char)0xFEFF;
    private const string UnknownVersion = "unknown";

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout,
    };

    private readonly HttpClient _client;
    private readonly Uri _manifestUri;
    private readonly string _userAgent;
    public HttpUpdateManifestFetcher(
        string? appVersion = null,
        Uri? manifestUri = null,
        HttpMessageHandler? handler = null)
    {
        _manifestUri = manifestUri ?? UpdateCheckDefaults.ManifestUri;
        _userAgent = $"AgentLimitChecker/{SanitizeVersion(appVersion)}";
        _client = handler is null
            ? SharedClient
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout };
    }
    public async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _manifestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

        var buffer = new byte[MaxResponseBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        return text.TrimStart(ByteOrderMark);
    }
    internal static string SanitizeVersion(string? appVersion)
    {
        if (string.IsNullOrWhiteSpace(appVersion))
        {
            return UnknownVersion;
        }

        var trimmed = appVersion.Trim();
        var plus = trimmed.IndexOf('+');
        var version = plus >= 0 ? trimmed[..plus] : trimmed;

        return version.Length > 0 && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? version
            : UnknownVersion;
    }
}
