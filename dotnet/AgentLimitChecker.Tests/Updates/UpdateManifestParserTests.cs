using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;
public class UpdateManifestParserTests
{
    private static string Manifest(string version, string url, int schema = 2) =>
        $$"""
        {
          "schema": {{schema}},
          "latest": {
            "version": "{{version}}",
            "url": "{{url}}",
            "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "releasedAt": "2026-09-04"
          }
        }
        """;

    [Fact]
    public void Parse_ValidManifest_ReturnsUpdateInfo()
    {
        var result = UpdateManifestParser.Parse(
            Manifest("1.2.3", "https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip"));

        Assert.Null(result.Error);
        Assert.NotNull(result.Info);
        Assert.Equal(new Version(1, 2, 3), result.Info!.Version);
        Assert.Equal("1.2.3", result.Info.VersionText);
        Assert.Equal(
            new Uri("https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip"),
            result.Info.DownloadUrl);
    }

    [Fact]
    public void Parse_ManifestWithSha256_ReturnsUpdateInfo()
    {
        var json = """
        {
          "schema": 2,
          "latest": {
            "version": "1.2.3",
            "url": "https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip",
            "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "releasedAt": "2026-09-04"
          }
        }
        """;

        var result = UpdateManifestParser.Parse(json);

        Assert.Null(result.Error);
        Assert.NotNull(result.Info);
        Assert.Equal(new Version(1, 2, 3), result.Info!.Version);
    }

    [Fact]
    public void Parse_UnsupportedSchema_Fails()
    {
        var result = UpdateManifestParser.Parse(
            Manifest("1.2.3", "https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip", schema: 1));

        AssertFailure(result);
    }

    [Fact]
    public void Parse_MissingSchema_Fails()
    {
        var json = """
        {
          "latest": { "version": "1.2.3", "url": "https://ktysne.info/agent-limit-checker/a.zip" }
        }
        """;

        AssertFailure(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_MissingLatest_Fails()
    {
        AssertFailure(UpdateManifestParser.Parse("""{ "schema": 2 }"""));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void Parse_InvalidVersion_Fails(string version)
    {
        var result = UpdateManifestParser.Parse(
            Manifest(version, "https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.0.0-win-x64.zip"));

        AssertFailure(result);
    }

    [Fact]
    public void Parse_HttpUrl_Fails()
    {
        var result = UpdateManifestParser.Parse(
            Manifest("1.2.3", "http://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip"));

        AssertFailure(result);
    }

    [Fact]
    public void Parse_OtherHost_Fails()
    {
        var result = UpdateManifestParser.Parse(
            Manifest("1.2.3", "https://evil.example.com/agent-limit-checker/AgentLimitChecker-1.2.3-win-x64.zip"));

        AssertFailure(result);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2.0")]
    [InlineData("2.1")]
    [InlineData("\"2\"")]
    public void Parse_SchemaMustBeIntegerTwo(string schema)
    {
        var json = $$"""
        {
          "schema": {{schema}},
          "latest": {
            "version": "1.2.3",
            "url": "https://ktysne.info/agent-limit-checker/a.zip",
            "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
          }
        }
        """;

        AssertFailure(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_GitHubReleaseUrlWithMatchingVersion_IsAccepted()
    {
        var result = UpdateManifestParser.Parse(Manifest(
            "1.2.3",
            "https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.3/AgentLimitChecker-1.2.3-win-x64.zip"));

        Assert.Null(result.Error);
        Assert.Equal("1.2.3", result.Info!.VersionText);
    }

    [Fact]
    public void Parse_ReleaseSiteGeneratedManifest_IsAccepted()
    {
        var manifestPath = Environment.GetEnvironmentVariable("AGENT_LIMIT_CHECKER_TEST_MANIFEST_PATH");
        var json = string.IsNullOrWhiteSpace(manifestPath)
            ? Manifest("4.0.0", "https://github.com/ktysne/agent-limit-checker/releases/download/v4.0.0/AgentLimitChecker-4.0.0-win-x64.zip")
            : File.ReadAllText(manifestPath);

        var result = UpdateManifestParser.Parse(json);

        Assert.Null(result.Error);
        Assert.Equal("4.0.0", result.Info!.VersionText);
        Assert.Equal("https://github.com/ktysne/agent-limit-checker/releases/download/v4.0.0/AgentLimitChecker-4.0.0-win-x64.zip",
            result.Info.DownloadUrl.ToString());
    }

    [Theory]
    [InlineData("https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.2/AgentLimitChecker-1.2.3-win-x64.zip")]
    [InlineData("https://github.com/ktysne/agent-limit-checker/releases/download/v1.2.3/AgentLimitChecker-1.2.2-win-x64.zip")]
    [InlineData("https://github.com/ktysne/agent-limit-checker/releases/download/1.2.3/AgentLimitChecker-1.2.3-win-x64.zip")]
    [InlineData("https://github.com/other/agent-limit-checker/releases/download/v1.2.3/AgentLimitChecker-1.2.3-win-x64.zip")]
    [InlineData("https://github.com/ktysne/other-releases/releases/download/v1.2.3/AgentLimitChecker-1.2.3-win-x64.zip")]
    public void Parse_GitHubReleaseUrlWithDifferentTagNameOrRepository_Fails(string url)
    {
        AssertFailure(UpdateManifestParser.Parse(Manifest("1.2.3", url)));
    }

    [Theory]
    [InlineData("https://user@ktysne.info/agent-limit-checker/a.zip")]
    [InlineData("https://ktysne.info:444/agent-limit-checker/a.zip")]
    [InlineData("https://ktysne.info.evil/agent-limit-checker/a.zip")]
    [InlineData("https://ktyſne.info/agent-limit-checker/a.zip")]
    [InlineData("https://github.com:444/ktysne/agent-limit-checker/releases/download/v1.2.3/AgentLimitChecker-1.2.3-win-x64.zip")]
    public void Parse_RawHostMustMatchWithoutUserInfoOrPort(string url)
    {
        AssertFailure(UpdateManifestParser.Parse(Manifest("1.2.3", url)));
    }

    [Fact]
    public void Parse_RelativeUrl_Fails()
    {
        var result = UpdateManifestParser.Parse(Manifest("1.2.3", "/agent-limit-checker/AgentLimitChecker-1.2.3-win-x64.zip"));

        AssertFailure(result);
    }

    [Fact]
    public void Parse_BrokenJson_Fails()
    {
        AssertFailure(UpdateManifestParser.Parse("""{ "schema": 2, "latest": """));
    }

    [Fact]
    public void Parse_EmptyText_Fails()
    {
        AssertFailure(UpdateManifestParser.Parse(""));
        AssertFailure(UpdateManifestParser.Parse(null));
    }

    private static string ManifestWithSha(string sha256Json) =>
        $$"""
        {
          "schema": 2,
          "latest": {
            "version": "1.2.3",
            "url": "https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip",
            "sha256": {{sha256Json}}
          }
        }
        """;

    [Fact]
    public void Parse_Sha256_IsReadAsLowercaseHex()
    {
        var sha = "0123456789ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef";

        var result = UpdateManifestParser.Parse(ManifestWithSha($"\"{sha}\""));

        Assert.Equal(sha.ToLowerInvariant(), result.Info!.Sha256);
    }

    [Theory]
    [InlineData("\"0123\"")]
    [InlineData("\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg\"")]
    [InlineData("\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0\"")]
    [InlineData("\"\"")]
    [InlineData("123")]
    [InlineData("null")]
    public void Parse_InvalidSha256_Fails(string sha256Json)
    {
        var result = UpdateManifestParser.Parse(ManifestWithSha(sha256Json));

        AssertFailure(result);
    }

    [Fact]
    public void Parse_MissingSha256_Fails()
    {
        var json = """
        {
          "schema": 2,
          "latest": {
            "version": "1.2.3",
            "url": "https://ktysne.info/agent-limit-checker/a.zip"
          }
        }
        """;

        AssertFailure(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_LocalhostUrl_IsAcceptedOnlyWithDevelopmentOverride()
    {
        var json = Manifest("1.2.3", "http://localhost:8000/AgentLimitChecker-1.2.3-win-x64.zip");

        AssertFailure(UpdateManifestParser.Parse(json));
        Assert.NotNull(UpdateManifestParser.Parse(json, allowDevelopmentHosts: true).Info);
    }

    private static void AssertFailure(UpdateManifestParseResult result)
    {
        Assert.Null(result.Info);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
