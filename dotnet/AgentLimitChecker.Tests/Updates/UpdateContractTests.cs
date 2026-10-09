using System.Text;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public class UpdateCommandLineTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Parse_NoArguments_IsNormalStartup()
    {
        Assert.Equal(UpdateCommandKind.Normal, UpdateCommandLine.Parse([]).Kind);
    }

    [Fact]
    public void Parse_ApplyUpdate_ReadsProcessIdAndPaths()
    {
        var parsed = UpdateCommandLine.Parse(["--apply-update", "1234", @"C:\u\app", @"C:\Apps\AgentLimitChecker"]);

        Assert.Equal(UpdateCommandKind.Apply, parsed.Kind);
        Assert.Equal(1234, parsed.ProcessId);
        Assert.Equal(@"C:\u\app", parsed.StagingDirectory);
        Assert.Equal(@"C:\Apps\AgentLimitChecker", parsed.InstallDirectory);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("12a")]
    [InlineData("99999999999")]
    [InlineData("")]
    public void Parse_ApplyUpdateWithInvalidProcessId_IsInvalid(string processId)
    {
        Assert.Equal(UpdateCommandKind.InvalidApply, UpdateCommandLine.Parse(["--apply-update", processId, "a", "b"]).Kind);
    }

    [Fact]
    public void Parse_ApplyUpdateWithWrongArgumentCount_IsInvalid()
    {
        Assert.Equal(UpdateCommandKind.InvalidApply, UpdateCommandLine.Parse(["--apply-update", "12", "a"]).Kind);
        Assert.Equal(UpdateCommandKind.InvalidApply, UpdateCommandLine.Parse(["--apply-update", "12", "a", "b", "c"]).Kind);
    }

    [Fact]
    public void Parse_ApplyUpdateNotFirst_IsNormalStartup()
    {
        Assert.Equal(UpdateCommandKind.Normal, UpdateCommandLine.Parse(["x", "--apply-update", "12", "a", "b"]).Kind);
    }

    [Fact]
    public void Parse_Updated_ReadsVersionAndId()
    {
        var parsed = UpdateCommandLine.Parse(["--updated", "1.2.0", "--update-id", Id]);

        Assert.Equal(UpdateCommandKind.Updated, parsed.Kind);
        Assert.Equal("1.2.0", parsed.UpdatedVersion);
        Assert.Equal(Id, parsed.UpdateId);
    }

    [Theory]
    [InlineData("--updated", "1.2.0")]
    [InlineData("--updated", "1.2.0", "--update-id", "not-an-id")]
    [InlineData("--updated", "1.2.0", "--other", Id)]
    public void Parse_MalformedUpdated_FallsBackToNormalStartup(params string[] args)
    {
        Assert.Equal(UpdateCommandKind.Normal, UpdateCommandLine.Parse(args).Kind);
    }

    [Fact]
    public void BuildArguments_RoundTripThroughParse()
    {
        var apply = UpdateCommandLine.Parse(UpdateCommandLine.BuildApplyArguments(77, @"C:\a b\app", @"D:\Agent Limit Checker"));
        var updated = UpdateCommandLine.Parse(UpdateCommandLine.BuildUpdatedArguments("2.0.1", Id));

        Assert.Equal((UpdateCommandKind.Apply, 77, @"C:\a b\app", @"D:\Agent Limit Checker"), (apply.Kind, apply.ProcessId, apply.StagingDirectory, apply.InstallDirectory));
        Assert.Equal((UpdateCommandKind.Updated, "2.0.1", Id), (updated.Kind, updated.UpdatedVersion, updated.UpdateId));
    }
}

public class UpdateUrlPolicyTests
{
    [Theory]
    [InlineData("https://ktysne.info/agent-limit-checker/archives/a.zip", false, true)]
    [InlineData("https://KTYSNE.info/a.zip", false, true)]
    [InlineData("https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.3/AgentLimitChecker-1.2.3-win-x64.zip", false, false)]
    [InlineData("http://ktysne.info/a.zip", false, false)]
    [InlineData("https://evil.example/a.zip", false, false)]
    [InlineData("https://ktysne.info.evil.example/a.zip", false, false)]
    [InlineData("http://localhost:8080/a.zip", false, false)]
    [InlineData("http://localhost:8080/a.zip", true, true)]
    [InlineData("https://127.0.0.1/a.zip", true, true)]
    [InlineData("http://192.168.0.2/a.zip", true, false)]
    [InlineData("ftp://localhost/a.zip", true, false)]
    [InlineData("http://user:pass@localhost/a.zip", true, false)]
    public void IsAllowedPackageUrl(string url, bool allowDevelopmentHosts, bool expected)
    {
        Assert.Equal(expected, UpdateUrlPolicy.IsAllowedPackageUrl(new Uri(url), allowDevelopmentHosts));
    }

    [Fact]
    public void IsAllowedPackageUrl_AcceptsVersionMatchedGitHubReleasePath()
    {
        var url = new Uri("https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.3/AgentLimitChecker-1.2.3-win-x64.zip");

        Assert.True(UpdateUrlPolicy.IsAllowedPackageUrl(url, "1.2.3", allowDevelopmentHosts: false));
        Assert.False(UpdateUrlPolicy.IsAllowedPackageUrl(url, "1.2.4", allowDevelopmentHosts: false));
    }

    [Fact]
    public void ResolveManifestSource_WithoutOverride_UsesDistributionSite()
    {
        var source = UpdateUrlPolicy.ResolveManifestSource(null);

        Assert.Equal(UpdateCheckDefaults.ManifestUri, source.ManifestUri);
        Assert.False(source.IsDevelopmentOverride);
    }

    [Fact]
    public void ResolveManifestSource_WithLocalhostOverride_EnablesDevelopmentHosts()
    {
        var source = UpdateUrlPolicy.ResolveManifestSource("http://localhost:8000/update.json");

        Assert.Equal(new Uri("http://localhost:8000/update.json"), source.ManifestUri);
        Assert.True(source.IsDevelopmentOverride);
    }

    [Theory]
    [InlineData("https://evil.example/update.json")]
    [InlineData("not a url")]
    [InlineData("   ")]
    public void ResolveManifestSource_WithNonLocalOverride_IgnoresIt(string value)
    {
        var source = UpdateUrlPolicy.ResolveManifestSource(value);

        Assert.Equal(UpdateCheckDefaults.ManifestUri, source.ManifestUri);
        Assert.False(source.IsDevelopmentOverride);
    }
}

public class UpdateSha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task ComputeHexAsync_MatchesKnownValues(string input, string expected)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));

        Assert.Equal(expected, await UpdateSha256.ComputeHexAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void Matches_IgnoresCase()
    {
        Assert.True(UpdateSha256.Matches("ABCDEF", "abcdef"));
        Assert.False(UpdateSha256.Matches("abcdef", "abcdee"));
        Assert.False(UpdateSha256.Matches(null, "abcdef"));
        Assert.False(UpdateSha256.Matches("", ""));
    }
}

public class UpdateRecordSerializerTests
{
    [Fact]
    public void PreparedRecord_RoundTrips()
    {
        var record = new UpdatePreparedRecord("1.2.0", new string('a', 64));

        Assert.Equal(record, UpdateRecordSerializer.ParsePrepared(UpdateRecordSerializer.Serialize(record)));
    }

    [Fact]
    public void CleanupRecord_RoundTrips()
    {
        var record = new UpdateCleanupRecord(@"C:\Apps\AgentLimitChecker", "1.2.0", [@"C:\Apps\AgentLimitChecker\AgentLimitChecker.exe.x.old", @"C:\Apps\AgentLimitChecker\manual.html.x.old"]);

        var parsed = UpdateRecordSerializer.ParseCleanup(UpdateRecordSerializer.Serialize(record));

        Assert.NotNull(parsed);
        Assert.Equal(record.InstallDirectory, parsed!.InstallDirectory);
        Assert.Equal(record.Version, parsed.Version);
        Assert.Equal(record.Backups, parsed.Backups);
    }

    [Fact]
    public void CleanupRecord_UsesDocumentedPropertyNames()
    {
        var json = UpdateRecordSerializer.Serialize(new UpdateCleanupRecord(@"C:\A", "1.0.0", []));

        Assert.Contains("\"installDir\"", json);
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"backups\"", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":\"1.0.0\"}")]
    [InlineData("{\"installDir\":\"C:\\\\A\",\"version\":\"1.0.0\",\"backups\":[null]}")]
    public void ParseCleanup_InvalidContent_ReturnsNull(string? json)
    {
        Assert.Null(UpdateRecordSerializer.ParseCleanup(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"version\":\"1.0.0\"}")]
    [InlineData("{\"version\":1}")]
    public void ParsePrepared_InvalidContent_ReturnsNull(string? json)
    {
        Assert.Null(UpdateRecordSerializer.ParsePrepared(json));
    }
}

public class UpdateLayoutTests
{
    [Theory]
    [InlineData("AgentLimitChecker.exe.0123456789abcdef0123456789abcdef.old", "0123456789abcdef0123456789abcdef")]
    [InlineData("manual.html.0123456789ABCDEF0123456789abcdef.old", null)]
    [InlineData("AgentLimitChecker.exe.old", null)]
    [InlineData(".0123456789abcdef0123456789abcdef.old", null)]
    [InlineData("AgentLimitChecker.exe", null)]
    public void TryGetBackupUpdateId(string fileName, string? expected)
    {
        Assert.Equal(expected, UpdateLayout.TryGetBackupUpdateId(fileName));
    }

    [Fact]
    public void IsSameOrUnder_DoesNotTreatSiblingWithSamePrefixAsChild()
    {
        var root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-layout", "update");

        Assert.True(UpdateLayout.IsSameOrUnder(Path.Combine(root, "x"), root));
        Assert.False(UpdateLayout.IsSameOrUnder(root + "-other", root));
    }
}

public class UpdateApplyArgumentsTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private static readonly string Root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-args-tests");
    private static readonly string UpdateRoot = Path.Combine(Root, "AgentLimitChecker", "update");
    private static readonly string Staging = Path.Combine(UpdateRoot, Id, "app");
    private static readonly string Install = Path.Combine(Root, "Apps", "AgentLimitChecker");

    private readonly FakeUpdateFileOperations _files = new();

    public UpdateApplyArgumentsTests()
    {
        _files.AddFile(Path.Combine(Staging, "AgentLimitChecker.exe"));
        _files.AddFile(Path.Combine(Install, "AgentLimitChecker.exe"));
    }

    private UpdateApplyArgumentsResult Validate(string staging, string install, int processId = 10) =>
        UpdateApplyArguments.Validate(new UpdateCommandLine(UpdateCommandKind.Apply, processId, staging, install), UpdateRoot, _files);

    [Fact]
    public void Validate_AcceptsStagingUnderUpdateRoot()
    {
        var result = Validate(Staging + Path.DirectorySeparatorChar, Install);

        Assert.Null(result.Error);
        Assert.Equal(Id, result.Context!.UpdateId);
        Assert.Equal(Staging, result.Context.StagingDirectory);
        Assert.Equal(Path.Combine(Install, "AgentLimitChecker.exe"), result.Context.InstallExecutablePath);
    }

    [Fact]
    public void Validate_RejectsRelativePaths()
    {
        Assert.NotNull(Validate(Path.Combine("update", Id, "app"), Install).Error);
    }

    [Fact]
    public void Validate_RejectsStagingOutsideUpdateRoot()
    {
        var elsewhere = Path.Combine(Root, "elsewhere", Id, "app");
        _files.AddFile(Path.Combine(elsewhere, "AgentLimitChecker.exe"));

        Assert.NotNull(Validate(elsewhere, Install).Error);
    }

    [Fact]
    public void Validate_RejectsStagingWhoseParentIsNotUpdateId()
    {
        var named = Path.Combine(UpdateRoot, "manual", "app");
        _files.AddFile(Path.Combine(named, "AgentLimitChecker.exe"));

        Assert.NotNull(Validate(named, Install).Error);
    }

    [Fact]
    public void Validate_RejectsInstallDirectoryOverlappingUpdateRoot()
    {
        var inside = Path.Combine(UpdateRoot, "install");
        _files.AddFile(Path.Combine(inside, "AgentLimitChecker.exe"));
        var ancestor = Path.Combine(Root, "AgentLimitChecker");
        _files.AddFile(Path.Combine(ancestor, "AgentLimitChecker.exe"));

        Assert.NotNull(Validate(Staging, inside).Error);
        Assert.NotNull(Validate(Staging, ancestor).Error);
    }

    [Fact]
    public void Validate_RejectsWhenExecutableIsMissing()
    {
        _files.Files.Remove(Path.Combine(Install, "AgentLimitChecker.exe"));

        Assert.NotNull(Validate(Staging, Install).Error);
    }

    [Fact]
    public void Validate_RejectsSamePath()
    {
        Assert.NotNull(Validate(Staging, Staging).Error);
    }

    [Fact]
    public void LogIdFor_UsesUpdateIdOnlyWhenWellFormed()
    {
        Assert.Equal(Id, UpdateApplyArguments.LogIdFor(new UpdateCommandLine(UpdateCommandKind.Apply, 1, Staging, Install)));
        Assert.Equal("unknown", UpdateApplyArguments.LogIdFor(new UpdateCommandLine(UpdateCommandKind.InvalidApply)));
        Assert.Equal("unknown", UpdateApplyArguments.LogIdFor(new UpdateCommandLine(UpdateCommandKind.Apply, 1, Path.Combine(Root, "..", "x", "app"), Install)));
    }
}

public class UpdateAutoApplyEligibilityTests
{
    private static readonly string Root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-eligibility-tests");
    private static readonly string Temp = Path.Combine(Root, "Temp");
    private static readonly string UpdateRoot = Path.Combine(Root, "AgentLimitChecker", "update");
    private static readonly string Install = Path.Combine(Root, "Apps", "AgentLimitChecker");

    private static readonly UpdateInfo Update = new(
        new Version(1, 2, 0), "1.2.0", new Uri("https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.0-win-x64.zip"), new string('a', 64));

    private static UpdateAutoApplyEnvironment Environment(
        string? processPath = null,
        bool isSingleFile = true,
        bool writable = true,
        bool allowDevelopmentHosts = false) =>
        new(processPath ?? Path.Combine(Install, "AgentLimitChecker.exe"), isSingleFile, Temp, UpdateRoot, allowDevelopmentHosts, _ => writable);

    [Fact]
    public void Evaluate_ReleaseBuildInWritableFolder_CanAutoApply()
    {
        var decision = UpdateAutoApplyEligibility.Evaluate(Update, Environment());

        Assert.True(decision.CanAutoApply);
        Assert.Equal(Install, decision.InstallDirectory);
    }

    [Fact]
    public void Evaluate_GitHubReleasePackage_CanAutoApply()
    {
        var githubRelease = Update with
        {
            DownloadUrl = new Uri("https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.0/AgentLimitChecker-1.2.0-win-x64.zip"),
        };

        Assert.True(UpdateAutoApplyEligibility.Evaluate(githubRelease, Environment()).CanAutoApply);
    }

    [Fact]
    public void Evaluate_WithoutSha256_CannotAutoApply()
    {
        Assert.False(UpdateAutoApplyEligibility.Evaluate(Update with { Sha256 = null }, Environment()).CanAutoApply);
    }

    [Fact]
    public void Evaluate_DevelopmentRun_CannotAutoApply()
    {
        Assert.False(UpdateAutoApplyEligibility.Evaluate(Update, Environment(isSingleFile: false)).CanAutoApply);
        Assert.False(UpdateAutoApplyEligibility.Evaluate(Update, Environment(processPath: Path.Combine(Install, "AgentLimitChecker.App.exe"))).CanAutoApply);
        Assert.False(UpdateAutoApplyEligibility.Evaluate(Update, Environment(processPath: null) with { ProcessPath = null }).CanAutoApply);
    }

    [Fact]
    public void Evaluate_RunningFromTempFolder_CannotAutoApply()
    {
        var decision = UpdateAutoApplyEligibility.Evaluate(
            Update, Environment(processPath: Path.Combine(Temp, "Temp1_AgentLimitChecker.zip", "AgentLimitChecker.exe")));

        Assert.False(decision.CanAutoApply);
        Assert.Contains("一時フォルダー", decision.UnavailableReason);
    }

    [Fact]
    public void Evaluate_InstallInsideUpdateRoot_CannotAutoApply()
    {
        Assert.False(UpdateAutoApplyEligibility.Evaluate(
            Update, Environment(processPath: Path.Combine(UpdateRoot, "x", "app", "AgentLimitChecker.exe"))).CanAutoApply);
    }

    [Fact]
    public void Evaluate_ReadOnlyInstall_CannotAutoApply()
    {
        Assert.False(UpdateAutoApplyEligibility.Evaluate(Update, Environment(writable: false)).CanAutoApply);
    }

    [Fact]
    public void Evaluate_LocalhostPackage_RequiresDevelopmentOverride()
    {
        var local = Update with { DownloadUrl = new Uri("http://localhost:8000/AgentLimitChecker.zip") };

        Assert.False(UpdateAutoApplyEligibility.Evaluate(local, Environment()).CanAutoApply);
        Assert.True(UpdateAutoApplyEligibility.Evaluate(local, Environment(allowDevelopmentHosts: true)).CanAutoApply);
    }
}
