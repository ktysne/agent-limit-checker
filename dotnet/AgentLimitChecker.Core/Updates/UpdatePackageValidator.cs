namespace AgentLimitChecker.Core.Updates;
public sealed record UpdatePackageEntry(string Name, long Length);
public sealed record UpdatePackageValidationResult(IReadOnlyList<string> FileNames, string? Error)
{
    public bool IsValid => Error is null;

    public static UpdatePackageValidationResult Rejected(string error) => new([], error);
}
public static class UpdatePackageValidator
{
    public const int MaxEntryCount = 64;

    public const long MaxTotalExtractedBytes = 1L * 1024 * 1024 * 1024;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static UpdatePackageValidationResult Validate(IReadOnlyList<UpdatePackageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return UpdatePackageValidationResult.Rejected("zip が空です。");
        }

        if (entries.Count > MaxEntryCount)
        {
            return UpdatePackageValidationResult.Rejected($"zip のエントリーが多すぎます: {entries.Count} 件");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            if (!IsAllowedFileName(entry.Name))
            {
                return UpdatePackageValidationResult.Rejected($"zip に許可しない名前のエントリーがあります: {entry.Name}");
            }

            if (!names.Add(entry.Name))
            {
                return UpdatePackageValidationResult.Rejected($"zip に同じ名前のエントリーが重複しています: {entry.Name}");
            }

            if (entry.Length < 0 || entry.Length > MaxTotalExtractedBytes - totalBytes)
            {
                return UpdatePackageValidationResult.Rejected("zip の展開後の合計サイズが上限を超えます。");
            }

            totalBytes += entry.Length;
        }

        if (!names.Contains(UpdateLayout.ExecutableFileName))
        {
            return UpdatePackageValidationResult.Rejected($"zip の直下に {UpdateLayout.ExecutableFileName} がありません。");
        }

        return new UpdatePackageValidationResult(entries.Select(entry => entry.Name).ToList(), null);
    }
    public static bool IsAllowedFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return false;
        }

        if (name.IndexOfAny(ForbiddenCharacters) >= 0 || name.Any(char.IsControl))
        {
            return false;
        }
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return false;
        }
        if (name.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dot = name.IndexOf('.');
        var baseName = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');
        return !ReservedDeviceNames.Contains(baseName, StringComparer.OrdinalIgnoreCase);
    }
}
