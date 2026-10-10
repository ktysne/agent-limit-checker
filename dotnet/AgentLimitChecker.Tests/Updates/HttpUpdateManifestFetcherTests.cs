using System.Net;
using System.Text;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;
public class HttpUpdateManifestFetcherTests
{
    private static readonly Uri TestUri = new("https://example.invalid/agent-limit-checker/update.json");
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;

        public StubHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
            : this(Encoding.UTF8.GetBytes(body), status)
        {
        }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body),
            });
        }
    }

    private static HttpUpdateManifestFetcher Fetcher(StubHandler handler, string? appVersion = "1.0.0")
        => new(appVersion, TestUri, handler);
    [Fact]
    public async Task FetchAsync_ResponseWithByteOrderMark_StripsIt()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("{ \"schema\": 1 }"))
            .ToArray();
        using var handler = new StubHandler(body);

        var text = await Fetcher(handler).FetchAsync(CancellationToken.None);

        Assert.Equal("{ \"schema\": 1 }", text);
    }
    [Fact]
    public async Task FetchAsync_NotFound_Throws()
    {
        using var handler = new StubHandler("not found", HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Fetcher(handler).FetchAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task FetchAsync_RedirectResponseIsRejected(HttpStatusCode status)
    {
        using var handler = new StubHandler("{}", status);
        await Assert.ThrowsAsync<HttpRequestException>(() => Fetcher(handler).FetchAsync(CancellationToken.None));
        Assert.Equal(TestUri, handler.LastRequest?.RequestUri);
    }
    [Fact]
    public async Task FetchAsync_OversizedResponse_IsTruncated()
    {
        const int maxResponseBytes = 64 * 1024;
        using var handler = new StubHandler(new string('a', maxResponseBytes + 1024));

        var text = await Fetcher(handler).FetchAsync(CancellationToken.None);

        Assert.Equal(maxResponseBytes, text.Length);
    }
    [Fact]
    public async Task FetchAsync_SendsUserAgentWithVersion()
    {
        using var handler = new StubHandler("{}");

        await Fetcher(handler, "1.2.3").FetchAsync(CancellationToken.None);

        Assert.Equal("AgentLimitChecker/1.2.3", handler.LastRequest?.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc1234", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void SanitizeVersion_KeepsHeaderSafeText(string appVersion, string expected)
    {
        Assert.Equal(expected, HttpUpdateManifestFetcher.SanitizeVersion(appVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("開発ビルド")]
    [InlineData("1.0.0 (debug)")]
    public void SanitizeVersion_UnsafeOrMissingText_FallsBackToUnknown(string? appVersion)
    {
        Assert.Equal("unknown", HttpUpdateManifestFetcher.SanitizeVersion(appVersion));
    }
}
