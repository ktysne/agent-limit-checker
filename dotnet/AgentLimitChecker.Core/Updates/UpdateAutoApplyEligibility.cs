
namespace AgentLimitChecker.Core.Updates;
public sealed record UpdateAutoApplyEnvironment(
    string? ProcessPath,
    bool IsSingleFilePublish,
    string TempDirectory,
    string UpdateRootDirectory,
    bool AllowDevelopmentHosts,
    Func<string, bool> CanWriteInstallDirectory);
public sealed record UpdateAutoApplyDecision(string? InstallDirectory, string? UnavailableReason)
{
    public bool CanAutoApply => UnavailableReason is null;
}
public static class UpdateAutoApplyEligibility
{
    public static UpdateAutoApplyDecision Evaluate(UpdateInfo update, UpdateAutoApplyEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(environment);

        if (update.Sha256 is null)
        {
            return Unavailable("配布サイトの情報に照合用の値が無いため、自動では更新できません。");
        }

        if (!UpdateUrlPolicy.IsAllowedPackageUrl(
                update.DownloadUrl,
                update.VersionText,
                environment.AllowDevelopmentHosts))
        {
            return Unavailable("配布ファイルの URL が許可された形式ではないため、自動では更新できません。");
        }

        var processPath = UpdateLayout.NormalizeFullPath(environment.ProcessPath);
        if (processPath is null
            || !environment.IsSingleFilePublish
            || !string.Equals(Path.GetFileName(processPath), UpdateLayout.ExecutableFileName, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable("配布版の AgentLimitChecker.exe として実行していないため、自動では更新できません。");
        }

        var install = Path.GetDirectoryName(processPath);
        if (install is null)
        {
            return Unavailable("インストール先を判別できないため、自動では更新できません。");
        }

        var temp = UpdateLayout.NormalizeFullPath(environment.TempDirectory);
        if (temp is not null && UpdateLayout.IsSameOrUnder(install, temp))
        {
            return Unavailable("一時フォルダーから実行しているため、自動では更新できません。zip を展開したフォルダーから起動してください。");
        }

        var updateRoot = UpdateLayout.NormalizeFullPath(environment.UpdateRootDirectory);
        if (updateRoot is null || UpdateLayout.Overlaps(install, updateRoot))
        {
            return Unavailable("インストール先が更新の作業フォルダーと重なっているため、自動では更新できません。");
        }

        if (!environment.CanWriteInstallDirectory(install))
        {
            return Unavailable("インストール先に書き込めないため、自動では更新できません。");
        }

        return new UpdateAutoApplyDecision(install, null);
    }

    private static UpdateAutoApplyDecision Unavailable(string reason) => new(null, reason);
}
