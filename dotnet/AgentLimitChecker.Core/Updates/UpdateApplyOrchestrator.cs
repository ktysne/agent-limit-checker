namespace AgentLimitChecker.Core.Updates;
public sealed class UpdateApplyOrchestrator
{
    public static readonly TimeSpan OldProcessTimeout = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan ExclusiveAccessTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ExclusiveAccessRetryInterval = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan NewVersionSurvivalTimeout = TimeSpan.FromSeconds(15);

    public const int SuccessExitCode = 0;

    public const int FailureExitCode = 1;

    private const string DialogProductName = "Agent Limit Checker";

    private const string OtherInstanceHint = "Agent Limit Checker が他にも起動していないか確認してください。";

    private readonly IUpdateFileOperations _files;
    private readonly IUpdateProcessOperations _processes;
    private readonly IUpdateApplyReporter _reporter;
    private readonly string _updateRootDirectory;
    private readonly string _versionText;
    public UpdateApplyOrchestrator(
        IUpdateFileOperations files,
        IUpdateProcessOperations processes,
        IUpdateApplyReporter reporter,
        string updateRootDirectory,
        string versionText)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _updateRootDirectory = updateRootDirectory;
        _versionText = versionText;
    }

    public int Run(UpdateCommandLine commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        _reporter.Log($"更新の適用を開始しました: 版={_versionText}");

        using var applierLock = _processes.TryAcquireApplierLock(ExclusiveAccessTimeout);
        if (applierLock is null)
        {
            _reporter.Log("別の更新処理が実行中のため適用を中止しました。");
            _reporter.ShowError("別の更新処理が実行中のため、この更新を中止しました。処理が終わってから、もう一度更新してください。");
            return FailureExitCode;
        }

        var validated = UpdateApplyArguments.Validate(commandLine, _updateRootDirectory, _files);
        if (validated.Context is not { } context)
        {
            _reporter.Log($"引数を検証できませんでした: {validated.Error}");
            _reporter.ShowError("更新の指定が正しくないため、適用を中止しました。もう一度 Agent Limit Checker から更新してください。");
            return FailureExitCode;
        }

        if (!_processes.WaitForInstanceProcessesToExit(context.InstallExecutablePath, OldProcessTimeout))
        {
            _reporter.Log("旧版のプロセスが 120 秒以内に終了しませんでした。");
            _reporter.ShowError(
                $"{DialogProductName} が 120 秒以内に終了しなかったため、更新を中止しました。{OtherInstanceHint}"
                + " すべて終了してから、もう一度更新してください。");
            return FailureExitCode;
        }

        var listing = _files.ListDirectory(context.StagingDirectory);
        if (listing is null || listing.Directories.Count > 0)
        {
            return ReportRestoredFailure(context, "展開先の内容が想定と異なります。");
        }

        var cleanupRecordPath = Path.Combine(_updateRootDirectory, UpdateLayout.CleanupRecordFileName(context.UpdateId));
        if (_files.PathExists(cleanupRecordPath))
        {
            return ReportRestoredFailure(context, "同じ更新 ID の後始末記録が既にあります。");
        }

        var planned = UpdateApplyPlanner.MakePlan(
            context.StagingDirectory, context.InstallDirectory, context.UpdateId, listing.Files, _files);
        if (planned.Plan is not { } plan)
        {
            return ReportRestoredFailure(context, planned.Error ?? "更新の計画を作れませんでした。");
        }

        if (!WaitForExclusiveAccess(plan))
        {
            return ReportRestoredFailure(context, $"更新対象のファイルを 30 秒待っても排他で開けませんでした。{OtherInstanceHint}");
        }

        var applied = UpdateApplyPlanner.Execute(plan, _files);
        if (!applied.Succeeded)
        {
            var reason = $"{applied.Error} {OtherInstanceHint}";
            return applied.RollbackSucceeded
                ? ReportRestoredFailure(context, reason)
                : ReportBrokenFailure(context, reason);
        }

        var record = new UpdateCleanupRecord(
            context.InstallDirectory,
            _versionText,
            plan.Items.Where(item => item.ReplaceExisting).Select(item => item.BackupPath).ToList());
        if (!_files.WriteNewFileAtomically(cleanupRecordPath, UpdateRecordSerializer.Serialize(record)))
        {
            return RollbackAndReport(plan, context, cleanupRecordPath, "後始末記録を書き込めませんでした。");
        }

        _reporter.Log($"ファイルを置き換えました: 版={_versionText}、インストール先={context.InstallDirectory}、ファイル数={plan.Items.Count}");

        var launched = _processes.LaunchAndWaitForStartup(
            context.InstallExecutablePath,
            UpdateCommandLine.BuildUpdatedArguments(_versionText, context.UpdateId),
            context.InstallDirectory,
            UpdateLayout.StartupEventName(context.UpdateId),
            NewVersionSurvivalTimeout);

        switch (launched.Outcome)
        {
            case UpdateLaunchOutcome.Signaled:
                _reporter.Log("新しいバージョンが起動を知らせました。");
                return SuccessExitCode;

            case UpdateLaunchOutcome.Survived:
                _reporter.Log("新しいバージョンが 15 秒間動作しました。");
                return SuccessExitCode;

            case UpdateLaunchOutcome.LaunchFailed:
                _reporter.Log($"新しいバージョンを起動できませんでした: {launched.Detail}");
                return RollbackAndReport(plan, context, cleanupRecordPath, "新しいバージョンを起動できませんでした。");

            default:
                _reporter.Log($"新しいバージョンが起動を知らせずに終了しました: {launched.Detail}");
                return RollbackAndReport(plan, context, cleanupRecordPath, "新しいバージョンが起動の途中で終了しました。");
        }
    }

    private bool WaitForExclusiveAccess(UpdateApplyPlan plan)
    {
        var deadline = _processes.Elapsed + ExclusiveAccessTimeout;
        while (true)
        {
            if (plan.Items.All(item => _files.CanOpenExclusively(item.DestinationPath)))
            {
                return true;
            }

            var remaining = deadline - _processes.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            _processes.Sleep(remaining < ExclusiveAccessRetryInterval ? remaining : ExclusiveAccessRetryInterval);
        }
    }

    private int RollbackAndReport(UpdateApplyPlan plan, UpdateApplyContext context, string cleanupRecordPath, string reason)
    {
        if (!_files.DeleteFile(cleanupRecordPath))
        {
            _reporter.Log($"後始末記録を削除できませんでした: {cleanupRecordPath}");
        }

        var rollback = WaitForExclusiveAccess(plan)
            ? UpdateApplyPlanner.Rollback(plan, _files)
            : new UpdateApplyResult(false, "ファイルを排他で開けませんでした。", false);
        _reporter.Log($"更新をロールバックしました: 理由={reason}、結果={(rollback.Succeeded ? "成功" : "失敗")}");

        return rollback.Succeeded
            ? ReportRestoredFailure(context, reason)
            : ReportBrokenFailure(context, reason);
    }

    private int ReportRestoredFailure(UpdateApplyContext context, string reason)
    {
        _reporter.Log($"更新を中止しました: {reason}");
        var previousStarted = LaunchPrevious(context);
        var started = previousStarted
            ? "元のバージョンを起動しました。"
            : "元のバージョンを起動できませんでした。インストール先から起動してください。";
        _reporter.ShowError(
            $"{DialogProductName} をバージョン {_versionText} に更新できませんでした。元のファイルへ戻しました。{started}"
            + $"{Environment.NewLine}{Environment.NewLine}理由: {reason}"
            + $"{Environment.NewLine}{Environment.NewLine}もう一度更新するか、配布ページ ({UpdateCheckDefaults.DistributionPage}) から zip を入手して展開してください。"
            + $"詳しい内容は次のフォルダーのログに記録しました。{Environment.NewLine}{_updateRootDirectory}");
        return FailureExitCode;
    }

    private int ReportBrokenFailure(UpdateApplyContext context, string reason)
    {
        _reporter.Log($"更新に失敗し、元に戻せないファイルがあります: {reason}");
        LaunchPrevious(context);
        _reporter.ShowError(
            $"{DialogProductName} をバージョン {_versionText} に更新できず、元に戻せないファイルがあります。"
            + $"{Environment.NewLine}{Environment.NewLine}理由: {reason}"
            + $"{Environment.NewLine}{Environment.NewLine}配布ページ ({UpdateCheckDefaults.DistributionPage}) から zip を入手し、次のフォルダーへ上書きで展開してください。"
            + $"{Environment.NewLine}{context.InstallDirectory}"
            + $"{Environment.NewLine}{Environment.NewLine}詳しい内容は次のフォルダーのログに記録しました。{Environment.NewLine}{_updateRootDirectory}");
        return FailureExitCode;
    }

    private bool LaunchPrevious(UpdateApplyContext context)
    {
        if (_processes.LaunchDetached(context.InstallExecutablePath, context.InstallDirectory))
        {
            return true;
        }

        _reporter.Log("元のバージョンを起動できませんでした。");
        return false;
    }
}
