using System.Net;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public sealed class HttpUpdatePackageDownloaderTests : IDisposable
{
    private static readonly Uri PackageUri = new("https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.0-win-x64.zip");

    private readonly string _directory = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-download-" + Guid.NewGuid().ToString("N"));

    public HttpUpdatePackageDownloaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private string Destination => Path.Combine(_directory, "package.zip.partial");

    [Fact]
    public async Task DownloadAsync_WritesBodyReportsProgressAndSendsUserAgent()
    {
        var body = new byte[200_000];
        Random.Shared.NextBytes(body);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        var reports = new List<UpdateDownloadProgress>();

        await new HttpUpdatePackageDownloader("1.1.0+abc", handler)
            .DownloadAsync(PackageUri, Destination, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(new UpdateDownloadProgress(body.Length, body.Length), reports[^1]);
        Assert.Equal("AgentLimitChecker/1.1.0", handler.LastUserAgent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task DownloadAsync_NonOkStatus_Fails(HttpStatusCode status)
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([1, 2, 3]) };
            return response;
        });

        await Assert.ThrowsAsync<UpdatePreparationException>(() =>
            new HttpUpdatePackageDownloader(null, handler).DownloadAsync(PackageUri, Destination, null, CancellationToken.None));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_Follows308AndKeepsSignedQueryUnchanged()
    {
        var githubReleaseUri = new Uri("https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.0/AgentLimitChecker-1.2.0-win-x64.zip");
        const string signedUrl = "https://objects.githubusercontent.com/release.zip?sig=a+b%2Bc%3Bd";
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
                redirect.Headers.TryAddWithoutValidation("Location", signedUrl);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });

        await new HttpUpdatePackageDownloader(null, handler)
            .DownloadAsync(githubReleaseUri, Destination, null, CancellationToken.None);

        Assert.Equal(new[] { githubReleaseUri.OriginalString, signedUrl }, handler.RequestUrls);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectToUnapprovedHost()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/a.zip");
            return response;
        });

        await Assert.ThrowsAsync<UpdatePreparationException>(() =>
            new HttpUpdatePackageDownloader(null, handler).DownloadAsync(PackageUri, Destination, null, CancellationToken.None));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_FollowsAtMostFiveRedirects()
    {
        var handler = new StubHandler(request =>
        {
            var hop = request.RequestUri!.Host == "ktysne.info"
                ? 0
                : int.Parse(request.RequestUri.Segments[^1].TrimEnd('/'));
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", $"https://github.com/{hop + 1}");
            return response;
        });

        await Assert.ThrowsAsync<UpdatePreparationException>(() =>
            new HttpUpdatePackageDownloader(null, handler).DownloadAsync(PackageUri, Destination, null, CancellationToken.None));
        Assert.Equal(6, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_DeclaredLengthOverLimit_FailsBeforeReading()
    {
        var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = HttpUpdatePackageDownloader.MaxPackageBytes + 1;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        await Assert.ThrowsAsync<UpdatePreparationException>(() =>
            new HttpUpdatePackageDownloader(null, handler).DownloadAsync(PackageUri, Destination, null, CancellationToken.None));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task DownloadAsync_StalledBody_FailsWithTimeoutMessage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });

        var ex = await Assert.ThrowsAsync<UpdatePreparationException>(() =>
            new HttpUpdatePackageDownloader(null, handler, TimeSpan.FromMilliseconds(200))
                .DownloadAsync(PackageUri, Destination, null, CancellationToken.None));

        Assert.Contains("止まった", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_CallerCancellation_IsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpUpdatePackageDownloader(null, handler, TimeSpan.FromSeconds(30))
                .DownloadAsync(PackageUri, Destination, null, cancellation.Token));
    }

    [Fact]
    public async Task DownloadAsync_ShortBody_Fails()
    {
        var content = new StreamContent(new MemoryStream([1, 2, 3]));
        content.Headers.ContentLength = 10;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new HttpUpdatePackageDownloader(null, handler).DownloadAsync(PackageUri, Destination, null, CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastUserAgent { get; private set; }

        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastUserAgent = request.Headers.UserAgent.ToString();
            RequestUrls.Add(request.RequestUri!.OriginalString);
            return Task.FromResult(respond(request));
        }
    }
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyncProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}

public sealed class FileSystemUpdateFileOperationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-fileops-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemUpdateFileOperations _operations = new();

    public FileSystemUpdateFileOperationsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void CanOpenExclusively_IsFalseWhileAnotherHandleIsOpen()
    {
        var path = Path.Combine(_directory, "AgentLimitChecker.exe");
        File.WriteAllText(path, "x");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.False(_operations.CanOpenExclusively(path));
        }

        Assert.True(_operations.CanOpenExclusively(path));
        Assert.True(_operations.CanOpenExclusively(Path.Combine(_directory, "missing.exe")));
    }

    [Fact]
    public void WriteNewFileAtomically_DoesNotOverwriteAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "cleanup.json");

        Assert.True(_operations.WriteNewFileAtomically(path, "first"));
        Assert.False(_operations.WriteNewFileAtomically(path, "second"));

        Assert.Equal("first", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void CopyFile_WhenSourceIsMissing_LeavesNothingAtDestination()
    {
        var destination = Path.Combine(_directory, "b");

        Assert.False(_operations.CopyFile(Path.Combine(_directory, "missing"), destination));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ReplaceFileAtomically_OverwritesAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "cleanup.json");
        File.WriteAllText(path, "first");

        Assert.True(_operations.ReplaceFileAtomically(path, "second"));

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void MoveAndCopy_DoNotOverwrite()
    {
        var source = Path.Combine(_directory, "a");
        var destination = Path.Combine(_directory, "b");
        File.WriteAllText(source, "a");
        File.WriteAllText(destination, "b");

        Assert.False(_operations.MoveFile(source, destination));
        Assert.False(_operations.CopyFile(source, destination));
        Assert.Equal("b", File.ReadAllText(destination));
    }

    [Fact]
    public void DeleteFile_RemovesReadOnlyFileAndTreatsMissingAsSuccess()
    {
        var path = Path.Combine(_directory, "ro.old");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.True(_operations.DeleteFile(path));
        Assert.False(File.Exists(path));
        Assert.True(_operations.DeleteFile(path));
    }

    [Fact]
    public void DeleteDirectoryTree_RemovesNestedContents()
    {
        var tree = Path.Combine(_directory, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(Path.Combine(tree, "app"));
        File.WriteAllText(Path.Combine(tree, "app", "AgentLimitChecker.exe"), "x");

        Assert.True(_operations.DeleteDirectoryTree(tree));
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void ListDirectory_SeparatesFilesAndDirectories()
    {
        File.WriteAllText(Path.Combine(_directory, "cleanup-x.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_directory, "folder"));

        var listing = _operations.ListDirectory(_directory);

        Assert.Equal(["cleanup-x.json"], listing!.Files);
        Assert.Equal(["folder"], listing.Directories);
        Assert.Null(_operations.ListDirectory(Path.Combine(_directory, "missing")));
    }
}
