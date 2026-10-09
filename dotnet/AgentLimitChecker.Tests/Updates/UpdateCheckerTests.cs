using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;
public class UpdateCheckerTests
{
    private static string Manifest(string version) =>
        $$"""
        {
          "schema": 2,
          "latest": {
            "version": "{{version}}",
            "url": "https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-{{version}}-win-x64.zip",
            "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "releasedAt": "2026-09-04"
          }
        }
        """;

    private static UpdateChecker Checker(string manifestVersion, string? currentVersion) =>
        new(_ => Task.FromResult(Manifest(manifestVersion)), currentVersion);

    [Fact]
    public async Task NewerVersion_IsAvailable()
    {
        var result = await Checker("1.1.0", "1.0.0").CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Available, result.Kind);
        Assert.Equal("1.1.0", result.Update?.VersionText);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SameVersion_IsUpToDate()
    {
        var result = await Checker("1.0.0", "1.0.0").CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.UpToDate, result.Kind);
    }

    [Fact]
    public async Task OlderVersion_IsUpToDate()
    {
        var result = await Checker("0.9.0", "1.0.0").CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.UpToDate, result.Kind);
    }
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.7")]
    public async Task CurrentVersionWithDifferentComponentCount_IsUpToDate(string currentVersion)
    {
        var result = await Checker("1.0.0", currentVersion).CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.UpToDate, result.Kind);
    }

    [Fact]
    public async Task NewerVersionAlreadySkipped_IsSkipped()
    {
        var result = await Checker("1.1.0", "1.0.0").CheckAsync(skippedVersion: "1.1.0", CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Skipped, result.Kind);
        Assert.Equal("1.1.0", result.Update?.VersionText);
    }

    [Fact]
    public async Task VersionNewerThanSkipped_IsAvailable()
    {
        var result = await Checker("1.2.0", "1.0.0").CheckAsync(skippedVersion: "1.1.0", CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Available, result.Kind);
        Assert.Equal("1.2.0", result.Update?.VersionText);
    }

    [Fact]
    public async Task FetchThrows_IsFailed()
    {
        var checker = new UpdateChecker(
            _ => throw new HttpRequestException("ネットワークに接続できません"),
            "1.0.0");

        var result = await checker.CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Null(result.Update);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task BrokenManifest_IsFailed()
    {
        var checker = new UpdateChecker(_ => Task.FromResult("{ broken"), "1.0.0");

        var result = await checker.CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task CurrentVersionWithBuildMetadata_IsComparable()
    {
        var newer = await Checker("1.1.0", "1.0.0+abc1234").CheckAsync(skippedVersion: null, CancellationToken.None);
        Assert.Equal(UpdateCheckKind.Available, newer.Kind);

        var same = await Checker("1.0.0", "1.0.0+abc1234").CheckAsync(skippedVersion: null, CancellationToken.None);
        Assert.Equal(UpdateCheckKind.UpToDate, same.Kind);
    }

    [Fact]
    public async Task UnknownCurrentVersion_IsFailed()
    {
        var result = await Checker("1.1.0", null).CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Null(result.Update);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task UnparsableCurrentVersion_IsFailed()
    {
        var result = await Checker("1.1.0", "開発ビルド").CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
    }
    [Fact]
    public async Task UnknownCurrentVersion_DoesNotFetchManifest()
    {
        var fetched = false;
        var checker = new UpdateChecker(
            _ =>
            {
                fetched = true;
                return Task.FromResult(Manifest("1.1.0"));
            },
            currentVersionText: null);

        await checker.CheckAsync(skippedVersion: null, CancellationToken.None);

        Assert.False(fetched);
    }
    [Fact]
    public async Task Cancelled_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var checker = new UpdateChecker(
            token => Task.FromCanceled<string>(token),
            "1.0.0");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.CheckAsync(skippedVersion: null, cancellation.Token));
    }
    [Fact]
    public async Task InvalidSkippedVersion_IsIgnored()
    {
        var result = await Checker("1.1.0", "1.0.0").CheckAsync(skippedVersion: "なにか", CancellationToken.None);

        Assert.Equal(UpdateCheckKind.Available, result.Kind);
    }
}
