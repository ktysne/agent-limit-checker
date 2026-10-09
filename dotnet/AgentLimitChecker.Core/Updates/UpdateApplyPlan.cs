namespace AgentLimitChecker.Core.Updates;
public sealed record UpdateApplyItem(
    string FileName,
    string SourcePath,
    string DestinationPath,
    bool ReplaceExisting,
    string BackupPath);

public sealed record UpdateApplyPlan(
    string StagingDirectory,
    string InstallDirectory,
    string UpdateId,
    IReadOnlyList<UpdateApplyItem> Items);
public sealed record UpdateApplyPlanResult(UpdateApplyPlan? Plan, string? Error);
public sealed record UpdateApplyResult(bool Succeeded, string? Error, bool RollbackSucceeded);
public static class UpdateApplyPlanner
{
    public static UpdateApplyPlanResult MakePlan(
        string stagingDirectory,
        string installDirectory,
        string updateId,
        IReadOnlyList<string> fileNames,
        IUpdateFileOperations operations)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentNullException.ThrowIfNull(operations);

        if (!UpdateLayout.IsUpdateId(updateId))
        {
            return new UpdateApplyPlanResult(null, "更新 ID が正しくありません。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<UpdateApplyItem>();
        foreach (var fileName in fileNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!UpdatePackageValidator.IsAllowedFileName(fileName) || !seen.Add(fileName))
            {
                return new UpdateApplyPlanResult(null, $"展開先のファイル名が正しくありません: {fileName}");
            }

            var source = Path.Combine(stagingDirectory, fileName);
            var destination = Path.Combine(installDirectory, fileName);
            var backup = UpdateLayout.BackupPath(destination, updateId);

            if (!operations.FileExists(source))
            {
                return new UpdateApplyPlanResult(null, $"展開先のファイルがありません: {source}");
            }
            if (operations.DirectoryExists(destination))
            {
                return new UpdateApplyPlanResult(null, $"既存の項目と種類が異なります: {destination}");
            }

            if (operations.PathExists(backup))
            {
                return new UpdateApplyPlanResult(null, $"退避先が既に存在します: {backup}");
            }

            items.Add(new UpdateApplyItem(fileName, source, destination, operations.FileExists(destination), backup));
        }

        if (!seen.Contains(UpdateLayout.ExecutableFileName))
        {
            return new UpdateApplyPlanResult(null, $"展開先に {UpdateLayout.ExecutableFileName} がありません。");
        }

        return new UpdateApplyPlanResult(new UpdateApplyPlan(stagingDirectory, installDirectory, updateId, items), null);
    }
    public static UpdateApplyResult Execute(UpdateApplyPlan plan, IUpdateFileOperations operations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(operations);

        var backedUp = new bool[plan.Items.Count];
        var placed = new bool[plan.Items.Count];
        string? error = null;
        var index = 0;
        for (; index < plan.Items.Count; index++)
        {
            var item = plan.Items[index];
            if (item.ReplaceExisting && !operations.CanOpenExclusively(item.DestinationPath))
            {
                error = $"ファイルを排他で開けません: {item.DestinationPath}";
                break;
            }

            var stagedPath = StagedCopyPath(item, plan.UpdateId);
            // 中断されても既存の実行ファイルを欠かさないよう、複製を完了してから退避する。
            if (!operations.CopyFile(item.SourcePath, stagedPath))
            {
                error = $"ファイルを複製できません: {item.DestinationPath}";
                break;
            }

            if (item.ReplaceExisting)
            {
                if (!operations.MoveFile(item.DestinationPath, item.BackupPath))
                {
                    operations.DeleteFile(stagedPath);
                    error = $"既存のファイルを退避できません: {item.DestinationPath}";
                    break;
                }

                backedUp[index] = true;
            }

            if (operations.PathExists(item.DestinationPath))
            {
                operations.DeleteFile(stagedPath);
                error = $"置き換え先に予期しないファイルがあります: {item.DestinationPath}";
                break;
            }

            if (!operations.MoveFile(stagedPath, item.DestinationPath))
            {
                operations.DeleteFile(stagedPath);
                error = $"ファイルを配置できません: {item.DestinationPath}";
                break;
            }

            placed[index] = true;
        }

        if (error is null)
        {
            return new UpdateApplyResult(true, null, true);
        }

        var rollbackSucceeded = true;
        for (var i = Math.Min(index, plan.Items.Count - 1); i >= 0; i--)
        {
            var item = plan.Items[i];
            if (placed[i] && !operations.DeleteFile(item.DestinationPath))
            {
                rollbackSucceeded = false;
                continue;
            }

            if (backedUp[i] && !operations.MoveFile(item.BackupPath, item.DestinationPath))
            {
                rollbackSucceeded = false;
            }
        }

        return new UpdateApplyResult(false, error, rollbackSucceeded);
    }

    private static string StagedCopyPath(UpdateApplyItem item, string updateId) => $"{item.DestinationPath}.{updateId}.new";
    public static UpdateApplyResult Rollback(UpdateApplyPlan plan, IUpdateFileOperations operations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(operations);

        var rollbackSucceeded = true;
        for (var i = plan.Items.Count - 1; i >= 0; i--)
        {
            var item = plan.Items[i];
            if (item.ReplaceExisting)
            {
                if (!operations.FileExists(item.BackupPath))
                {
                    rollbackSucceeded = false;
                    continue;
                }

                if (!operations.DeleteFile(item.DestinationPath) || !operations.MoveFile(item.BackupPath, item.DestinationPath))
                {
                    rollbackSucceeded = false;
                }

                continue;
            }

            if (!operations.DeleteFile(item.DestinationPath))
            {
                rollbackSucceeded = false;
            }
        }

        return new UpdateApplyResult(rollbackSucceeded, rollbackSucceeded ? null : "元のファイルへ戻せない項目があります。", rollbackSucceeded);
    }
}
