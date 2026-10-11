namespace AgentLimitChecker.Core.Shell;

/// <summary>トレイアイコンの押下で非アクティブ化して閉じたポップオーバーを、続くクリックで開き直さないための判定。</summary>
/// <remarks>非アクティブ化はアイコンの押下で起き、クリックの通知はボタンを離した後に届く。</remarks>
public sealed class TrayClickGuard
{
    public const long SuppressWindowMs = 1000;
    private long? hiddenByTrayPressAtMs;

    public void OnHiddenByDeactivation(bool pointerOnTray, long nowMs) =>
        hiddenByTrayPressAtMs = pointerOnTray ? nowMs : null;

    public void Reset() => hiddenByTrayPressAtMs = null;

    /// <summary>直前の非アクティブ化がこのクリックの押下によるものなら true を返す。判定は 1 回で消費する。</summary>
    public bool ConsumeSuppression(long nowMs)
    {
        var at = hiddenByTrayPressAtMs;
        hiddenByTrayPressAtMs = null;
        return at is { } pressedAt && nowMs - pressedAt <= SuppressWindowMs;
    }
}
