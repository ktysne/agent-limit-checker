using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class TrayClickGuardTests
{
    [Fact]
    public void アイコンの押下で閉じた直後のクリックは開き直さない()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        Assert.True(guard.ConsumeSuppression(1200));
    }

    [Fact]
    public void 抑止は1回のクリックで消費する()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        guard.ConsumeSuppression(1200);
        Assert.False(guard.ConsumeSuppression(1300));
    }

    [Fact]
    public void アイコン以外の場所で閉じた後のクリックは開く()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: false, nowMs: 1000);
        Assert.False(guard.ConsumeSuppression(1200));
    }

    [Fact]
    public void 押下から時間が経ったクリックは開く()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        Assert.False(guard.ConsumeSuppression(1000 + TrayClickGuard.SuppressWindowMs + 1));
    }

    [Fact]
    public void 別の経路で表示した後は抑止しない()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        guard.Reset();
        Assert.False(guard.ConsumeSuppression(1200));
    }
}
