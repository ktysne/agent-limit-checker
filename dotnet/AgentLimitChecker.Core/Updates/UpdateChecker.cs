using System.Text.RegularExpressions;

namespace AgentLimitChecker.Core.Updates;
public enum UpdateCheckKind
{
    UpToDate,
    Available,
    Skipped,
    Failed,
}
public sealed record UpdateCheckResult(UpdateCheckKind Kind, UpdateInfo? Update, string? Error);
public sealed class UpdateChecker
{
    private static readonly Regex LeadingVersionPattern = new(
        @"^\d+(\.\d+){1,3}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private readonly Func<CancellationToken, Task<string>> _manifestFetcher;
    private readonly string? _currentVersionText;
    private readonly bool _allowDevelopmentHosts;
    public UpdateChecker(
        Func<CancellationToken, Task<string>> manifestFetcher,
        string? currentVersionText,
        bool allowDevelopmentHosts = false)
    {
        ArgumentNullException.ThrowIfNull(manifestFetcher);

        _manifestFetcher = manifestFetcher;
        _currentVersionText = currentVersionText;
        _allowDevelopmentHosts = allowDevelopmentHosts;
    }
    public async Task<UpdateCheckResult> CheckAsync(string? skippedVersion, CancellationToken cancellationToken)
    {
        var currentVersion = TryParseVersion(_currentVersionText);
        if (currentVersion is null)
        {
            return Failed("現在のバージョンを判別できませんでした。");
        }

        string manifest;
        try
        {
            manifest = await _manifestFetcher(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed($"最新バージョンの情報を取得できませんでした: {ex.Message}");
        }

        var parsed = UpdateManifestParser.Parse(manifest, _allowDevelopmentHosts);
        if (parsed.Info is null)
        {
            return Failed(parsed.Error ?? "最新バージョンの情報を解釈できませんでした。");
        }

        var latest = parsed.Info;
        if (Normalize(latest.Version) <= currentVersion)
        {
            return new UpdateCheckResult(UpdateCheckKind.UpToDate, latest, null);
        }

        if (IsSameVersion(skippedVersion, latest))
        {
            return new UpdateCheckResult(UpdateCheckKind.Skipped, latest, null);
        }

        return new UpdateCheckResult(UpdateCheckKind.Available, latest, null);
    }

    private static UpdateCheckResult Failed(string error) => new(UpdateCheckKind.Failed, null, error);
    private static bool IsSameVersion(string? skippedVersion, UpdateInfo latest)
    {
        if (string.IsNullOrWhiteSpace(skippedVersion))
        {
            return false;
        }

        var skipped = TryParseVersion(skippedVersion);
        if (skipped is not null)
        {
            return skipped == Normalize(latest.Version);
        }

        return string.Equals(skippedVersion.Trim(), latest.VersionText, StringComparison.OrdinalIgnoreCase);
    }
    public static Version? TryParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = LeadingVersionPattern.Match(text.Trim());
        if (!match.Success || !Version.TryParse(match.Value, out var version))
        {
            return null;
        }

        return Normalize(version);
    }
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
