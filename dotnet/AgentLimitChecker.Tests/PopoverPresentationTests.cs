using AgentLimitChecker.Core.Settings;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class PopoverPresentationTests
{
    [Theory]
    [InlineData(null, "ウィンドウ未開始 (このウィンドウでまだ消費なし)")]
    [InlineData(0d, "まもなくリセット")]
    [InlineData(41 * 60 * 1000d, "あと 41分 (00:41 リセット)")]
    [InlineData((86400 + 3600 + 60) * 1000d, "あと 1日1時間1分 (01:01 リセット)")]
    public void リセットの相対時間と絶対時刻を表示する(double? reset, string expected) =>
        Assert.Equal(expected, PopoverPresentation.ResetText(reset, 0, TimeZoneInfo.Utc));

    [Theory]
    [InlineData(115.9354, null, false, "115.94 クレジット")]
    [InlineData(248.23, "USD", false, "$248.23")]
    [InlineData(12345.67, "USD", false, "$12,345.67")]
    [InlineData(0, "USD", false, null)]
    [InlineData(0, "USD", true, "$0.00")]
    [InlineData(-1, null, true, null)]
    [InlineData(double.NaN, null, true, null)]
    public void クレジットの単位とゼロの非表示を守る(double amount, string? currency, bool zero, string? expected) =>
        Assert.Equal(expected, PopoverPresentation.Credit(amount, currency, zero));

    [Theory]
    [InlineData(.6999, "#4caf50")][InlineData(.7, "#ff9800")][InlineData(.8499, "#ff9800")][InlineData(.85, "#f44336")]
    public void 色の閾値を守る(double utilization, string expected) => Assert.Equal(expected, PopoverPresentation.Color(utilization));

    [Theory]
    [InlineData(false, false, "通知は未選択です。Topic URL は推測されにくいものを使ってください。")]
    [InlineData(true, false, "リセット時刻通知が有効です。リセット時刻に ntfy へ送信します。")]
    [InlineData(false, true, "リセット権の期限通知が有効です。失効5時間前に ntfy へ送信します。")]
    [InlineData(true, true, "リセット時刻とリセット権の期限通知が有効です。ntfy へ送信します。")]
    public void 通知の選択に対応する案内を表示する(bool reset, bool expiry, string expected) =>
        Assert.Equal(expected, PopoverPresentation.NtfyStatus(new NtfySettings { TopicUrl = "https://ntfy.sh/test", NotifyFiveHour = reset, NotifyResetCreditsExpiry = expiry }));

    [Theory]
    [InlineData(500, 1040, 0, 0, 1920, 1040)]
    [InlineData(500, 0, 0, 40, 1920, 1040)]
    [InlineData(0, 500, 40, 0, 1880, 1080)]
    [InlineData(1900, 500, 0, 0, 1880, 1080)]
    [InlineData(-1000, 500, -1920, 0, 1920, 1040)]
    public void タスクバーの上下左右と負の座標でも作業領域に収める(double tx, double ty, double wx, double wy, double ww, double wh)
    {
        var work = new PopoverRect(wx, wy, ww, wh);
        var location = PopoverPlacement.Calculate(new(tx, ty, 24, 24), work, 500);
        Assert.InRange(location.Left, wx + 4, wx + ww - 364);
        Assert.InRange(location.Top, wy + 4, wy + wh - 504);
    }
    [Fact] public void 小さい作業領域では上下4の余白を引く() =>
        Assert.Equal(682, PopoverPlacement.MaximumHeight(new(0, 0, 1280, 690)));
    [Fact] public void 十分に広い作業領域でも高さの上限は900() =>
        Assert.Equal(900, PopoverPlacement.MaximumHeight(new(0, 0, 2560, 1400)));
}
