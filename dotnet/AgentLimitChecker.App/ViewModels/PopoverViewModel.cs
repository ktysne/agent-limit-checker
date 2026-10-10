using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.App.ViewModels;

public sealed record MeterViewModel(string Label, string Value, string Color, double Fraction, string Reset, bool Compact);
public sealed record CreditViewModel(string Label, string Value, string Expiry = "");
public sealed record ServiceViewModel(string Title, string Subtitle, string Brand, string Target, string? AccountId,
    string LoginTooltip, bool LoggingIn, bool Loading, string Plan, string ErrorTitle, string ErrorMessage,
    string ErrorHint, IReadOnlyList<MeterViewModel> Meters, IReadOnlyList<CreditViewModel> Credits)
{
    public bool HasError => ErrorTitle.Length > 0;
}

public sealed class PopoverViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ServiceViewModel> Services { get; private set; } = [];
    public string Footer { get; private set; } = "";
    public string DefaultLoginLabel { get; private set; } = "";
    public bool ShowDefaultLogin => DefaultLoginLabel.Length > 0;
    private bool settingsOpen;
    public bool SettingsOpen { get => settingsOpen; set { settingsOpen = value; Changed(); } }

    public void Update(ShellSnapshot snapshot, double now)
    {
        var services = new List<ServiceViewModel> { Build("Claude Code", "", "#d97706", "claude", null,
            snapshot.Claude, snapshot.LoginInProgress.Claude, now) };
        if (snapshot.CodexAccounts.Count == 0)
            services.Add(Build("Codex", "", "#10a37f", "codex", null, null, false, now));
        foreach (var account in snapshot.CodexAccounts)
            services.Add(Build(account.DisplayName, string.IsNullOrEmpty(account.CustomName) ? "" : account.Account.Label,
                "#10a37f", "codex", account.Account.Id, account.Result,
                snapshot.LoginInProgress.Codex.GetValueOrDefault(account.Account.Id), now));
        Services = services;
        Footer = PopoverPresentation.Footer(snapshot.FetchedAt, snapshot.AppVersion);
        DefaultLoginLabel = snapshot.CodexAccounts.Count > 0 && !snapshot.CodexAccounts.Any(a => a.Account.IsDefault)
            ? $"既定ホーム ({snapshot.CodexDefaultAccount.Label}) にログイン" : "";
        Changed(nameof(Services)); Changed(nameof(Footer)); Changed(nameof(DefaultLoginLabel)); Changed(nameof(ShowDefaultLogin));
    }

    private static ServiceViewModel Build(string title, string subtitle, string brand, string target, string? id,
        ServiceResult? result, bool loggingIn, double now)
    {
        var meters = new List<MeterViewModel>();
        var credits = new List<CreditViewModel>();
        var errorTitle = ""; var errorMessage = ""; var hint = "";
        if (result is { Ok: false })
        {
            var reauth = loggingIn && result.Error?.Code is "claude_unauthorized" or "claude_credentials_missing";
            errorTitle = reauth ? "⟳ 再認証中…" : "⚠ 取得失敗";
            errorMessage = reauth ? "ブラウザで Anthropic の承認画面が開きます。完了すると自動で復帰します。" : result.Error?.Message ?? "原因不明";
            if (!reauth) hint = PopoverPresentation.ErrorHint(result.Error?.Code);
        }
        var usage = result is { Ok: true } ? result.Data : null;
        void AddMeter(string label, RateLimit limit, bool compact) => meters.Add(new(label,
            TrayPresentation.PercentLabel(double.IsFinite(limit.Utilization) ? limit.Utilization : null),
            PopoverPresentation.Color(limit.Utilization), PopoverPresentation.BarFraction(limit.Utilization),
            PopoverPresentation.ResetText(limit.ResetsAt, now), compact));
        if (usage?.FiveHour is { } five) AddMeter("5時間", five, false);
        if (usage?.Weekly is { } weekly) AddMeter(PopoverPresentation.WeeklyLabel(weekly.Utilization, weekly.ResetsAt, now), weekly, true);
        foreach (var scope in usage?.WeeklyScoped ?? [])
            AddMeter(PopoverPresentation.WeeklyLabel(scope.Utilization, scope.ResetsAt, now, scope.Label), new(scope.Utilization, scope.ResetsAt), true);
        if (usage?.CloudCredit is { } cloud && PopoverPresentation.Credit(Math.Max(0, cloud.Remaining), "USD", true) is { } remaining &&
            PopoverPresentation.Credit(cloud.Limit, "USD") is { } limit)
            meters.Add(new("クラウドクレジット", $"{remaining} / {limit}", PopoverPresentation.Color(cloud.Utilization),
                PopoverPresentation.BarFraction(cloud.Utilization), string.Join("・", new[] { PopoverPresentation.CloudExpiry(cloud.ExpiresAt, now),
                    cloud.Locked is not null ? "利用停止中" : "" }.Where(s => s.Length > 0)), true));
        if (usage?.Credits is { } balance && (balance.Unlimited ? "無制限" : PopoverPresentation.Credit(balance.Amount, balance.Currency)) is { } value)
            credits.Add(new("クレジット残高", value));
        if (usage?.ResetCredits is { } reset)
            credits.Add(new("リセット権", $"{reset.AvailableCount} 回", reset.NextExpiresAt is { } expiry && double.IsFinite(expiry) && expiry > now
                ? $"最短 {PopoverPresentation.Local(expiry):M/d} 失効" : ""));
        return new(title, subtitle, brand, target, id, target == "claude" ? "claude login を実行" : $"{title} の codex login を実行",
            loggingIn, result is null, usage?.Plan is { Length: > 0 } plan ? $"Plan: {plan}" : "",
            errorTitle, errorMessage, hint, meters, credits);
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
