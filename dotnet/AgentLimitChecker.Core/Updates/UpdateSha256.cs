using System.Security.Cryptography;

namespace AgentLimitChecker.Core.Updates;
public static class UpdateSha256
{
    public static async Task<string> ComputeHexAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<string> ComputeFileHexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return await ComputeHexAsync(stream, cancellationToken).ConfigureAwait(false);
    }
    public static bool Matches(string? actual, string? expected) =>
        !string.IsNullOrEmpty(actual)
        && !string.IsNullOrEmpty(expected)
        && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
