namespace AgentLimitChecker.Core.Updates;
public static class UpdateCheckDefaults
{
    public static readonly Uri ManifestUri = new("https://ktysne.info/agent-limit-checker/update-v2.json");
    public const string AllowedDownloadHost = "ktysne.info";

    public const string AllowedReleaseHost = "github.com";
    public const string ManifestUrlEnvironmentVariable = "AGENT_LIMIT_CHECKER_UPDATE_MANIFEST_URL";
    public const string DistributionPage = "https://ktysne.info/agent-limit-checker/";
}
