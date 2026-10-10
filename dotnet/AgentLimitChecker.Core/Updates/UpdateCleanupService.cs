namespace AgentLimitChecker.Core.Updates;
public sealed class UpdateCleanupService
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(2);

    private const string CleanupRecordPrefix = "cleanup-";
    private const string CleanupRecordExtension = ".json";

    private readonly IUpdateFileOperations _files;
    private readonly IUpdateProcessOperations _processes;
    private readonly Action<string> _log;

    public UpdateCleanupService(IUpdateFileOperations files, IUpdateProcessOperations processes, Action<string> log)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }
    public void Run(string? installDirectory, string? runningVersionText, string updateRootDirectory)
    {
        var install = UpdateLayout.NormalizeFullPath(installDirectory);
        var runningVersion = UpdateChecker.TryParseVersion(runningVersionText);
        var updateRoot = UpdateLayout.NormalizeFullPath(updateRootDirectory);
        if (install is null || runningVersion is null || updateRoot is null
            || !_files.DirectoryExists(updateRoot) || _files.IsReparsePoint(updateRoot))
        {
            return;
        }

        using var applierLock = _processes.TryAcquireApplierLock(LockTimeout);
        if (applierLock is null)
        {
            _log("後始末: 別の更新処理が実行中のため、今回は何も削除しません。");
            return;
        }

        var listing = _files.ListDirectory(updateRoot);
        if (listing is null)
        {
            return;
        }

        var records = listing.Files
            .Select(name => ReadRecordEntry(updateRoot, name))
            .OfType<UpdateCleanupRecordEntry>()
            .ToList();
        var folders = listing.Directories
            .Select(name => ReadFolderEntry(Path.Combine(updateRoot, name)))
            .ToList();

        var plan = UpdateCleanupSelector.Select(install, runningVersion, updateRoot, records, folders, _files);
        Execute(plan);
    }
    public void Execute(UpdateCleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var action in plan.RecordActions)
        {
            var remaining = new List<string>();
            foreach (var backup in action.BackupsToDelete)
            {
                if (_files.DeleteFile(backup))
                {
                    _log($"後始末: 退避ファイルを削除しました: {backup}");
                }
                else
                {
                    _log($"後始末: 退避ファイルを削除できませんでした: {backup}");
                    remaining.Add(backup);
                }
            }
            if (remaining.Count > 0)
            {
                if (!_files.ReplaceFileAtomically(
                        action.RecordPath,
                        UpdateRecordSerializer.Serialize(action.Record with { Backups = remaining })))
                {
                    _log($"後始末: 残った退避ファイルの記録を書き直せませんでした: {action.RecordPath}");
                }

                continue;
            }

            if (!_files.DeleteFile(action.RecordPath))
            {
                _log($"後始末: 記録を削除できませんでした: {action.RecordPath}");
            }
        }

        foreach (var directory in plan.DirectoriesToDelete)
        {
            _log(_files.DeleteDirectoryTree(directory)
                ? $"後始末: 作業フォルダーを削除しました: {directory}"
                : $"後始末: 作業フォルダーを削除できませんでした: {directory}");
        }
    }

    private UpdateCleanupRecordEntry? ReadRecordEntry(string updateRoot, string fileName)
    {
        if (!fileName.StartsWith(CleanupRecordPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(CleanupRecordExtension, StringComparison.Ordinal))
        {
            return null;
        }

        var id = fileName[CleanupRecordPrefix.Length..^CleanupRecordExtension.Length];
        if (!UpdateLayout.IsUpdateId(id))
        {
            return null;
        }

        var path = Path.Combine(updateRoot, fileName);
        var record = UpdateRecordSerializer.ParseCleanup(_files.ReadAllText(path));
        if (record is null)
        {
            _log($"後始末: 解釈できない記録を残します: {path}");
        }

        return new UpdateCleanupRecordEntry(path, id, record);
    }

    private UpdateFolderEntry ReadFolderEntry(string path)
    {
        var isReparsePoint = _files.IsReparsePoint(path);
        if (isReparsePoint)
        {
            return new UpdateFolderEntry(path, true, false, null);
        }

        var preparedPath = Path.Combine(path, UpdateLayout.PreparedRecordFileName);
        var hasPrepared = _files.PathExists(preparedPath);
        var prepared = hasPrepared ? UpdateRecordSerializer.ParsePrepared(_files.ReadAllText(preparedPath)) : null;
        return new UpdateFolderEntry(path, false, hasPrepared, prepared);
    }
}
