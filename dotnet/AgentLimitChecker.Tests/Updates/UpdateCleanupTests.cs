using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;
public class UpdateCleanupTests
{
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly Version Running = new(1, 2, 0);
    private static readonly string Root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p8-update", "tests"), "agent-limit-checker-cleanup-tests");
    private static readonly string UpdateRoot = Path.Combine(Root, "AgentLimitChecker", "update");
    private static readonly string Install = Path.Combine(Root, "Apps", "AgentLimitChecker");

    private readonly FakeUpdateFileOperations _files = new();

    public UpdateCleanupTests()
    {
        _files.AddDirectory(UpdateRoot);
        _files.AddDirectory(Install);
    }

    private static string Backup(string name, string id) => Path.Combine(Install, $"{name}.{id}.old");

    private string AddBackup(string name, string id)
    {
        var path = Backup(name, id);
        _files.AddFile(path, "old");
        return path;
    }

    private static UpdateCleanupRecordEntry Record(string id, IReadOnlyList<string> backups, string? install = null, string version = "1.2.0") =>
        new(Path.Combine(UpdateRoot, $"cleanup-{id}.json"), id, new UpdateCleanupRecord(install ?? Install, version, backups));

    private static UpdateFolderEntry Folder(string name, bool reparse = false, string? preparedVersion = null, bool hasPrepared = false) =>
        new(Path.Combine(UpdateRoot, name), reparse, hasPrepared || preparedVersion is not null,
            preparedVersion is null ? null : new UpdatePreparedRecord(preparedVersion, new string('0', 64)));

    private UpdateCleanupPlan Select(IReadOnlyList<UpdateCleanupRecordEntry>? records = null, IReadOnlyList<UpdateFolderEntry>? folders = null) =>
        UpdateCleanupSelector.Select(Install, Running, UpdateRoot, records ?? [], folders ?? [], _files);

    [Fact]
    public void Select_WithNothingToClean_ReturnsEmptyPlan()
    {
        var plan = Select();

        Assert.Empty(plan.RecordActions);
        Assert.Empty(plan.DirectoriesToDelete);
    }

    [Fact]
    public void Select_WhenAllConditionsHold_SelectsEveryBackupAndFolder()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);
        var manual = AddBackup("manual.html", IdA);

        var plan = Select([Record(IdA, [exe, manual])], [Folder(IdA, preparedVersion: "1.2.0"), Folder(IdB)]);

        Assert.Equal([exe, manual], plan.RecordActions.Single().BackupsToDelete);
        Assert.Equal([Path.Combine(UpdateRoot, IdA), Path.Combine(UpdateRoot, IdB)], plan.DirectoriesToDelete);
    }

    [Fact]
    public void Select_SkipsBackupOutsideInstallDirectory()
    {
        var outside = Path.Combine(Root, "Other", $"AgentLimitChecker.exe.{IdA}.old");
        _files.AddFile(outside);
        var nested = Path.Combine(Install, "sub", $"AgentLimitChecker.exe.{IdA}.old");
        _files.AddFile(nested);
        var traversal = Path.Combine(Install, "..", "Other", $"AgentLimitChecker.exe.{IdA}.old");

        var plan = Select([Record(IdA, [outside, nested, traversal])]);

        Assert.Empty(plan.RecordActions.Single().BackupsToDelete);
    }

    [Fact]
    public void Select_SkipsBackupWhoseIdDoesNotMatchRecord()
    {
        var other = AddBackup("AgentLimitChecker.exe", IdB);

        var plan = Select([Record(IdA, [other])]);

        Assert.Empty(plan.RecordActions.Single().BackupsToDelete);
    }

    [Theory]
    [InlineData("AgentLimitChecker.exe")]
    [InlineData("AgentLimitChecker.exe.tmp")]
    [InlineData("settings.json")]
    public void Select_SkipsFilesThatAreNotBackups(string name)
    {
        var path = Path.Combine(Install, name);
        _files.AddFile(path);

        var plan = Select([Record(IdA, [path])]);

        Assert.Empty(plan.RecordActions.Single().BackupsToDelete);
    }

    [Fact]
    public void Select_SkipsBackupThatIsReparsePoint()
    {
        var link = AddBackup("AgentLimitChecker.exe", IdA);
        _files.ReparsePoints.Add(link);

        var plan = Select([Record(IdA, [link])]);

        Assert.Empty(plan.RecordActions.Single().BackupsToDelete);
    }

    [Fact]
    public void Select_SkipsRecordForAnotherInstallDirectory()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);

        var plan = Select([Record(IdA, [exe], install: Path.Combine(Root, "Other"))]);

        Assert.Empty(plan.RecordActions);
    }

    [Fact]
    public void Select_MatchesInstallDirectoryIgnoringCase()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);

        var plan = Select([Record(IdA, [exe], install: Install.ToUpperInvariant())]);

        Assert.Equal([exe], plan.RecordActions.Single().BackupsToDelete);
    }

    [Fact]
    public void Select_SkipsRecordWrittenByNewerVersion()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);

        var plan = Select([Record(IdA, [exe], version: "1.3.0")]);

        Assert.Empty(plan.RecordActions);
    }

    [Fact]
    public void Select_SkipsUnreadableRecord()
    {
        AddBackup("AgentLimitChecker.exe", IdA);

        var plan = Select([new UpdateCleanupRecordEntry(Path.Combine(UpdateRoot, $"cleanup-{IdA}.json"), IdA, null)]);

        Assert.Empty(plan.RecordActions);
    }

    [Fact]
    public void Select_KeepsPreparedFolderForNewerVersion()
    {
        var plan = Select(folders: [Folder(IdA, preparedVersion: "1.3.0")]);

        Assert.Empty(plan.DirectoriesToDelete);
    }

    [Fact]
    public void Select_KeepsFolderWithUnreadablePreparedRecord()
    {
        var plan = Select(folders: [Folder(IdA, hasPrepared: true)]);

        Assert.Empty(plan.DirectoriesToDelete);
    }

    [Fact]
    public void Select_SkipsReparsePointFolder()
    {
        var plan = Select(folders: [Folder(IdA, reparse: true)]);

        Assert.Empty(plan.DirectoriesToDelete);
    }

    [Fact]
    public void Select_SkipsFoldersThatAreNotUpdateIdsOrNotDirectlyUnderUpdateRoot()
    {
        var plan = Select(folders:
        [
            Folder("logs"),
            Folder(IdA.ToUpperInvariant()),
            new UpdateFolderEntry(Path.Combine(Root, IdA), false, false, null),
            new UpdateFolderEntry(Path.Combine(UpdateRoot, IdA, IdB), false, false, null),
        ]);

        Assert.Empty(plan.DirectoriesToDelete);
    }

    [Fact]
    public void Service_DeletesOnlySelectedItemsAndLeavesOtherFilesUntouched()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);
        var foreign = AddBackup("manual.html", IdB);
        var recordPath = Path.Combine(UpdateRoot, $"cleanup-{IdA}.json");
        _files.AddFile(recordPath, UpdateRecordSerializer.Serialize(new UpdateCleanupRecord(Install, "1.2.0", [exe, foreign])));
        var keptLog = Path.Combine(UpdateRoot, $"apply-{IdA}.log");
        _files.AddFile(keptLog);
        _files.AddFile(Path.Combine(UpdateRoot, IdA, "app", "AgentLimitChecker.exe"));
        _files.AddFile(Path.Combine(UpdateRoot, IdB, "prepared.json"), UpdateRecordSerializer.Serialize(new UpdatePreparedRecord("2.0.0", new string('0', 64))));
        _files.AddFile(Path.Combine(Install, "AgentLimitChecker.exe"));

        new UpdateCleanupService(_files, new FakeUpdateProcessOperations(), _ => { }).Run(Install, "1.2.0", UpdateRoot);

        Assert.False(_files.FileExists(exe));
        Assert.True(_files.FileExists(foreign));
        Assert.False(_files.FileExists(recordPath));
        Assert.True(_files.FileExists(keptLog));
        Assert.False(_files.DirectoryExists(Path.Combine(UpdateRoot, IdA)));
        Assert.True(_files.DirectoryExists(Path.Combine(UpdateRoot, IdB)));
        Assert.True(_files.FileExists(Path.Combine(Install, "AgentLimitChecker.exe")));
    }

    [Fact]
    public void Service_KeepsBackupsThatCouldNotBeDeletedInRecord()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);
        var manual = AddBackup("manual.html", IdA);
        _files.FailDelete.Add(exe);
        var recordPath = Path.Combine(UpdateRoot, $"cleanup-{IdA}.json");
        _files.AddFile(recordPath, UpdateRecordSerializer.Serialize(new UpdateCleanupRecord(Install, "1.2.0", [exe, manual])));

        new UpdateCleanupService(_files, new FakeUpdateProcessOperations(), _ => { }).Run(Install, "1.2.0", UpdateRoot);

        Assert.False(_files.FileExists(manual));
        var rewritten = UpdateRecordSerializer.ParseCleanup(_files.Files[recordPath]);
        Assert.Equal([exe], rewritten!.Backups);
    }

    [Fact]
    public void Service_WhenRecordRewriteFails_KeepsOriginalRecord()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);
        var manual = AddBackup("manual.html", IdA);
        _files.FailDelete.Add(exe);
        _files.FailWrite = true;
        var recordPath = Path.Combine(UpdateRoot, $"cleanup-{IdA}.json");
        _files.AddFile(recordPath, UpdateRecordSerializer.Serialize(new UpdateCleanupRecord(Install, "1.2.0", [exe, manual])));

        new UpdateCleanupService(_files, new FakeUpdateProcessOperations(), _ => { }).Run(Install, "1.2.0", UpdateRoot);

        var kept = UpdateRecordSerializer.ParseCleanup(_files.Files[recordPath]);
        Assert.Contains(exe, kept!.Backups);
    }

    [Fact]
    public void Service_WhenLockIsNotAvailable_DeletesNothing()
    {
        var exe = AddBackup("AgentLimitChecker.exe", IdA);
        _files.AddFile(Path.Combine(UpdateRoot, $"cleanup-{IdA}.json"), UpdateRecordSerializer.Serialize(new UpdateCleanupRecord(Install, "1.2.0", [exe])));
        _files.AddDirectory(Path.Combine(UpdateRoot, IdB));
        var processes = new FakeUpdateProcessOperations { LockAvailable = false };

        new UpdateCleanupService(_files, processes, _ => { }).Run(Install, "1.2.0", UpdateRoot);

        Assert.Equal(UpdateCleanupService.LockTimeout, processes.LastLockTimeout);
        Assert.True(_files.FileExists(exe));
        Assert.True(_files.DirectoryExists(Path.Combine(UpdateRoot, IdB)));
        Assert.DoesNotContain(_files.Operations, op => op.StartsWith("delete", StringComparison.Ordinal) || op.StartsWith("rmdir", StringComparison.Ordinal));
    }

    [Fact]
    public void Service_WhenUpdateRootIsReparsePoint_DeletesNothing()
    {
        _files.AddDirectory(Path.Combine(UpdateRoot, IdB));
        _files.ReparsePoints.Add(UpdateRoot);

        new UpdateCleanupService(_files, new FakeUpdateProcessOperations(), _ => { }).Run(Install, "1.2.0", UpdateRoot);

        Assert.Empty(_files.Operations);
    }
}
