using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class LoginCommandTests
{
    [Fact]
    public void VisibleCommandStaysOnOneLine() => Assert.DoesNotContain('\n', LoginCommand.BuildLoginPsCommand("C:/Users/me/AppData/Roaming/npm/claude.ps1", ["auth", "login"]));

    [Fact]
    public void VisibleCommandDoesNotEmitDoubleQuotes() => Assert.DoesNotContain('"', LoginCommand.BuildLoginPsCommand("C:/path with spaces/claude.exe", ["auth", "login"]));

    [Fact]
    public void VisibleCommandQuotesExecutableAndEveryArgument() => Assert.StartsWith("& 'C:/path with spaces/claude.exe' 'auth' 'login';", LoginCommand.BuildLoginPsCommand("C:/path with spaces/claude.exe", ["auth", "login"]));

    [Fact]
    public void VisibleCommandDoublesQuotesInPath() => Assert.Contains("& 'C:/o''brien/codex.exe' 'login';", LoginCommand.BuildLoginPsCommand("C:/o'brien/codex.exe", ["login"]));

    [Fact]
    public void VisibleCommandClosesOnSuccessAndWaitsOnFailure()
    {
        var command = LoginCommand.BuildLoginPsCommand("claude", ["auth", "login"]);
        Assert.Contains("if ($c -eq 0 -or $null -eq $c) {", command);
        Assert.Contains("Start-Sleep -Seconds 2", command);
        Assert.Contains("} else {", command);
        Assert.Contains("Read-Host", command);
        Assert.DoesNotMatch(@"}\s*;\s*else", command);
    }

    [Fact]
    public void VisibleCommandSetsCodexHomeBeforeInvocation()
    {
        var command = LoginCommand.BuildLoginPsCommand("codex.exe", ["login"], new Dictionary<string, string?> { ["CODEX_HOME"] = "C:/Users/me/.codex-review" });
        Assert.StartsWith("$env:CODEX_HOME='C:/Users/me/.codex-review'; & 'codex.exe' 'login';", command);
        Assert.DoesNotContain('"', command);
        Assert.DoesNotContain('\n', command);
    }

    [Fact]
    public void VisibleCommandDoublesQuotesInEnvironmentValue()
    {
        var command = LoginCommand.BuildLoginPsCommand("codex.exe", ["login"], new Dictionary<string, string?> { ["CODEX_HOME"] = "C:/o'brien/.codex" });
        Assert.StartsWith("$env:CODEX_HOME='C:/o''brien/.codex'; ", command);
        Assert.DoesNotContain('"', command);
    }

    [Fact]
    public void VisibleCommandOmitsAbsentOrEmptyEnvironment()
    {
        var command = LoginCommand.BuildLoginPsCommand("codex.exe", ["login"]);
        Assert.StartsWith("& 'codex.exe' 'login';", command);
        Assert.DoesNotContain("$env:", command);
        Assert.DoesNotContain("$env:", LoginCommand.BuildLoginPsCommand("codex.exe", ["login"], new Dictionary<string, string?> { ["CODEX_HOME"] = "" }));
    }

    [Fact]
    public void VisibleCommandDropsInvalidEnvironmentNames()
    {
        var command = LoginCommand.BuildLoginPsCommand("codex.exe", ["login"], new Dictionary<string, string?> { ["BAD; Remove-Item 'x"] = "value", ["CODEX_HOME"] = "C:/home/.codex" });
        Assert.DoesNotContain("Remove-Item", command);
        Assert.StartsWith("$env:CODEX_HOME='C:/home/.codex'; ", command);
    }

    [Fact]
    public void SilentCommandContainsOnlyInvocation()
    {
        var command = LoginCommand.BuildSilentPsCommand("claude", ["auth", "login"]);
        Assert.Equal("& 'claude' 'auth' 'login'", command);
        Assert.DoesNotContain("Read-Host", command);
        Assert.DoesNotContain("LASTEXITCODE", command);
    }

    [Fact]
    public void SilentCommandQuotesSpacedPathWithoutDoubleQuotes()
    {
        var command = LoginCommand.BuildSilentPsCommand("C:/path with spaces/claude.exe", ["auth", "login"]);
        Assert.Equal("& 'C:/path with spaces/claude.exe' 'auth' 'login'", command);
        Assert.DoesNotContain('"', command);
    }

    [Fact]
    public void SilentCommandDoublesQuotesInPath() => Assert.Equal("& 'C:/o''brien/codex.exe' 'login'", LoginCommand.BuildSilentPsCommand("C:/o'brien/codex.exe", ["login"]));

    [Fact]
    public void ClaudeUsesAuthLoginAndCodexUsesLogin()
    {
        Assert.Equal(new[] { "auth", "login" }, LoginCommand.ArgsFor("claude"));
        Assert.Equal(new[] { "login" }, LoginCommand.ArgsFor("codex"));
    }

    [Theory]
    [InlineData("claude", "Claude Code", "CLAUDE_PATH")]
    [InlineData("codex", "Codex", "CODEX_PATH")]
    public void MissingCliMessageMatchesElectron(string target, string name, string envName)
    {
        var message = LoginCommand.MissingCliMessage(target);
        Assert.Equal($"{name} CLI が見つかりません", message.Title);
        Assert.Equal($"{name} CLI が見つかりません。PATH に追加するか {envName} に実行ファイルパスを設定してください。", message.Message);
    }
}
