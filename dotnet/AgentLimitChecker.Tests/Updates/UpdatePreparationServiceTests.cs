using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public sealed class UpdatePreparationServiceTests : IDisposable
{
    private readonly string _updateRoot = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-prepare-" + Guid.NewGuid().ToString("N"), "update");
    private readonly List<UpdatePreparationProgress> _progress = [];

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_updateRoot)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateZip(params (string Name, string Contents)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(contents);
            }
        }

        return buffer.ToArray();
    }

    private static UpdateInfo Info(byte[] zip, string version = "1.2.0") => new(
        new Version(version),
        version,
        new Uri($"https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-{version}-win-x64.zip"),
        Convert.ToHexStringLower(SHA256.HashData(zip)));

    private UpdatePreparationService CreateService(FakeDownloader downloader) =>
        new(downloader, new FileSystemUpdateFileOperations(), _updateRoot, _ => { });

    private Task<PreparedUpdate> PrepareAsync(FakeDownloader downloader, UpdateInfo info, CancellationToken cancellationToken = default) =>
        CreateService(downloader).PrepareAsync(info, new SyncProgress(_progress.Add), cancellationToken);

    private string[] UpdateFolders() =>
        Directory.Exists(_updateRoot) ? Directory.GetDirectories(_updateRoot) : [];

    [Fact]
    public async Task PrepareAsync_DownloadsVerifiesAndExtractsFlatPackage()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"), ("manual.html", "manual"));
        var info = Info(zip);

        var prepared = await PrepareAsync(new FakeDownloader(zip), info);

        Assert.False(prepared.Reused);
        Assert.Equal(Path.Combine(_updateRoot, prepared.UpdateId, "app"), prepared.StagingDirectory);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(prepared.StagingDirectory, "AgentLimitChecker.exe")));
        Assert.Equal("manual", File.ReadAllText(Path.Combine(prepared.StagingDirectory, "manual.html")));
        Assert.False(File.Exists(Path.Combine(prepared.UpdateDirectory, "AgentLimitChecker-1.2.0-win-x64.zip.partial")));
        var record = UpdateRecordSerializer.ParsePrepared(File.ReadAllText(Path.Combine(prepared.UpdateDirectory, "prepared.json")));
        Assert.Equal(new UpdatePreparedRecord("1.2.0", info.Sha256!), record);
        Assert.Equal(
            [UpdatePreparationStage.Preparing, UpdatePreparationStage.Downloading, UpdatePreparationStage.Verifying, UpdatePreparationStage.Extracting, UpdatePreparationStage.Ready],
            _progress.Select(p => p.Stage).Distinct().ToArray());
    }

    [Fact]
    public async Task PrepareAsync_ShaMismatch_FailsAndRemovesFolder()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"));
        var info = Info(zip) with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<UpdatePreparationException>(() => PrepareAsync(new FakeDownloader(zip), info));

        Assert.Empty(UpdateFolders());
    }

    [Fact]
    public async Task PrepareAsync_PackageWithSubfolder_FailsAndRemovesFolder()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"), ("docs/manual.html", "manual"));

        var ex = await Assert.ThrowsAsync<UpdatePreparationException>(() => PrepareAsync(new FakeDownloader(zip), Info(zip)));

        Assert.Contains("docs/manual.html", ex.Message);
        Assert.Empty(UpdateFolders());
    }

    [Fact]
    public async Task PrepareAsync_Cancelled_RemovesFolder()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"));
        using var cancellation = new CancellationTokenSource();
        var downloader = new FakeDownloader(zip) { BeforeWrite = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrepareAsync(downloader, Info(zip), cancellation.Token));

        Assert.Empty(UpdateFolders());
    }

    [Fact]
    public async Task PrepareAsync_WithoutSha256_Fails()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"));

        await Assert.ThrowsAsync<UpdatePreparationException>(() => PrepareAsync(new FakeDownloader(zip), Info(zip) with { Sha256 = null }));
    }

    [Fact]
    public async Task PrepareAsync_ReusesPreparedFolderWithSameVersionAndHash()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"));
        var info = Info(zip);
        var first = await PrepareAsync(new FakeDownloader(zip), info);
        var downloader = new FakeDownloader(zip);

        var second = await PrepareAsync(downloader, info);

        Assert.True(second.Reused);
        Assert.Equal(first.UpdateId, second.UpdateId);
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_DoesNotReuseWhenZipWasAltered()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"));
        var info = Info(zip);
        var first = await PrepareAsync(new FakeDownloader(zip), info);
        File.AppendAllText(Path.Combine(first.UpdateDirectory, "AgentLimitChecker-1.2.0-win-x64.zip"), "tampered");

        var second = await PrepareAsync(new FakeDownloader(zip), info);

        Assert.False(second.Reused);
        Assert.NotEqual(first.UpdateId, second.UpdateId);
    }

    [Fact]
    public async Task PrepareAsync_DoesNotReuseWhenExecutableIsMissing()
    {
        var zip = CreateZip(("AgentLimitChecker.exe", "exe"));
        var info = Info(zip);
        var first = await PrepareAsync(new FakeDownloader(zip), info);
        File.Delete(Path.Combine(first.StagingDirectory, "AgentLimitChecker.exe"));

        var second = await PrepareAsync(new FakeDownloader(zip), info);

        Assert.False(second.Reused);
    }

    private sealed class FakeDownloader(byte[] contents) : IUpdatePackageDownloader
    {
        public int CallCount { get; private set; }

        public Action? BeforeWrite { get; init; }

        public async Task DownloadAsync(Uri url, string destinationPath, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            BeforeWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(destinationPath, contents, cancellationToken);
            progress?.Report(new UpdateDownloadProgress(contents.Length, contents.Length));
        }
    }

    private sealed class SyncProgress(Action<UpdatePreparationProgress> report) : IProgress<UpdatePreparationProgress>
    {
        public void Report(UpdatePreparationProgress value) => report(value);
    }
}
