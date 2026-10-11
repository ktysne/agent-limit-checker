using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class TrayClickGuardTests
{
    [Fact]
    public void アイコンの押下で閉じた直後のクリックは開き直さない()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        guard.OnTrayPressed(1010);
        Assert.True(guard.ConsumeSuppression());
    }

    [Fact]
    public void 押下の通知が非アクティブ化より先に届いても開き直さない()
    {
        var guard = new TrayClickGuard();
        guard.OnTrayPressed(1000);
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1010);
        Assert.True(guard.ConsumeSuppression());
    }

    [Fact]
    public void 抑止は1回のクリックで消費する()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        guard.ConsumeSuppression();
        guard.OnTrayPressed(5000);
        Assert.False(guard.ConsumeSuppression());
    }

    [Fact]
    public void クリックが届かずに残った記録は次の別の押下で捨てる()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        guard.OnTrayPressed(1000 + TrayClickGuard.SamePressWindowMs + 1);
        Assert.False(guard.ConsumeSuppression());
    }

    [Fact]
    public void アイコン以外の場所で閉じた後のクリックは開く()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: false, nowMs: 1000);
        guard.OnTrayPressed(1200);
        Assert.False(guard.ConsumeSuppression());
    }

    [Fact]
    public void 別の経路で表示した後は抑止しない()
    {
        var guard = new TrayClickGuard();
        guard.OnHiddenByDeactivation(pointerOnTray: true, nowMs: 1000);
        guard.Reset();
        Assert.False(guard.ConsumeSuppression());
    }
}
