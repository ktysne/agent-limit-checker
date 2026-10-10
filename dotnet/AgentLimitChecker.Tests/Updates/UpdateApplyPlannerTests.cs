using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;

public class UpdateApplyPlannerTests
{
    private const string UpdateId = "0123456789abcdef0123456789abcdef";

    private static readonly string Root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-plan-tests");
    private static readonly string Staging = Path.Combine(Root, "update", UpdateId, "app");
    private static readonly string Install = Path.Combine(Root, "install");

    private static FakeUpdateFileOperations CreateFiles(params string[] stagedNames)
    {
        var files = new FakeUpdateFileOperations();
        files.AddDirectory(Install);
        foreach (var name in stagedNames)
        {
            files.AddFile(Path.Combine(Staging, name), "new:" + name);
        }

        return files;
    }

    private static string InInstall(string name) => Path.Combine(Install, name);

    private static string Backup(string name) => InInstall(name) + "." + UpdateId + ".old";

    private static string Staged(string name) => InInstall(name) + "." + UpdateId + ".new";

    private static UpdateApplyPlan MakePlan(FakeUpdateFileOperations files, params string[] names)
    {
        var result = UpdateApplyPlanner.MakePlan(Staging, Install, UpdateId, names, files);
        Assert.Null(result.Error);
        return result.Plan!;
    }

    [Fact]
    public void MakePlan_MarksExistingFilesForReplacementWithBackupPath()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");

        var plan = MakePlan(files, "AgentLimitChecker.exe", "manual.html");

        var exe = plan.Items.Single(item => item.FileName == "AgentLimitChecker.exe");
        Assert.True(exe.ReplaceExisting);
        Assert.Equal(Backup("AgentLimitChecker.exe"), exe.BackupPath);
        Assert.False(plan.Items.Single(item => item.FileName == "manual.html").ReplaceExisting);
    }

    [Fact]
    public void MakePlan_RejectsWhenBackupPathAlreadyExists()
    {
        var files = CreateFiles("AgentLimitChecker.exe");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.AddFile(Backup("AgentLimitChecker.exe"), "older");

        var result = UpdateApplyPlanner.MakePlan(Staging, Install, UpdateId, ["AgentLimitChecker.exe"], files);

        Assert.Null(result.Plan);
        Assert.Contains("退避先", result.Error);
    }

    [Fact]
    public void MakePlan_RejectsWhenDestinationIsDirectory()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        files.AddDirectory(InInstall("manual.html"));

        var result = UpdateApplyPlanner.MakePlan(Staging, Install, UpdateId, ["AgentLimitChecker.exe", "manual.html"], files);

        Assert.Null(result.Plan);
        Assert.Contains("種類", result.Error);
    }

    [Theory]
    [InlineData("sub/AgentLimitChecker.exe")]
    [InlineData("..")]
    [InlineData("AgentLimitChecker.exe.old")]
    public void MakePlan_RejectsNamesThatAreNotPlainFileNames(string name)
    {
        var files = CreateFiles("AgentLimitChecker.exe");

        var result = UpdateApplyPlanner.MakePlan(Staging, Install, UpdateId, ["AgentLimitChecker.exe", name], files);

        Assert.Null(result.Plan);
    }

    [Fact]
    public void MakePlan_RejectsMissingExecutable()
    {
        var files = CreateFiles("manual.html");

        var result = UpdateApplyPlanner.MakePlan(Staging, Install, UpdateId, ["manual.html"], files);

        Assert.Null(result.Plan);
    }

    [Fact]
    public void MakePlan_RejectsInvalidUpdateId()
    {
        var files = CreateFiles("AgentLimitChecker.exe");

        var result = UpdateApplyPlanner.MakePlan(Staging, Install, "..", ["AgentLimitChecker.exe"], files);

        Assert.Null(result.Plan);
    }

    [Fact]
    public void Execute_ReplacesFilesAndLeavesFilesNotInPackageUntouched()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.AddFile(InInstall("user-notes.txt"), "mine");
        var plan = MakePlan(files, "AgentLimitChecker.exe", "manual.html");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.True(result.Succeeded);
        Assert.Equal("new:AgentLimitChecker.exe", files.Files[InInstall("AgentLimitChecker.exe")]);
        Assert.Equal("old", files.Files[Backup("AgentLimitChecker.exe")]);
        Assert.Equal("new:manual.html", files.Files[InInstall("manual.html")]);
        Assert.Equal("mine", files.Files[InInstall("user-notes.txt")]);
        Assert.DoesNotContain(files.Operations, op => op.Contains("user-notes.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_ChecksExclusiveAccessBeforeMovingRunningExecutable()
    {
        var files = CreateFiles("AgentLimitChecker.exe");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.LockedFiles.Add(InInstall("AgentLimitChecker.exe"));
        var plan = MakePlan(files, "AgentLimitChecker.exe");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackSucceeded);
        Assert.Empty(files.Operations);
        Assert.Equal("old", files.Files[InInstall("AgentLimitChecker.exe")]);
    }

    [Fact]
    public void Execute_WhenCopyFails_LeavesExistingExecutableInPlaceWithoutBackup()
    {
        var files = CreateFiles("AgentLimitChecker.exe");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.FailCopyTo.Add(Staged("AgentLimitChecker.exe"));
        var plan = MakePlan(files, "AgentLimitChecker.exe");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal("old", files.Files[InInstall("AgentLimitChecker.exe")]);
        Assert.False(files.FileExists(Backup("AgentLimitChecker.exe")));
        Assert.False(files.FileExists(Staged("AgentLimitChecker.exe")));
    }

    [Fact]
    public void Execute_WhenLaterCopyFails_RestoresEarlierItemsInReverseOrder()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "license.html", "manual.html");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old-exe");
        files.AddFile(InInstall("license.html"), "old-license");
        files.FailCopyTo.Add(Staged("manual.html"));
        var plan = MakePlan(files, "AgentLimitChecker.exe", "license.html", "manual.html");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal("old-exe", files.Files[InInstall("AgentLimitChecker.exe")]);
        Assert.Equal("old-license", files.Files[InInstall("license.html")]);
        Assert.False(files.FileExists(InInstall("manual.html")));
        Assert.False(files.FileExists(Backup("AgentLimitChecker.exe")));

        var rollback = files.Operations.SkipWhile(op => !op.StartsWith("copy", StringComparison.Ordinal) || !op.Contains("manual.html", StringComparison.Ordinal)).Skip(1).ToList();
        Assert.Equal(
            [
                $"delete {InInstall("license.html")}",
                $"move {Backup("license.html")} -> {InInstall("license.html")}",
                $"delete {InInstall("AgentLimitChecker.exe")}",
                $"move {Backup("AgentLimitChecker.exe")} -> {InInstall("AgentLimitChecker.exe")}",
            ],
            rollback);
    }

    [Fact]
    public void Execute_WhenBackupMoveFails_DoesNotDeleteTheOriginal()
    {
        var files = CreateFiles("AgentLimitChecker.exe");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.FailMoveFrom.Add(InInstall("AgentLimitChecker.exe"));
        var plan = MakePlan(files, "AgentLimitChecker.exe");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal("old", files.Files[InInstall("AgentLimitChecker.exe")]);
        Assert.DoesNotContain($"delete {InInstall("AgentLimitChecker.exe")}", files.Operations);
        Assert.False(files.FileExists(Staged("AgentLimitChecker.exe")));
    }

    [Fact]
    public void Execute_WhenFileAppearsAfterPlanning_DoesNotOverwriteOrDeleteIt()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        var plan = MakePlan(files, "AgentLimitChecker.exe", "manual.html");
        files.AddFile(InInstall("manual.html"), "user");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.Equal("user", files.Files[InInstall("manual.html")]);
        Assert.False(files.FileExists(InInstall("AgentLimitChecker.exe")));
    }

    [Fact]
    public void Execute_WhenFileAppearsBetweenCheckAndPlacement_DoesNotDeleteIt()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        var plan = MakePlan(files, "AgentLimitChecker.exe", "manual.html");
        files.BeforeCopy = destination =>
        {
            if (destination == Staged("manual.html"))
            {
                files.AddFile(InInstall("manual.html"), "user");
            }
        };

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal("user", files.Files[InInstall("manual.html")]);
        Assert.False(files.FileExists(Staged("manual.html")));
        Assert.False(files.FileExists(InInstall("AgentLimitChecker.exe")));
    }

    [Fact]
    public void Execute_WhenStagedNameAppearsBeforeCopy_DoesNotDeleteIt()
    {
        var files = CreateFiles("AgentLimitChecker.exe");
        var plan = MakePlan(files, "AgentLimitChecker.exe");
        files.BeforeCopy = destination => files.AddFile(destination, "other");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.Equal("other", files.Files[Staged("AgentLimitChecker.exe")]);
        Assert.DoesNotContain(files.Operations, op => op.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public void Execute_WhenRestoreFails_ReportsRollbackFailure()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.FailCopyTo.Add(Staged("manual.html"));
        files.FailMoveFrom.Add(Backup("AgentLimitChecker.exe"));
        var plan = MakePlan(files, "AgentLimitChecker.exe", "manual.html");

        var result = UpdateApplyPlanner.Execute(plan, files);

        Assert.False(result.Succeeded);
        Assert.False(result.RollbackSucceeded);
    }

    [Fact]
    public void Rollback_RestoresBackupsAndRemovesAddedFiles()
    {
        var files = CreateFiles("AgentLimitChecker.exe", "manual.html");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        files.AddFile(InInstall("user-notes.txt"), "mine");
        var plan = MakePlan(files, "AgentLimitChecker.exe", "manual.html");
        Assert.True(UpdateApplyPlanner.Execute(plan, files).Succeeded);

        var result = UpdateApplyPlanner.Rollback(plan, files);

        Assert.True(result.Succeeded);
        Assert.Equal("old", files.Files[InInstall("AgentLimitChecker.exe")]);
        Assert.False(files.FileExists(InInstall("manual.html")));
        Assert.False(files.FileExists(Backup("AgentLimitChecker.exe")));
        Assert.Equal("mine", files.Files[InInstall("user-notes.txt")]);
    }

    [Fact]
    public void Rollback_WhenBackupIsMissing_KeepsReplacedFileAndReportsFailure()
    {
        var files = CreateFiles("AgentLimitChecker.exe");
        files.AddFile(InInstall("AgentLimitChecker.exe"), "old");
        var plan = MakePlan(files, "AgentLimitChecker.exe");
        Assert.True(UpdateApplyPlanner.Execute(plan, files).Succeeded);
        files.Files.Remove(Backup("AgentLimitChecker.exe"));

        var result = UpdateApplyPlanner.Rollback(plan, files);

        Assert.False(result.Succeeded);
        Assert.Equal("new:AgentLimitChecker.exe", files.Files[InInstall("AgentLimitChecker.exe")]);
    }
}
