using AgentLimitChecker.Core.Providers;

namespace AgentLimitChecker.Tests;

public sealed class CliPathsTests
{
    [Fact]
    public void ResolveExecutableFromPath_ReturnsFirstCandidateInPriorityOrder()
    {
        using var dir = new ProviderTestDirectory();
        dir.FileAt("codex.ps1");
        var exe = dir.FileAt("codex.exe");
        Assert.Equal(exe, CliPaths.ResolveExecutableFromPath(["codex.exe", "codex.ps1"], new Dictionary<string, string> { ["PATH"] = dir.Root }));
    }
    [Fact]
    public void ResolveExecutableFromPath_SearchesLaterPathDirectoriesOnlyWhenNeeded()
    {
        using var a = new ProviderTestDirectory();
        using var b = new ProviderTestDirectory();
        var target = b.FileAt("claude.cmd");
        Assert.Equal(target, CliPaths.ResolveExecutableFromPath(["claude.exe", "claude.cmd"], new Dictionary<string, string> { ["PATH"] = a.Root + Path.PathSeparator + b.Root }));
    }
    [Fact]
    public void ResolveCodexExecutables_PrefersOrdinaryCliAndDeduplicatesPaths()
    {
        using var dir = new ProviderTestDirectory();
        var shim = dir.FileAt("normal", "codex.cmd");
        var exe = dir.FileAt("normal", "codex.exe");
        var extension = dir.FileAt(".vscode", "extensions", "openai.chatgpt-2", "bin", "windows-x86_64", "codex.exe");
        var npm = dir.FileAt("npm", "codex.ps1");
        var normal = Path.GetDirectoryName(exe)!;
        var env = new Dictionary<string, string> { ["PATH"] = string.Join(Path.PathSeparator, normal, normal.ToUpperInvariant(), Path.GetDirectoryName(extension)), ["USERPROFILE"] = dir.Root, ["APPDATA"] = dir.Root };
        Assert.Equal(new[] { exe, shim, npm, extension }, CliPaths.ResolveCodexExecutables(env));
        env["CODEX_PATH"] = npm;
        Assert.Equal(new[] { npm }, CliPaths.ResolveCodexExecutables(env));
        env["CLAUDE_PATH"] = shim;
        Assert.Equal(shim, CliPaths.ResolveClaudeExecutable(env));
    }
    [Fact]
    public void ResolveClaudeExecutable_PrefersPs1AndFallsBackToNpm()
    {
        using var dir = new ProviderTestDirectory();
        dir.FileAt("npm", "claude.cmd");
        var ps1 = dir.FileAt("npm", "claude.ps1");
        Assert.Equal(ps1, CliPaths.ResolveClaudeExecutable(new Dictionary<string, string> { ["APPDATA"] = dir.Root }));
    }
}
