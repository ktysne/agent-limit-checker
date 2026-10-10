using System.Text.RegularExpressions;

namespace AgentLimitChecker.Core.Updates;
public static class UpdateLayout
{
    public const string ExecutableFileName = "AgentLimitChecker.exe";
    public const string UpdateDirectoryName = "update";
    public const string StagingDirectoryName = "app";

    public const string PreparedRecordFileName = "prepared.json";
    public const string ApplierMutexName = @"Local\AgentLimitChecker.UpdateApplier";
    public const string PartialSuffix = ".partial";

    private const string BackupSuffix = ".old";

    private static readonly Regex UpdateIdPattern = new(
        "^[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static string NewUpdateId() => Guid.NewGuid().ToString("N");
    public static bool IsUpdateId(string? text) => text is not null && UpdateIdPattern.IsMatch(text);

    public static string PackageFileName(string versionText) => $"AgentLimitChecker-{versionText}-win-x64.zip";

    public static string CleanupRecordFileName(string updateId) => $"cleanup-{updateId}.json";

    public static string ApplyLogFileName(string updateId) => $"apply-{updateId}.log";

    public static string StartupEventName(string updateId) => $@"Local\AgentLimitChecker.Updated.{updateId}";
    public static string BackupPath(string destinationPath, string updateId) =>
        destinationPath + "." + updateId + BackupSuffix;
    public static string? TryGetBackupUpdateId(string fileName)
    {
        if (!fileName.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var withoutSuffix = fileName[..^BackupSuffix.Length];
        var dot = withoutSuffix.LastIndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        var id = withoutSuffix[(dot + 1)..];
        return IsUpdateId(id) ? id : null;
    }
    public static string? NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    public static bool IsSameOrUnder(string path, string directory)
    {
        if (PathEquals(path, directory))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    public static bool Overlaps(string left, string right) => IsSameOrUnder(left, right) || IsSameOrUnder(right, left);
}
