namespace AgentLimitChecker.Core.Shell;

/// <summary>トレイアイコンの押下で非アクティブ化して閉じたポップオーバーを、続くクリックで開き直さないための判定。</summary>
/// <remarks>非アクティブ化はアイコンの押下で起き、クリックの通知はボタンを離した後に届く。</remarks>
public sealed class TrayClickGuard
{
    /// <summary>非アクティブ化とアイコンの押下の通知を同じ押下によるものとみなす間隔。2 つの通知の順序は保証されない。</summary>
    public const long SamePressWindowMs = 500;
    private long? hiddenByTrayPressAtMs;

    public void OnHiddenByDeactivation(bool pointerOnTray, long nowMs) =>
        hiddenByTrayPressAtMs = pointerOnTray ? nowMs : null;

    /// <summary>押下が記録より後の別の押下なら、クリックが届かずに残った記録を捨てる。</summary>
    public void OnTrayPressed(long nowMs)
    {
        if (hiddenByTrayPressAtMs is { } at && nowMs - at > SamePressWindowMs) hiddenByTrayPressAtMs = null;
    }

    public void Reset() => hiddenByTrayPressAtMs = null;

    /// <summary>直前の非アクティブ化がこのクリックの押下によるものなら true を返す。判定は 1 回で消費する。</summary>
    public bool ConsumeSuppression()
    {
        var suppress = hiddenByTrayPressAtMs is not null;
        hiddenByTrayPressAtMs = null;
        return suppress;
    }
}
