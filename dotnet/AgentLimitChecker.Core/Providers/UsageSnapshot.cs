namespace AgentLimitChecker.Core.Providers;

/// <summary>利用率とリセット時刻。ResetsAt はミリ秒の UNIX 時刻。</summary>
public sealed record RateLimit(double Utilization, double? ResetsAt);
/// <summary>モデル別の週の利用率。ResetsAt はミリ秒の UNIX 時刻。</summary>
public sealed record WeeklyScopedLimit(string? Id, string Label, double Utilization, double? ResetsAt);
public sealed record CreditBalance(double? Amount, string? Currency, bool Unlimited);
/// <summary>利用可能なリセット回数。NextExpiresAt はミリ秒の UNIX 時刻。</summary>
public sealed record ResetCreditBalance(double AvailableCount, double? NextExpiresAt);
/// <summary>クラウドクレジットの残高。ExpiresAt はミリ秒の UNIX 時刻。</summary>
public sealed record CloudCreditBalance(double Limit, double Used, double Remaining, double Utilization,
    double? ExpiresAt, string? Locked);

public sealed record UsageSnapshot(RateLimit? FiveHour, RateLimit? Weekly,
    IReadOnlyList<WeeklyScopedLimit> WeeklyScoped, CreditBalance? Credits,
    ResetCreditBalance? ResetCredits, string? Plan, CloudCreditBalance? CloudCredit = null);

public sealed class ProviderException(string code, string message, bool restartable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Restartable { get; } = restartable;
}
