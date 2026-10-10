using System.Net;

namespace AgentLimitChecker.Core.Updates;
public sealed class HttpUpdatePackageDownloader : IUpdatePackageDownloader
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 81920;

    private static readonly HttpClient SharedClient = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = DefaultConnectTimeout,
        })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly TimeSpan _stallTimeout;
    public HttpUpdatePackageDownloader(string? appVersion = null, HttpMessageHandler? handler = null, TimeSpan? stallTimeout = null)
    {
        _userAgent = $"AgentLimitChecker/{HttpUpdateManifestFetcher.SanitizeVersion(appVersion)}";
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _client = handler is null
            ? SharedClient
            : new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task DownloadAsync(
        Uri url,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            var currentUrl = url;
            var redirectsFollowed = 0;
            HttpResponseMessage response;
            while (true)
            {
                stall.CancelAfter(_stallTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

                response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                    .ConfigureAwait(false);
                if (!UpdateDownloadRedirectPolicy.IsRedirectStatus((int)response.StatusCode))
                {
                    break;
                }

                using (response)
                {
                    var locations = response.Headers.TryGetValues("Location", out var values)
                        ? values.ToArray()
                        : [];
                    if (locations.Length != 1
                        || !UpdateDownloadRedirectPolicy.TryGetTarget(
                            (int)response.StatusCode,
                            redirectsFollowed,
                            locations[0],
                            out var redirectTarget))
                    {
                        throw new UpdatePreparationException("配布ファイルの転送先が許可されていません。");
                    }

                    currentUrl = redirectTarget!;
                    redirectsFollowed++;
                }
            }

            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new UpdatePreparationException($"配布ファイルを取得できませんでした（HTTP {(int)response.StatusCode}）。");
                }

                var total = response.Content.Headers.ContentLength;
                if (total > MaxPackageBytes)
                {
                    throw new UpdatePreparationException("配布ファイルが大きすぎます。");
                }

                progress?.Report(new UpdateDownloadProgress(0, total));

                await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                await using var destination = new FileStream(
                    destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

                var buffer = new byte[BufferSize];
                long received = 0;
                while (true)
                {
                    stall.CancelAfter(_stallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > MaxPackageBytes)
                    {
                        throw new UpdatePreparationException("配布ファイルが大きすぎます。");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new UpdateDownloadProgress(received, total));
                }

                if (total is { } expected && received != expected)
                {
                    throw new UpdatePreparationException("配布ファイルを最後まで受信できませんでした。");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdatePreparationException(
                $"配布サイトからの応答が {(int)_stallTimeout.TotalSeconds} 秒止まったため、ダウンロードを中止しました。");
        }
        catch (HttpRequestException ex)
        {
            throw new UpdatePreparationException($"配布サイトに接続できませんでした: {ex.Message}", ex);
        }
    }
}
