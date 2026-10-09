namespace AgentLimitChecker.Core.Updates;
public sealed record UpdateInfo(Version Version, string VersionText, Uri DownloadUrl, string? Sha256 = null);
