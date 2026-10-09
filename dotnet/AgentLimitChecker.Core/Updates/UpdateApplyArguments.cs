namespace AgentLimitChecker.Core.Updates;
public sealed record UpdateApplyContext(int ProcessId, string StagingDirectory, string InstallDirectory, string UpdateId)
{
    public string InstallExecutablePath => Path.Combine(InstallDirectory, UpdateLayout.ExecutableFileName);
}

public sealed record UpdateApplyArgumentsResult(UpdateApplyContext? Context, string? Error);
public static class UpdateApplyArguments
{
    public static UpdateApplyArgumentsResult Validate(
        UpdateCommandLine commandLine,
        string updateRootDirectory,
        IUpdateFileOperations operations)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(operations);

        if (commandLine.Kind != UpdateCommandKind.Apply || commandLine.ProcessId <= 0)
        {
            return Fail("更新の指定が正しくありません。");
        }

        var staging = UpdateLayout.NormalizeFullPath(commandLine.StagingDirectory);
        var install = UpdateLayout.NormalizeFullPath(commandLine.InstallDirectory);
        var updateRoot = UpdateLayout.NormalizeFullPath(updateRootDirectory);
        if (staging is null || install is null || updateRoot is null)
        {
            return Fail("展開先またはインストール先のフルパスを解決できません。");
        }

        if (UpdateLayout.PathEquals(staging, install))
        {
            return Fail("展開先とインストール先が同じです。");
        }

        if (!operations.DirectoryExists(staging) || !operations.DirectoryExists(install)
            || !operations.FileExists(Path.Combine(staging, UpdateLayout.ExecutableFileName))
            || !operations.FileExists(Path.Combine(install, UpdateLayout.ExecutableFileName)))
        {
            return Fail("展開先またはインストール先に AgentLimitChecker.exe がありません。");
        }

        var updateDirectory = Path.GetDirectoryName(staging);
        var updateId = updateDirectory is null ? null : Path.GetFileName(updateDirectory);
        var parentOfUpdateDirectory = updateDirectory is null ? null : Path.GetDirectoryName(updateDirectory);
        if (!UpdateLayout.IsUpdateId(updateId)
            || !string.Equals(Path.GetFileName(staging), UpdateLayout.StagingDirectoryName, StringComparison.OrdinalIgnoreCase)
            || parentOfUpdateDirectory is null
            || !UpdateLayout.PathEquals(parentOfUpdateDirectory, updateRoot))
        {
            return Fail("展開先が更新の作業フォルダーの下にありません。");
        }
        if (UpdateLayout.Overlaps(install, updateRoot))
        {
            return Fail("インストール先が更新の作業フォルダーと重なっています。");
        }

        return new UpdateApplyArgumentsResult(new UpdateApplyContext(commandLine.ProcessId, staging, install, updateId!), null);
    }
    public static string LogIdFor(UpdateCommandLine commandLine)
    {
        var staging = UpdateLayout.NormalizeFullPath(commandLine.StagingDirectory);
        var updateDirectory = staging is null ? null : Path.GetDirectoryName(staging);
        var id = updateDirectory is null ? null : Path.GetFileName(updateDirectory);
        return UpdateLayout.IsUpdateId(id) ? id! : "unknown";
    }

    private static UpdateApplyArgumentsResult Fail(string error) => new(null, error);
}
