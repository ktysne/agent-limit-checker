using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Settings;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class TrayPresentationTests
{
    private static CodexAccount Default => new("default", ".codex", "C:/test/.codex", "C:/test/.codex/auth.json", true);
    private static ServiceResult Success(double? utilization) => new(true, new(utilization.HasValue ? new RateLimit(utilization.Value, null) : null, null, [], null, null, null));
    private static ShellSnapshot Snapshot(ServiceResult? claude = null, params AccountResult[] accounts) => new(
        claude, accounts, Default, 0, new AppSettings { PollingIntervalSec = 120 }, true, false, "light", "4.0.0", new(false, new Dictionary<string, bool>()));
    private static AccountResult Account(string id, string label, string name, ServiceResult result, bool isDefault = false) =>
        new(new(id, label, "C:/test/" + label, "C:/test/" + label + "/auth.json", isDefault), result, name, null, name);

    [Theory]
    [InlineData(null, "--%")]
    [InlineData(double.NaN, "--%")]
    [InlineData(-.4, "0%")]
    [InlineData(.125, "13%")]
    [InlineData(.7, "70%")]
    [InlineData(1d, "100%")]
    [InlineData(1.001, "100%+")]
    public void PercentLabelsMatchJavascriptRoundingAndBoundaries(double? value, string expected) => Assert.Equal(expected, TrayPresentation.PercentLabel(value));

    [Theory]
    [InlineData("claude_credentials_missing", "login required")]
    [InlineData("claude_unauthorized", "login required")]
    [InlineData("claude_rate_limited", "rate limited")]
    [InlineData("codex_cli_missing", "CLI missing")]
    [InlineData("codex_rpc_error", "login required")]
    [InlineData("codex_home_missing", "login required")]
    [InlineData("codex_timeout", "timeout")]
    [InlineData("claude_timeout", "timeout")]
    [InlineData("claude_network", "network error")]
    [InlineData("unknown", "error")]
    public void ErrorSummariesMatchElectron(string code, string expected) => Assert.Equal(expected, TrayPresentation.ErrorSummary(new(code, "ignored")));

    [Fact]
    public void TooltipUsesTitleClaudeAndOneLinePerCodexAccount()
    {
        Assert.Equal("Agent Limit Checker\nClaude: 取得中\nCodex: 取得中", TrayPresentation.Tooltip(Snapshot()));
        var snapshot = Snapshot(Success(.125), Account("default", ".codex", "Codex Main", Success(.9), true),
            Account("review", ".codex-review", "Review", new(false, Error: new("codex_timeout", "ignored"))));
        Assert.Equal("Agent Limit Checker\nClaude: 13%\nCodex Main: 90%\nReview: timeout", TrayPresentation.Tooltip(snapshot));
        Assert.Equal("Claude: --%", TrayPresentation.ServiceStatusLabel("Claude", Success(null)));
    }

    [Fact]
    public void TooltipNeverExceedsNotifyIconLimitOrSplitsSurrogatePair()
    {
        var snapshot = Snapshot(Success(.5), Account("large", ".codex-review", new string('界', 180), Success(.5)));
        var text = TrayPresentation.Tooltip(snapshot);
        Assert.Equal(127, text.Length);
        Assert.EndsWith("…", text);
        var name = new string('x', 81) + "😀" + new string('x', 100);
        text = TrayPresentation.Tooltip(Snapshot(Success(.5), Account("large", ".codex-review", name, Success(.5))));
        Assert.True(text.Length <= 127);
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public void MenuPreservesOrderDisabledUsageCheckboxAndIntervalRadioItems()
    {
        var menu = TrayPresentation.Menu(Snapshot(Success(.5), Account("default", ".codex", "Codex", Success(.9), true)));
        Assert.Equal("Claude 5h: 50%", menu[0].Label);
        Assert.Equal("Codex 5h:  90%", menu[1].Label);
        Assert.False(menu[0].Enabled);
        Assert.False(menu[1].Enabled);
        Assert.Equal(new[] { "", "詳細を表示", "今すぐ更新", "", "claude login (新しいターミナルで実行)",
            "codex login (新しいターミナルで実行)", "", "ログイン時に自動起動", "更新間隔", "", "終了" }, menu.Skip(2).Select(m => m.Label));
        Assert.All(menu.Where(m => m.Label == ""), m => Assert.Equal(TrayMenuKind.Separator, m.Kind));
        var autoLaunch = Assert.Single(menu, m => m.Command == "auto-launch");
        Assert.Equal(TrayMenuKind.Checkbox, autoLaunch.Kind);
        Assert.True(autoLaunch.Checked);
        var intervals = Assert.Single(menu, m => m.Children is not null).Children!;
        Assert.Equal(new[] { "30秒", "1分", "2分", "5分", "10分" }, intervals.Select(m => m.Label));
        Assert.Equal(new[] { "30", "60", "120", "300", "600" }, intervals.Select(m => m.Argument));
        Assert.All(intervals, m => Assert.Equal(TrayMenuKind.Radio, m.Kind));
        Assert.Equal("120", Assert.Single(intervals, m => m.Checked).Argument);
    }

    [Fact]
    public void MenuAppendsMissingDefaultLoginAndUsesDiscoveredDisplayName()
    {
        var menu = TrayPresentation.Menu(Snapshot(null, Account("review", ".codex-review", "レビュー", Success(.5))));
        var logins = menu.Where(m => m.Command == "codex-login").ToArray();
        Assert.Equal("codex login レビュー (新しいターミナルで実行)", logins[0].Label);
        Assert.Equal("review", logins[0].Argument);
        Assert.Equal("codex login .codex (既定ホームを作成)", logins[1].Label);
        Assert.Equal(Default.Id, logins[1].Argument);
    }

    [Fact]
    public void InitialMenuHasDisabledUnknownUsageAndDefaultLogin()
    {
        var menu = TrayPresentation.Menu(Snapshot());
        Assert.Equal("Claude 5h: --%", menu[0].Label);
        Assert.Equal("Codex 5h:  --%", menu[1].Label);
        Assert.Equal(Default.Id, Assert.Single(menu, m => m.Command == "codex-login").Argument);
    }

    [Fact]
    public void WorstCodexUtilizationIgnoresFailedAndMissingFiveHourData()
    {
        var snapshot = Snapshot(null, Account("first", ".codex", "Codex", Success(.2)),
            Account("second", ".codex-other", "Other", Success(.8)),
            Account("failed", ".codex-failed", "Failed", new(false, Data: Success(1).Data)),
            Account("empty", ".codex-empty", "Empty", Success(null)));
        Assert.Equal(.8, TrayPresentation.CodexUtilization(snapshot));
        Assert.Null(TrayPresentation.CodexUtilization(Snapshot()));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void TrayBitmapDrawsAtRequestedResolutionWithPremultipliedChannels(int size)
    {
        var initial = TrayBitmap.Draw(size, null, null);
        var usage = TrayBitmap.Draw(size, .5, .9);
        var error = TrayBitmap.Draw(size, .5, .9, true, true);
        Assert.Equal(size * size * 4, initial.Length);
        Assert.False(initial.SequenceEqual(usage));
        Assert.False(usage.SequenceEqual(error));
        for (var i = 0; i < error.Length; i += 4)
            for (var channel = 0; channel < 3; channel++) Assert.True(error[i + channel] <= error[i + 3]);
        Assert.Contains(usage, channel => channel != 0);
    }

    [Fact]
    public void UtilizationColorsMatchElectronThresholds()
    {
        Assert.Equal((120, 120, 120), TrayBitmap.ColorForUtilization(null, false));
        Assert.Equal((160, 160, 160), TrayBitmap.ColorForUtilization(.9, true));
        Assert.Equal((76, 175, 80), TrayBitmap.ColorForUtilization(.699, false));
        Assert.Equal((255, 152, 0), TrayBitmap.ColorForUtilization(.7, false));
        Assert.Equal((244, 67, 54), TrayBitmap.ColorForUtilization(.85, false));
    }

    // 期待値の再生成は node dotnet/AgentLimitChecker.Tests/Fixtures/tray-reference.cjs。
    [Theory]
    [InlineData(null, null, false, "1525057CE021A88AA218EBB306F0A7704E415D8C3428E3030A79662FF4AFCE70")]
    [InlineData(.45, .9, false, "D6FDCFB6F02BE51ED101E5F727C374A0159EC983C6CDA27F0219D1F2C7ED92D9")]
    [InlineData(.45, .9, true, "93A52DCC92F007B8A77DC8EC1F3C947718D1C3EF25A3D4ADE1D82899F0E72CC6")]
    public void ThirtyTwoPixelBitmapMatchesElectronPixels(double? claude, double? codex, bool error, string hash) =>
        Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TrayBitmap.Draw(32, claude, codex, error, error))));
}
