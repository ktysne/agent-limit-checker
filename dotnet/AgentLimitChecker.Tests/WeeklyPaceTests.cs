using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class WeeklyPaceTests
{
    private const double DayMilliseconds = 86_400_000;
    [Fact] public void 使用率10パーセントで残り6日の配分を表示する() =>
        Assert.Equal("今日あと18.6% · 6日均等なら15.0%/日", PopoverPresentation.WeeklyPace(.1, 6 * DayMilliseconds, 0));
    [Fact] public void 今日の配分を超過した場合は超過表示にする() =>
        Assert.Equal("今日の枠 超過 · 6日均等なら10.8%/日", PopoverPresentation.WeeklyPace(.35, 6 * DayMilliseconds, 0));
    [Fact] public void 丸め後に残り枠がゼロなら超過表示にする() =>
        Assert.Equal("今日の枠 超過 · 2日均等なら7.1%/日", PopoverPresentation.WeeklyPace(.857, 2 * DayMilliseconds, 0));
    [Fact] public void 最終日は週次配分を表示しない() =>
        Assert.Equal("週次", PopoverPresentation.WeeklyLabel(.1, DayMilliseconds, 0));
    [Fact] public void リセット時刻がないか過去なら配分を表示しない()
    {
        Assert.Equal("週次", PopoverPresentation.WeeklyLabel(.1, null, 0));
        Assert.Equal("週次", PopoverPresentation.WeeklyLabel(.1, 0, 0));
    }
    [Fact] public void 使用率が未取得かNaNなら配分を表示しない()
    {
        Assert.Equal("週次", PopoverPresentation.WeeklyLabel(null, 6 * DayMilliseconds, 0));
        Assert.Equal("週次", PopoverPresentation.WeeklyLabel(double.NaN, 6 * DayMilliseconds, 0));
    }
    [Fact] public void 使用率が100パーセントを超えると均等配分をゼロと表示する() =>
        Assert.Equal("今日の枠 超過 · 6日均等なら0.0%/日", PopoverPresentation.WeeklyPace(1.2, 6 * DayMilliseconds, 0));
    [Fact] public void リセット直後の残り7日を配分に反映する() =>
        Assert.Equal("今日あと14.3% · 7日均等なら14.3%/日", PopoverPresentation.WeeklyPace(0, 7 * DayMilliseconds, 0));
    [Fact] public void モデル名の後に配分を表示する() =>
        Assert.Equal("週次 (Sonnet & Opus, 今日あと18.6% · 6日均等なら15.0%/日)",
            PopoverPresentation.WeeklyLabel(.1, 6 * DayMilliseconds, 0, "Sonnet & Opus"));
    [Fact] public void 配分を表示できないモデル別ラベルはモデル名だけを表示する() =>
        Assert.Equal("週次 (Sonnet)", PopoverPresentation.WeeklyLabel(.1, DayMilliseconds, 0, "Sonnet"));
}
