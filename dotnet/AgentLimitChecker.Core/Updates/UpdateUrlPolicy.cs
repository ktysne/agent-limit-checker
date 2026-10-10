namespace AgentLimitChecker.Core.Updates;
public static class UpdateUrlPolicy
{
    public static bool IsAllowedPackageUrl(Uri url, bool allowDevelopmentHosts)
        => IsAllowedPackageUrl(url, versionText: null, allowDevelopmentHosts: allowDevelopmentHosts);
    public static bool IsAllowedPackageUrl(Uri url, string? versionText, bool allowDevelopmentHosts)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri)
        {
            return false;
        }

        var originalUrl = url.OriginalString;
        if (TryGetHttpsAuthority(originalUrl, out var authority, out var suffix))
        {
            if (IsSameHost(authority, UpdateCheckDefaults.AllowedDownloadHost))
            {
                return true;
            }

            if (versionText is not null
                && IsSameHost(authority, UpdateCheckDefaults.AllowedReleaseHost)
                && string.Equals(suffix, GetReleasePath(versionText), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return allowDevelopmentHosts && IsDevelopmentUrl(url);
    }
    public static bool IsDevelopmentUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri)
        {
            return false;
        }

        var originalUrl = url.OriginalString;
        var prefixLength = originalUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
            : originalUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
            : 0;
        if (prefixLength == 0 || !TryGetAuthority(originalUrl, prefixLength, out var authority, out _)
            || authority.Contains('@'))
        {
            return false;
        }

        var colon = authority.IndexOf(':');
        var hostname = colon < 0 ? authority : authority[..colon];
        if (colon >= 0 && !IsValidPort(authority[(colon + 1)..]))
        {
            return false;
        }

        return IsSameHost(hostname, "localhost") || string.Equals(hostname, "127.0.0.1", StringComparison.Ordinal);
    }

    private static bool TryGetHttpsAuthority(string value, out string authority, out string suffix)
    {
        authority = string.Empty;
        suffix = string.Empty;
        return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && TryGetAuthority(value, 8, out authority, out suffix);
    }

    private static bool TryGetAuthority(string value, int start, out string authority, out string suffix)
    {
        var end = value.Length;
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] is '/' or '?' or '#')
            {
                end = index;
                break;
            }
        }

        authority = value[start..end];
        suffix = value[end..];
        return authority.Length > 0;
    }

    private static bool IsSameHost(string authority, string host) =>
        authority.All(char.IsAscii)
        && string.Equals(authority, host, StringComparison.OrdinalIgnoreCase);

    private static string GetReleasePath(string versionText) =>
        $"/ktysne/agent-limit-checker/releases/download/v{versionText}/AgentLimitChecker-{versionText}-win-x64.zip";

    private static bool IsValidPort(string text)
    {
        if (text.Length == 0 || !int.TryParse(text, out var port))
        {
            return false;
        }

        return port is >= 0 and <= 65535;
    }
    public static UpdateManifestSource ResolveManifestSource(string? environmentValue)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue)
            && Uri.TryCreate(environmentValue.Trim(), UriKind.Absolute, out var overridden)
            && IsDevelopmentUrl(overridden))
        {
            return new UpdateManifestSource(overridden, IsDevelopmentOverride: true);
        }

        return new UpdateManifestSource(UpdateCheckDefaults.ManifestUri, IsDevelopmentOverride: false);
    }
}
public sealed record UpdateManifestSource(Uri ManifestUri, bool IsDevelopmentOverride);
