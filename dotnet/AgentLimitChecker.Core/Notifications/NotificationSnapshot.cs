using AgentLimitChecker.Core.Providers;

namespace AgentLimitChecker.Core.Notifications;

/// <summary>通知対象サービスの識別子、表示名、取得済み利用状況をまとめる。</summary>
public sealed record NotificationServiceSnapshot(
    string ServiceId,
    string DisplayName,
    bool IsAvailable,
    UsageSnapshot? Usage);

/// <summary>リセット時刻とリセット権期限の通知に使うサービス別利用状況。</summary>
public sealed record NotificationSnapshot(
    NotificationServiceSnapshot? Claude,
    IReadOnlyList<NotificationServiceSnapshot> CodexAccounts);
