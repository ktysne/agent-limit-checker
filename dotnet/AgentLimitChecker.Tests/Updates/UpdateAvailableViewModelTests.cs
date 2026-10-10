using AgentLimitChecker.App.ViewModels;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;
public class UpdateAvailableViewModelTests
{
    private static readonly Uri DownloadUrl = new("https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip");

    private static readonly PreparedUpdate Prepared = new("0123456789abcdef0123456789abcdef", @"C:\u\id", @"C:\u\id\app", Reused: false);

    private static UpdateAvailableViewModel Create() => new("0.1.0", "1.2.3", DownloadUrl);

    private static (UpdateAvailableViewModel ViewModel, List<UpdateDialogChoice> Closed) CreateAuto(
        Func<IProgress<UpdatePreparationProgress>, CancellationToken, Task<PreparedUpdate>> prepare,
        Func<PreparedUpdate, bool>? startApplier = null)
    {
        var viewModel = new UpdateAvailableViewModel("0.1.0", "1.2.3", DownloadUrl, null, prepare, startApplier ?? (_ => true));
        var closed = new List<UpdateDialogChoice>();
        viewModel.CloseRequested += (_, choice) => closed.Add(choice);
        return (viewModel, closed);
    }

    [Fact]
    public void VersionTexts_AreLabelled()
    {
        var viewModel = Create();

        Assert.Equal("現在のバージョン: 0.1.0", viewModel.CurrentVersionText);
        Assert.Equal("新しいバージョン: 1.2.3", viewModel.NewVersionText);
    }

    [Fact]
    public void DownloadUrlText_ShowsFullUrl()
    {
        var viewModel = Create();

        Assert.Equal("https://ktysne.info/agent-limit-checker/archives/AgentLimitChecker-1.2.3-win-x64.zip", viewModel.DownloadUrlText);
        Assert.Equal(viewModel.DownloadUrl.ToString(), viewModel.DownloadUrlText);
    }
    [Fact]
    public void NewVersion_KeepsManifestText()
    {
        Assert.Equal("1.2.3", Create().NewVersion);
    }

    [Fact]
    public void NullDownloadUrl_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateAvailableViewModel("0.1.0", "1.2.3", null!));
    }

    [Fact]
    public async Task WithoutAutoApply_PrimaryIsDownloadAndClosesWithDownload()
    {
        var viewModel = new UpdateAvailableViewModel("0.1.0", "1.2.3", DownloadUrl, "インストール先に書き込めないため、自動では更新できません。", null, null);
        var closed = new List<UpdateDialogChoice>();
        viewModel.CloseRequested += (_, choice) => closed.Add(choice);

        Assert.False(viewModel.CanAutoApply);
        Assert.Equal("配布ページを開く", viewModel.PrimaryButtonText);
        Assert.Contains("インストール先に書き込めない", viewModel.HintText);

        await viewModel.ExecutePrimaryAsync();

        Assert.Equal([UpdateDialogChoice.Download], closed);
    }

    [Fact]
    public void WithAutoApply_InitialButtons()
    {
        var (viewModel, _) = CreateAuto((_, _) => Task.FromResult(Prepared));

        Assert.Equal("更新する", viewModel.PrimaryButtonText);
        Assert.Equal("閉じる", viewModel.SecondaryButtonText);
        Assert.True(viewModel.IsPrimaryEnabled);
        Assert.True(viewModel.IsSkipEnabled);
        Assert.False(viewModel.IsOpenBrowserVisible);
        Assert.Null(viewModel.StageText);
    }

    [Fact]
    public async Task Primary_PreparesThenStartsApplierAndClosesWithApplied()
    {
        PreparedUpdate? started = null;
        var (viewModel, closed) = CreateAuto((_, _) => Task.FromResult(Prepared), prepared =>
        {
            started = prepared;
            return true;
        });

        await viewModel.ExecutePrimaryAsync();

        Assert.Same(Prepared, started);
        Assert.Equal([UpdateDialogChoice.Applied], closed);
        Assert.Equal(UpdateDialogState.Applying, viewModel.State);
        Assert.Equal("アプリを終了して更新します。", viewModel.StageText);
        Assert.False(viewModel.IsSecondaryEnabled);
        Assert.False(viewModel.TryClose());
    }

    [Fact]
    public async Task WhilePreparing_ButtonsSwitchToCancelAndSkipIsDisabled()
    {
        var gate = new TaskCompletionSource<PreparedUpdate>();
        var (viewModel, _) = CreateAuto((_, _) => gate.Task);

        var running = viewModel.ExecutePrimaryAsync();

        Assert.Equal(UpdateDialogState.Preparing, viewModel.State);
        Assert.Equal("準備しています…", viewModel.StageText);
        Assert.Equal("中止", viewModel.SecondaryButtonText);
        Assert.True(viewModel.IsSecondaryEnabled);
        Assert.False(viewModel.IsPrimaryEnabled);
        Assert.False(viewModel.IsSkipEnabled);

        gate.SetResult(Prepared);
        await running;
    }

    [Fact]
    public async Task ProgressUpdatesStageAndSizeText()
    {
        var gate = new TaskCompletionSource<PreparedUpdate>();
        var (viewModel, _) = CreateAuto((_, _) => gate.Task);
        var running = viewModel.ExecutePrimaryAsync();

        viewModel.ApplyProgress(new UpdatePreparationProgress(
            UpdatePreparationStage.Downloading, new UpdateDownloadProgress(1_572_864, 10_485_760)));

        Assert.Equal("ダウンロード中です。", viewModel.StageText);
        Assert.Equal("1.5 MB / 10.0 MB", viewModel.ProgressText);
        Assert.Equal(15, viewModel.ProgressPercent);
        Assert.False(viewModel.IsProgressIndeterminate);

        viewModel.ApplyProgress(new UpdatePreparationProgress(
            UpdatePreparationStage.Downloading, new UpdateDownloadProgress(1_048_576, null)));
        Assert.Equal("1.0 MB / 不明", viewModel.ProgressText);
        Assert.True(viewModel.IsProgressIndeterminate);

        viewModel.ApplyProgress(new UpdatePreparationProgress(UpdatePreparationStage.Verifying));
        Assert.Equal("ダウンロードしたファイルを照合しています。", viewModel.StageText);
        Assert.Null(viewModel.ProgressText);

        viewModel.ApplyProgress(new UpdatePreparationProgress(UpdatePreparationStage.Extracting));
        Assert.Equal("ダウンロードしたファイルを展開しています。", viewModel.StageText);

        gate.SetResult(Prepared);
        await running;
    }

    [Fact]
    public async Task PreparationFailure_ShowsRetryAndBrowserButtons()
    {
        var (viewModel, closed) = CreateAuto((_, _) => Task.FromException<PreparedUpdate>(
            new UpdatePreparationException("ダウンロードしたファイルが配布サイトの情報と一致しません。")));

        await viewModel.ExecutePrimaryAsync();

        Assert.Empty(closed);
        Assert.Equal(UpdateDialogState.Failed, viewModel.State);
        Assert.Equal("更新の準備に失敗しました。", viewModel.StageText);
        Assert.Equal("ダウンロードしたファイルが配布サイトの情報と一致しません。", viewModel.FailureDetail);
        Assert.Equal("もう一度試す", viewModel.PrimaryButtonText);
        Assert.Equal("閉じる", viewModel.SecondaryButtonText);
        Assert.True(viewModel.IsOpenBrowserVisible);
        Assert.True(viewModel.IsSkipEnabled);

        viewModel.ExecuteOpenBrowser();
        Assert.Equal([UpdateDialogChoice.Download], closed);
    }

    [Fact]
    public async Task BrowserCannotCloseTheDialogWhilePreparationIsRunning()
    {
        var gate = new TaskCompletionSource<PreparedUpdate>();
        var (viewModel, closed) = CreateAuto((_, _) => gate.Task);
        var running = viewModel.ExecutePrimaryAsync();
        viewModel.ExecuteOpenBrowser();
        Assert.Empty(closed);
        gate.SetResult(Prepared);
        await running;
        Assert.Equal([UpdateDialogChoice.Applied], closed);
    }

    [Fact]
    public async Task ApplierStartFailure_ReturnsToFailedStateWithoutClosing()
    {
        var (viewModel, closed) = CreateAuto((_, _) => Task.FromResult(Prepared), _ => false);

        await viewModel.ExecutePrimaryAsync();

        Assert.Empty(closed);
        Assert.Equal(UpdateDialogState.Failed, viewModel.State);
        Assert.Equal("更新を開始できませんでした。", viewModel.FailureDetail);
        Assert.Equal("もう一度試す", viewModel.PrimaryButtonText);
    }

    [Fact]
    public async Task Retry_AfterFailure_RunsPreparationAgain()
    {
        var attempts = 0;
        var (viewModel, closed) = CreateAuto((_, _) => ++attempts == 1
            ? Task.FromException<PreparedUpdate>(new UpdatePreparationException("失敗"))
            : Task.FromResult(Prepared));

        await viewModel.ExecutePrimaryAsync();
        await viewModel.ExecutePrimaryAsync();

        Assert.Equal(2, attempts);
        Assert.Equal([UpdateDialogChoice.Applied], closed);
    }

    [Fact]
    public async Task Cancel_DuringPreparation_ReturnsToIdleWithoutClosing()
    {
        var (viewModel, closed) = CreateAuto(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Prepared;
        });

        var running = viewModel.ExecutePrimaryAsync();
        viewModel.ExecuteSecondary();
        await running;

        Assert.Empty(closed);
        Assert.Equal(UpdateDialogState.Idle, viewModel.State);
        Assert.Equal("閉じる", viewModel.SecondaryButtonText);
    }

    [Fact]
    public async Task Cancel_WhenPreparationCompletesWithoutObservingIt_DoesNotStartApplier()
    {
        var gate = new TaskCompletionSource<PreparedUpdate>();
        var applierStarted = false;
        var (viewModel, closed) = CreateAuto((_, _) => gate.Task, _ => applierStarted = true);

        var running = viewModel.ExecutePrimaryAsync();
        viewModel.ExecuteSecondary();
        gate.SetResult(Prepared);
        await running;

        Assert.False(applierStarted);
        Assert.Empty(closed);
        Assert.Equal(UpdateDialogState.Idle, viewModel.State);
    }

    [Fact]
    public async Task CancelRequest_DisablesCancelButtonUntilPreparationStops()
    {
        var gate = new TaskCompletionSource<PreparedUpdate>();
        var (viewModel, _) = CreateAuto((_, _) => gate.Task);

        var running = viewModel.ExecutePrimaryAsync();
        viewModel.ExecuteSecondary();

        Assert.True(viewModel.IsCancelRequested);
        Assert.False(viewModel.IsSecondaryEnabled);

        gate.SetException(new OperationCanceledException());
        await running;
        Assert.False(viewModel.IsCancelRequested);
        Assert.Equal(UpdateDialogState.Idle, viewModel.State);
    }

    [Fact]
    public async Task ClosingWindow_DuringPreparation_CancelsThenClosesWithRemindLater()
    {
        var (viewModel, closed) = CreateAuto(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Prepared;
        });

        var running = viewModel.ExecutePrimaryAsync();
        Assert.False(viewModel.TryClose());
        await running;

        Assert.Equal([UpdateDialogChoice.RemindLater], closed);
    }

    [Fact]
    public void Secondary_WhenIdle_ClosesWithRemindLater()
    {
        var (viewModel, closed) = CreateAuto((_, _) => Task.FromResult(Prepared));

        viewModel.ExecuteSecondary();
        viewModel.ExecuteSkip();

        Assert.Equal([UpdateDialogChoice.RemindLater, UpdateDialogChoice.Cancel], closed);
        Assert.True(viewModel.TryClose());
    }

    [Fact]
    public async Task LateProgressAfterFailure_IsIgnored()
    {
        var (viewModel, _) = CreateAuto((_, _) => Task.FromException<PreparedUpdate>(new UpdatePreparationException("失敗")));
        await viewModel.ExecutePrimaryAsync();

        viewModel.ApplyProgress(new UpdatePreparationProgress(UpdatePreparationStage.Downloading, new UpdateDownloadProgress(1, 2)));

        Assert.Equal(UpdateDialogState.Failed, viewModel.State);
    }
}
