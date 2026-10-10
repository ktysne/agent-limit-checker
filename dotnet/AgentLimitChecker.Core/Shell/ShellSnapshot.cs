using AgentLimitChecker.Core.Notifications;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Core.Shell;

public sealed record ShellError(string Code, string Message, double? RetryAfter = null);
public sealed record ServiceResult(bool Ok, UsageSnapshot? Data = null, ShellError? Error = null);
public sealed record AccountResult(CodexAccount Account, ServiceResult Result, string DisplayName,
    string? CustomName, string DefaultName);
public sealed record LoginProgress(bool Claude, IReadOnlyDictionary<string, bool> Codex);
public sealed record ShellSnapshot(ServiceResult? Claude, IReadOnlyList<AccountResult> CodexAccounts,
    CodexAccount CodexDefaultAccount, long FetchedAt, AppSettings Settings, bool AutoLaunchEnabled,
    bool IsPolling, string Theme, string AppVersion, LoginProgress LoginInProgress)
{
    public NotificationSnapshot ToNotificationSnapshot() => new(
        Claude is null ? null : new("claude", "Claude", Claude.Ok, Claude.Data),
        CodexAccounts.Select(a => new NotificationServiceSnapshot(a.Account.Id, a.DisplayName, a.Result.Ok, a.Result.Data)).ToArray());
}

public enum TrayMenuKind { Normal, Separator, Checkbox, Radio }
public sealed record TrayMenuItem(string Label = "", string? Command = null, string? Argument = null,
    bool Enabled = true, bool Checked = false, TrayMenuKind Kind = TrayMenuKind.Normal,
    IReadOnlyList<TrayMenuItem>? Children = null);

public static class TrayPresentation
{
    public const string StartupTooltip = "Agent Limit Checker (起動中…)";
    public static double? Utilization(ServiceResult? service) => service is { Ok: true } ? service.Data?.FiveHour?.Utilization : null;
    public static double? CodexUtilization(ShellSnapshot snapshot)
    {
        double? worst = null;
        foreach (var account in snapshot.CodexAccounts)
        {
            var utilization = Utilization(account.Result);
            if (utilization is not null && (worst is null || utilization > worst)) worst = utilization;
        }
        return worst;
    }
    public static string PercentLabel(double? utilization) => utilization is null || double.IsNaN(utilization.Value)
        ? "--%" : utilization > 1 ? "100%+" : $"{Math.Floor(Math.Max(0, utilization.Value) * 100 + .5)}%";
    public static string ErrorSummary(ShellError? error) => error?.Code switch
    {
        "claude_credentials_missing" or "claude_unauthorized" or "codex_rpc_error" or "codex_home_missing" => "login required",
        "claude_rate_limited" => "rate limited",
        "codex_cli_missing" => "CLI missing",
        "codex_timeout" or "claude_timeout" => "timeout",
        "claude_network" => "network error",
        _ => "error"
    };
    public static string ServiceStatusLabel(string name, ServiceResult? service) =>
        $"{name}: {(service is null ? "取得中" : !service.Ok ? ErrorSummary(service.Error) : PercentLabel(Utilization(service)))}";

    public static string Tooltip(ShellSnapshot snapshot)
    {
        var lines = new List<string> { "Agent Limit Checker", ServiceStatusLabel("Claude", snapshot.Claude) };
        if (snapshot.CodexAccounts.Count == 0) lines.Add(ServiceStatusLabel("Codex", null));
        else lines.AddRange(snapshot.CodexAccounts.Select(a => ServiceStatusLabel(a.DisplayName, a.Result)));
        var value = string.Join('\n', lines);
        if (value.Length <= 127) return value;
        var length = char.IsHighSurrogate(value[125]) ? 125 : 126;
        return value[..length] + "…";
    }

    public static TrayMenuItem UsageMenuItem(string name, ServiceResult? service) => new(
        ($"{name} 5h:").PadRight(11) + (service is { Ok: false } ? ErrorSummary(service.Error) : PercentLabel(Utilization(service))), Enabled: false);

    public static IReadOnlyList<TrayMenuItem> Menu(ShellSnapshot snapshot)
    {
        var accounts = snapshot.CodexAccounts;
        var targets = CodexHomes.CodexLoginTargets(accounts.Select(a => a.Account with { DisplayName = a.DisplayName }).ToArray(), snapshot.CodexDefaultAccount);
        var items = new List<TrayMenuItem> { UsageMenuItem("Claude", snapshot.Claude) };
        if (accounts.Count == 0) items.Add(UsageMenuItem("Codex", null));
        else items.AddRange(accounts.Select(a => UsageMenuItem(a.DisplayName, a.Result)));
        items.AddRange([
            new(Kind: TrayMenuKind.Separator), new("詳細を表示", "details"), new("今すぐ更新", "refresh"),
            new(Kind: TrayMenuKind.Separator), new("claude login (新しいターミナルで実行)", "claude-login")]);
        items.AddRange(targets.Select(a => new TrayMenuItem(targets.Count <= 1
            ? "codex login (新しいターミナルで実行)"
            : accounts.All(found => found.Account.Id != a.Id) ? $"codex login {a.Label} (既定ホームを作成)"
            : $"codex login {a.DisplayName} (新しいターミナルで実行)", "codex-login", a.Id)));
        items.AddRange([
            new(Kind: TrayMenuKind.Separator),
            new("ログイン時に自動起動", "auto-launch", Checked: snapshot.AutoLaunchEnabled, Kind: TrayMenuKind.Checkbox),
            new("更新間隔", Children: new[] { (30, "30秒"), (60, "1分"), (120, "2分"), (300, "5分"), (600, "10分") }
                .Select(p => new TrayMenuItem(p.Item2, "interval", p.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Checked: snapshot.Settings.PollingIntervalSec == p.Item1, Kind: TrayMenuKind.Radio)).ToArray()),
            new(Kind: TrayMenuKind.Separator), new("終了", "quit")]);
        return items;
    }
}
