using System.Globalization;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Core.Shell;

public static class PopoverPresentation
{
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
    public static string? WeeklyPace(double? utilization, double? resetsAt, double now)
    {
        if (utilization is not { } used || !double.IsFinite(used) || resetsAt is not { } reset ||
            !double.IsFinite(reset) || !double.IsFinite(now) || reset <= now) return null;
        var days = Math.Clamp(Math.Ceiling((reset - now) / 86_400_000), 1, 7);
        if (days <= 1) return null;
        var today = 100.0 / 7 * (8 - days) - used * 100;
        var perDay = Math.Max(0, 100 - used * 100) / days;
        return (Math.Round(today, 1, MidpointRounding.AwayFromZero) <= 0 ? "今日の枠 超過" : $"今日あと{Fixed(today, 1)}%") +
            $" · {days}日均等なら{Fixed(perDay, 1)}%/日";
    }

    public static string WeeklyLabel(double? utilization, double? resetsAt, double now, string? scope = null)
    {
        var pace = WeeklyPace(utilization, resetsAt, now);
        var details = new[] { scope, pace }.Where(s => s is not null);
        var label = string.Join(", ", details);
        return label.Length == 0 ? "週次" : $"週次 ({label})";
    }

    public static string Fixed(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero).ToString($"F{digits}", CultureInfo.InvariantCulture);
    public static string Color(double? utilization) => utilization is null || utilization < .7 ? "#4caf50" :
        utilization < .85 ? "#ff9800" : "#f44336";
    public static double BarFraction(double utilization) => double.IsFinite(utilization) ? Math.Clamp(utilization, 0, 1) : 0;

    public static string ResetText(double? resetsAt, double now, TimeZoneInfo? zone = null)
    {
        if (resetsAt is not { } reset || !double.IsFinite(reset)) return "ウィンドウ未開始 (このウィンドウでまだ消費なし)";
        if (reset <= now) return "まもなくリセット";
        var seconds = (long)Math.Floor((reset - now) / 1000 + .5);
        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;
        var relative = days > 0 ? $"{days}日{hours}時間{minutes}分" : hours > 0 ? $"{hours}時間{minutes}分" : $"{minutes}分";
        return $"あと {relative} ({Local(reset, zone):HH:mm} リセット)";
    }

    public static DateTimeOffset Local(double milliseconds, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds), zone ?? TimeZoneInfo.Local);

    public static string CloudExpiry(double? expiresAt, double now)
    {
        if (expiresAt is not { } expiry || !double.IsFinite(expiry)) return "";
        if (expiry <= now) return "失効済み";
        var hours = (long)Math.Floor((expiry - now) / 3_600_000);
        return $"あと {hours / 24}日{hours % 24}時間 ({Local(expiry).ToString("M/d H:mm", Japanese)} 失効)";
    }

    public static string? Credit(double? amount, string? currency, bool allowZero = false)
    {
        if (amount is not { } value || !double.IsFinite(value) || value < 0 || (!allowZero && value == 0)) return null;
        var number = Fixed(value, currency is "JPY" ? 0 : 2);
        if (currency is "USD" or "JPY" or "EUR" or "GBP")
            number = Math.Round(value, currency == "JPY" ? 0 : 2, MidpointRounding.AwayFromZero)
                .ToString(currency == "JPY" ? "N0" : "N2", Japanese);
        return currency switch { null or "" => $"{number} クレジット", "USD" => $"${number}", "JPY" => $"￥{number}",
            "EUR" => $"€{number}", "GBP" => $"£{number}", _ => $"{number} {currency}" };
    }

    public static string Footer(long fetchedAt, string version) => string.Join(" · ",
        new[] { string.IsNullOrEmpty(version) ? null : $"v{version}", fetchedAt == 0 ? null : $"最終更新: {Local(fetchedAt):HH:mm:ss}" }.Where(s => s is not null));

    public static string NtfyStatus(NtfySettings config)
    {
        var reset = config.NotifyFiveHour || config.NotifyWeekly;
        var expiry = config.NotifyResetCreditsExpiry;
        if (!reset && !expiry) return "通知は未選択です。Topic URL は推測されにくいものを使ってください。";
        if (string.IsNullOrEmpty(config.TopicUrl)) return "通知を送るには ntfy の Topic URL が必要です。";
        if (reset && expiry) return "リセット時刻とリセット権の期限通知が有効です。ntfy へ送信します。";
        return expiry ? "リセット権の期限通知が有効です。失効5時間前に ntfy へ送信します。" :
            "リセット時刻通知が有効です。リセット時刻に ntfy へ送信します。";
    }

    public static string ErrorHint(string? code) => code switch
    {
        "claude_credentials_missing" or "codex_rpc_error" => "🔑 ボタンを押すと再ログインできます。完了すると自動で復帰します。",
        "claude_unauthorized" => "OAuth トークンが無効です。🔑 ボタンから再ログインすると自動で復帰します。",
        "claude_refresh_token_missing" => "refresh token がありません。🔑 ボタンから再ログインすると自動で復帰します。",
        "codex_cli_missing" => "PowerShell で `npm i -g @openai/codex` を実行してください。",
        "codex_home_missing" => "🔑 ボタンで codex login を実行すると ~/.codex が作成され、完了すると自動で復帰します。",
        _ => ""
    };
}
