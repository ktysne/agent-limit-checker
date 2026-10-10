namespace AgentLimitChecker.Core.Updates;
public sealed record UpdateCleanupRecordEntry(string RecordPath, string UpdateId, UpdateCleanupRecord? Record);
public sealed record UpdateFolderEntry(string Path, bool IsReparsePoint, bool HasPreparedRecord, UpdatePreparedRecord? Prepared);
public sealed record UpdateCleanupRecordAction(string RecordPath, UpdateCleanupRecord Record, IReadOnlyList<string> BackupsToDelete);

public sealed record UpdateCleanupPlan(
    IReadOnlyList<UpdateCleanupRecordAction> RecordActions,
    IReadOnlyList<string> DirectoriesToDelete)
{
    public static readonly UpdateCleanupPlan Empty = new([], []);
}
public static class UpdateCleanupSelector
{
    public static UpdateCleanupPlan Select(
        string installDirectory,
        Version runningVersion,
        string updateRootDirectory,
        IReadOnlyList<UpdateCleanupRecordEntry> records,
        IReadOnlyList<UpdateFolderEntry> folders,
        IUpdateFileOperations probe)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(probe);

        var actions = new List<UpdateCleanupRecordAction>();
        foreach (var entry in records)
        {
            if (SelectRecord(installDirectory, runningVersion, entry, probe) is { } action)
            {
                actions.Add(action);
            }
        }

        var directories = folders
            .Where(folder => IsDeletableFolder(updateRootDirectory, runningVersion, folder))
            .Select(folder => folder.Path)
            .ToList();

        return new UpdateCleanupPlan(actions, directories);
    }

    private static UpdateCleanupRecordAction? SelectRecord(
        string installDirectory,
        Version runningVersion,
        UpdateCleanupRecordEntry entry,
        IUpdateFileOperations probe)
    {
        if (entry.Record is not { } record || !UpdateLayout.IsUpdateId(entry.UpdateId))
        {
            return null;
        }
        var recordInstall = UpdateLayout.NormalizeFullPath(record.InstallDirectory);
        var recordVersion = UpdateChecker.TryParseVersion(record.Version);
        if (recordInstall is null || !UpdateLayout.PathEquals(recordInstall, installDirectory)
            || recordVersion is null || recordVersion > runningVersion)
        {
            return null;
        }

        var backups = record.Backups
            .Where(backup => IsDeletableBackup(installDirectory, entry.UpdateId, backup, probe))
            .ToList();
        return new UpdateCleanupRecordAction(entry.RecordPath, record, backups);
    }

    private static bool IsDeletableBackup(string installDirectory, string updateId, string backup, IUpdateFileOperations probe)
    {
        var full = UpdateLayout.NormalizeFullPath(backup);
        if (full is null)
        {
            return false;
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !UpdateLayout.PathEquals(parent, installDirectory))
        {
            return false;
        }

        var backupId = UpdateLayout.TryGetBackupUpdateId(Path.GetFileName(full));
        if (backupId is null || !string.Equals(backupId, updateId, StringComparison.Ordinal))
        {
            return false;
        }

        return probe.FileExists(full) && !probe.IsReparsePoint(full);
    }

    private static bool IsDeletableFolder(string updateRootDirectory, Version runningVersion, UpdateFolderEntry folder)
    {
        var full = UpdateLayout.NormalizeFullPath(folder.Path);
        var parent = full is null ? null : Path.GetDirectoryName(full);
        if (full is null || parent is null || !UpdateLayout.PathEquals(parent, updateRootDirectory)
            || !UpdateLayout.IsUpdateId(Path.GetFileName(full)) || folder.IsReparsePoint)
        {
            return false;
        }

        if (!folder.HasPreparedRecord)
        {
            return true;
        }
        var preparedVersion = UpdateChecker.TryParseVersion(folder.Prepared?.Version);
        return preparedVersion is not null && preparedVersion <= runningVersion;
    }
}
