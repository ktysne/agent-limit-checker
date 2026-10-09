namespace AgentLimitChecker.Core.Updates;
public static class UpdateDownloadRedirectPolicy
{
    public const int MaxRedirectCount = 5;
    public static bool IsRedirectStatus(int statusCode) =>
        statusCode is 301 or 302 or 303 or 307 or 308;
    public static bool TryGetTarget(
        int statusCode,
        int redirectsFollowed,
        string? location,
        out Uri? target)
    {
        target = null;
        if (!IsRedirectStatus(statusCode)
            || redirectsFollowed < 0
            || redirectsFollowed >= MaxRedirectCount
            || !IsAllowedLocation(location))
        {
            return false;
        }

        target = CreateRequestUri(location!);
        return true;
    }
    public static bool IsAllowedLocation(string? location)
    {
        if (string.IsNullOrEmpty(location)
            || !location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var character in location)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\' || character == '#')
            {
                return false;
            }
        }

        var remainder = location[8..];
        var authorityEnd = remainder.IndexOfAny(['/', '?', '#']);
        if (authorityEnd < 0)
        {
            authorityEnd = remainder.Length;
        }

        var authority = remainder[..authorityEnd];
        if (authority.Contains('@'))
        {
            return false;
        }

        var colon = authority.IndexOf(':');
        var hostname = colon < 0 ? authority : authority[..colon];
        if (colon >= 0 && !IsHttpsPort(authority[(colon + 1)..]))
        {
            return false;
        }

        var normalizedHost = NormalizeAsciiHostname(hostname);
        if (normalizedHost is null || !IsValidHostname(normalizedHost))
        {
            return false;
        }

        return string.Equals(normalizedHost, "github.com", StringComparison.Ordinal)
            || (normalizedHost.EndsWith(".githubusercontent.com", StringComparison.Ordinal)
                && normalizedHost.Length > ".githubusercontent.com".Length);
    }
    public static Uri CreateRequestUri(string absoluteUrl)
    {
        ArgumentNullException.ThrowIfNull(absoluteUrl);
        return new Uri(absoluteUrl, UriKind.Absolute);
    }

    private static string? NormalizeAsciiHostname(string hostname)
    {
        if (hostname.Length == 0 || hostname.Any(character => character > 0x7f))
        {
            return null;
        }

        return hostname.ToLowerInvariant();
    }

    private static bool IsValidHostname(string hostname)
    {
        var labels = hostname.Split('.');
        return labels.Length > 1 && labels.All(label =>
            label.Length > 0
            && label[0] != '-'
            && label[^1] != '-'
            && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static bool IsHttpsPort(string portText) =>
        portText.Length > 0
        && portText.All(char.IsAsciiDigit)
        && int.TryParse(portText, out var port)
        && port == 443;
}
