using System.Text.RegularExpressions;

namespace AgentLimitChecker.Core.Shell;

public static partial class LoginCommand
{
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentName();

    public static string BuildSilentPsCommand(string executable, IReadOnlyList<string> args) =>
        string.Join(" ", new[] { "&", Quote(executable) }.Concat(args.Select(Quote)));

    public static string BuildLoginPsCommand(string executable, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var prefix = string.Concat((environment ?? new Dictionary<string, string?>())
            .Where(pair => EnvironmentName().IsMatch(pair.Key) && !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"$env:{pair.Key}={Quote(pair.Value!)}; "));
        return prefix + BuildSilentPsCommand(executable, args) + "; $c=$LASTEXITCODE; " +
            "if ($c -eq 0 -or $null -eq $c) { Write-Host ''; Write-Host 'ログインが完了しました。このウィンドウは自動的に閉じます…' -ForegroundColor Green; Start-Sleep -Seconds 2 " +
            "} else { Write-Host ''; Write-Host ('ログインに失敗しました (exit ' + $c + ')。Enter キーでこのウィンドウを閉じます。') -ForegroundColor Yellow; [void](Read-Host) }";
    }

    public static IReadOnlyList<string> ArgsFor(string target) => target == "claude" ? ["auth", "login"] : ["login"];

    public static (string Title, string Message) MissingCliMessage(string target)
    {
        var name = target == "claude" ? "Claude Code" : "Codex";
        var envName = target == "claude" ? "CLAUDE_PATH" : "CODEX_PATH";
        return ($"{name} CLI が見つかりません",
            $"{name} CLI が見つかりません。PATH に追加するか {envName} に実行ファイルパスを設定してください。");
    }
}
