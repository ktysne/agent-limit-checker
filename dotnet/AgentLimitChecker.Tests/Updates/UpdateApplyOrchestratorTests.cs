using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public class UpdateApplyOrchestratorTests
{
    private const string UpdateId = "0123456789abcdef0123456789abcdef";
    private const string Version = "1.2.0";

    private static readonly string Root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-orchestrator-tests");
    private static readonly string UpdateRoot = Path.Combine(Root, "AgentLimitChecker", "update");
    private static readonly string Staging = Path.Combine(UpdateRoot, UpdateId, "app");
    private static readonly string Install = Path.Combine(Root, "Apps", "AgentLimitChecker");
    private static readonly string InstallExe = Path.Combine(Install, "AgentLimitChecker.exe");
    private static readonly string BackupExe = InstallExe + "." + UpdateId + ".old";
    private static readonly string CleanupRecord = Path.Combine(UpdateRoot, $"cleanup-{UpdateId}.json");

    private readonly FakeUpdateFileOperations _files = new();
    private readonly FakeUpdateProcessOperations _processes = new();
    private readonly RecordingApplyReporter _reporter = new();

    public UpdateApplyOrchestratorTests()
    {
        _files.AddFile(Path.Combine(Staging, "AgentLimitChecker.exe"), "new-exe");
        _files.AddFile(Path.Combine(Staging, "manual.html"), "new-manual");
        _files.AddFile(InstallExe, "old-exe");
        _files.AddFile(Path.Combine(Install, "manual.html"), "old-manual");
    }

    private static UpdateCommandLine Command(int processId = 4242, string? staging = null, string? install = null) =>
        new(UpdateCommandKind.Apply, processId, staging ?? Staging, install ?? Install);

    private int Run(UpdateCommandLine? command = null) =>
        new UpdateApplyOrchestrator(_files, _processes, _reporter, UpdateRoot, Version).Run(command ?? Command());

    private void AssertOriginalFilesRestored()
    {
        Assert.Equal("old-exe", _files.Files[InstallExe]);
        Assert.Equal("old-manual", _files.Files[Path.Combine(Install, "manual.html")]);
        Assert.False(_files.FileExists(BackupExe));
    }

    [Fact]
    public void Run_WhenNewVersionSignals_SucceedsAndKeepsCleanupRecord()
    {
        _processes.LaunchResult = new UpdateLaunchResult(UpdateLaunchOutcome.Signaled);

        var exitCode = Run();

        Assert.Equal(UpdateApplyOrchestrator.SuccessExitCode, exitCode);
        Assert.Equal("new-exe", _files.Files[InstallExe]);
        Assert.Equal("old-exe", _files.Files[BackupExe]);
        Assert.Empty(_reporter.Errors);

        var record = UpdateRecordSerializer.ParseCleanup(_files.Files[CleanupRecord]);
        Assert.NotNull(record);
        Assert.Equal(Install, record!.InstallDirectory);
        Assert.Equal(Version, record.Version);
        Assert.Contains(BackupExe, record.Backups);
    }

    [Fact]
    public void Run_LaunchesNewVersionFromInstallDirectoryWithUpdatedArguments()
    {
        Run();

        Assert.Contains($"launch-new {InstallExe} cwd={Install}", _processes.Calls);
        Assert.Equal(["--updated", Version, "--update-id", UpdateId], _processes.LastLaunchArguments);
        Assert.Equal($@"Local\AgentLimitChecker.Updated.{UpdateId}", _processes.LastStartupEventName);
    }

    [Fact]
    public void Run_HoldsApplierLockUntilNewVersionIsConfirmed()
    {
        var heldDuringLaunch = false;
        _processes.OnLaunch = () => heldDuringLaunch = _processes.IsLockHeld;

        Run();

        Assert.True(heldDuringLaunch);
        Assert.False(_processes.IsLockHeld);
    }

    [Fact]
    public void Run_WhenNewVersionSurvivesWithoutSignal_Succeeds()
    {
        _processes.LaunchResult = new UpdateLaunchResult(UpdateLaunchOutcome.Survived);

        Assert.Equal(UpdateApplyOrchestrator.SuccessExitCode, Run());
        Assert.Equal("new-exe", _files.Files[InstallExe]);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public void Run_WhenNewVersionExitsWithoutSignal_RollsBackEvenIfExitCodeIsZero()
    {
        _processes.LaunchResult = new UpdateLaunchResult(UpdateLaunchOutcome.ExitedWithoutSignal, "exit code=0");

        var exitCode = Run();

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, exitCode);
        AssertOriginalFilesRestored();
        Assert.False(_files.FileExists(CleanupRecord));
        Assert.Contains($"launch-previous {InstallExe}", _processes.Calls);
        Assert.Single(_reporter.Errors);
        Assert.Contains("元のファイルへ戻しました", _reporter.Errors[0]);
    }

    [Fact]
    public void Run_WhenRollingBack_DeletesCleanupRecordBeforeTouchingFiles()
    {
        _processes.LaunchResult = new UpdateLaunchResult(UpdateLaunchOutcome.ExitedWithoutSignal);

        Run();

        var deleteRecord = _files.Operations.IndexOf($"delete {CleanupRecord}");
        var restoreExe = _files.Operations.IndexOf($"move {BackupExe} -> {InstallExe}");
        Assert.True(deleteRecord >= 0 && restoreExe > deleteRecord);
    }

    [Fact]
    public void Run_WhenNewVersionCannotBeLaunched_RollsBackAndStartsPreviousVersion()
    {
        _processes.LaunchResult = new UpdateLaunchResult(UpdateLaunchOutcome.LaunchFailed, "0x2");

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());
        AssertOriginalFilesRestored();
        Assert.Contains($"launch-previous {InstallExe}", _processes.Calls);
        Assert.Contains("起動できませんでした", _reporter.Errors.Single());
    }

    [Fact]
    public void Run_WhenFilesStayLocked_AbortsAfterTimeoutWithoutChangingFiles()
    {
        _files.LockedFiles.Add(InstallExe);

        var exitCode = Run();

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, exitCode);
        Assert.True(_processes.Elapsed >= UpdateApplyOrchestrator.ExclusiveAccessTimeout);
        Assert.Equal("old-exe", _files.Files[InstallExe]);
        Assert.DoesNotContain(_files.Operations, op => op.StartsWith("move", StringComparison.Ordinal));
        Assert.Contains("他にも起動していないか", _reporter.Errors.Single());
    }

    [Fact]
    public void Run_WhenLockIsReleasedWhileWaiting_Continues()
    {
        _files.LockedFiles.Add(InstallExe);
        _files.OnExclusiveCheck = _ =>
        {
            if (_processes.Elapsed >= TimeSpan.FromSeconds(3))
            {
                _files.LockedFiles.Clear();
            }
        };

        Assert.Equal(UpdateApplyOrchestrator.SuccessExitCode, Run());
        Assert.Equal("new-exe", _files.Files[InstallExe]);
    }

    [Fact]
    public void Run_WhenOldProcessDoesNotExit_AbortsWithoutChangingFiles()
    {
        _processes.OldProcessesExit = false;

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());
        Assert.Equal("old-exe", _files.Files[InstallExe]);
        Assert.Empty(_files.Operations);
        Assert.Contains("120 秒", _reporter.Errors.Single());
    }

    [Fact]
    public void Run_WhenAnotherUpdateHoldsTheLock_AbortsBeforeValidating()
    {
        _processes.LockAvailable = false;

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());
        Assert.Empty(_files.Operations);
        Assert.DoesNotContain(_processes.Calls, call => call.StartsWith("wait-old", StringComparison.Ordinal));
        Assert.Single(_reporter.Errors);
    }

    [Fact]
    public void Run_WhenCleanupRecordCannotBeWritten_RollsBack()
    {
        _files.FailWrite = true;

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());
        AssertOriginalFilesRestored();
        Assert.DoesNotContain(_processes.Calls, call => call.StartsWith("launch-new", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WhenCleanupRecordAlreadyExists_AbortsWithoutChangingFiles()
    {
        _files.AddFile(CleanupRecord, "{}");

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());
        Assert.Equal("old-exe", _files.Files[InstallExe]);
        Assert.DoesNotContain(_files.Operations, op => op.StartsWith("move", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WhenRollbackIsImpossible_TellsUserToExtractZipIntoInstallDirectory()
    {
        _processes.LaunchResult = new UpdateLaunchResult(UpdateLaunchOutcome.ExitedWithoutSignal);
        _files.FailMoveFrom.Add(BackupExe);

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());

        var message = _reporter.Errors.Single();
        Assert.Contains("元に戻せないファイル", message);
        Assert.Contains(Install, message);
        Assert.Contains(UpdateCheckDefaults.DistributionPage, message);
        Assert.False(_files.FileExists(CleanupRecord));
    }

    [Fact]
    public void Run_WhenStagingContainsSubdirectory_AbortsWithoutChangingFiles()
    {
        _files.AddDirectory(Path.Combine(Staging, "extra"));

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, Run());
        Assert.Equal("old-exe", _files.Files[InstallExe]);
        Assert.DoesNotContain(_files.Operations, op => op.StartsWith("move", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WithInvalidArguments_AbortsBeforeWaitingForProcesses()
    {
        var exitCode = Run(new UpdateCommandLine(UpdateCommandKind.InvalidApply));

        Assert.Equal(UpdateApplyOrchestrator.FailureExitCode, exitCode);
        Assert.DoesNotContain(_processes.Calls, call => call.StartsWith("wait-old", StringComparison.Ordinal));
        Assert.Empty(_files.Operations);
    }
}
