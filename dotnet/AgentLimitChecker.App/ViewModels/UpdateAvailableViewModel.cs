using System.Globalization;
using AgentLimitChecker.Core.Updates;
using System.ComponentModel;

namespace AgentLimitChecker.App.ViewModels;
public enum UpdateDialogChoice
{
    Cancel,
    RemindLater,
    Download,
    Applied,
}
public enum UpdateDialogState
{
    Idle,
    Preparing,
    Downloading,
    Verifying,
    Extracting,
    Ready,
    Applying,
    Failed,
}
public sealed partial class UpdateAvailableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly Func<IProgress<UpdatePreparationProgress>, CancellationToken, Task<PreparedUpdate>>? _prepare;
    private readonly Func<PreparedUpdate, bool>? _startApplier;
    private CancellationTokenSource? _cancellation;
    private bool _closeAfterCancel;

    private UpdateDialogState state = UpdateDialogState.Idle;
    public UpdateDialogState State
    {
        get => state;
        private set
        {
            state = value;
            Changed(nameof(State));
            Changed(nameof(StageText));
            Changed(nameof(IsInProgress));
            Changed(nameof(IsProgressVisible));
            Changed(nameof(IsProgressIndeterminate));
            Changed(nameof(ProgressText));
            Changed(nameof(PrimaryButtonText));
            Changed(nameof(IsPrimaryEnabled));
            Changed(nameof(SecondaryButtonText));
            Changed(nameof(IsSecondaryEnabled));
            Changed(nameof(IsSkipEnabled));
            Changed(nameof(IsOpenBrowserVisible));
        }
    }

    private UpdateDownloadProgress? downloadProgress;
    public UpdateDownloadProgress? DownloadProgress
    {
        get => downloadProgress;
        private set
        {
            downloadProgress = value;
            Changed(nameof(DownloadProgress));
            Changed(nameof(ProgressText));
            Changed(nameof(ProgressPercent));
            Changed(nameof(IsProgressIndeterminate));
        }
    }

    private bool isCancelRequested;
    public bool IsCancelRequested
    {
        get => isCancelRequested;
        private set
        {
            isCancelRequested = value;
            Changed(nameof(IsCancelRequested));
            Changed(nameof(IsSecondaryEnabled));
        }
    }

    private string? failureDetail;
    public string? FailureDetail
    {
        get => failureDetail;
        private set
        {
            failureDetail = value;
            Changed(nameof(FailureDetail));
        }
    }
    public UpdateAvailableViewModel(string currentVersion, string newVersion, Uri downloadUrl)
        : this(currentVersion, newVersion, downloadUrl, autoApplyUnavailableReason: null, prepare: null, startApplier: null)
    {
    }
    public UpdateAvailableViewModel(
        string currentVersion,
        string newVersion,
        Uri downloadUrl,
        string? autoApplyUnavailableReason,
        Func<IProgress<UpdatePreparationProgress>, CancellationToken, Task<PreparedUpdate>>? prepare,
        Func<PreparedUpdate, bool>? startApplier)
    {
        ArgumentNullException.ThrowIfNull(downloadUrl);

        CurrentVersion = currentVersion;
        NewVersion = newVersion;
        DownloadUrl = downloadUrl;
        _prepare = prepare;
        _startApplier = startApplier;
        AutoApplyUnavailableReason = CanAutoApply ? null : autoApplyUnavailableReason;
    }
    public event EventHandler<UpdateDialogChoice>? CloseRequested;

    public string CurrentVersion { get; }

    public string NewVersion { get; }

    public Uri DownloadUrl { get; }

    public string CurrentVersionText => $"現在のバージョン: {CurrentVersion}";

    public string NewVersionText => $"新しいバージョン: {NewVersion}";

    public string DownloadUrlText => DownloadUrl.ToString();

    public bool CanAutoApply => _prepare is not null && _startApplier is not null;

    public string? AutoApplyUnavailableReason { get; }

    public string HintText => CanAutoApply
        ? "「更新する」を選ぶと、新しいバージョンをダウンロードして照合し、Agent Limit Checker を終了して更新します。更新後は新しいバージョンが起動します。"
          + "「閉じる」を選ぶと、後でトレイのメニューから更新できます。"
        : $"{(AutoApplyUnavailableReason is null ? string.Empty : AutoApplyUnavailableReason + Environment.NewLine)}"
          + "「配布ページを開く」を選ぶとブラウザで配布ページを開きます。「閉じる」を選ぶと、後でトレイのメニューから更新できます。";

    public bool IsInProgress => State is UpdateDialogState.Preparing or UpdateDialogState.Downloading
        or UpdateDialogState.Verifying or UpdateDialogState.Extracting or UpdateDialogState.Ready
        or UpdateDialogState.Applying;

    public string? StageText => State switch
    {
        UpdateDialogState.Preparing => "準備しています…",
        UpdateDialogState.Downloading => "ダウンロード中です。",
        UpdateDialogState.Verifying => "ダウンロードしたファイルを照合しています。",
        UpdateDialogState.Extracting => "ダウンロードしたファイルを展開しています。",
        UpdateDialogState.Ready => "更新の準備ができました。",
        UpdateDialogState.Applying => "アプリを終了して更新します。",
        UpdateDialogState.Failed => "更新の準備に失敗しました。",
        _ => null,
    };

    public bool IsProgressVisible => IsInProgress;

    public bool IsProgressIndeterminate => State != UpdateDialogState.Downloading || DownloadProgress?.TotalBytes is not > 0;

    public double ProgressPercent => DownloadProgress is { TotalBytes: > 0 } progress
        ? Math.Clamp(progress.ReceivedBytes * 100.0 / progress.TotalBytes.Value, 0, 100)
        : 0;

    public string? ProgressText => State == UpdateDialogState.Downloading && DownloadProgress is { } progress
        ? $"{FormatMegabytes(progress.ReceivedBytes)} / {(progress.TotalBytes is { } total ? FormatMegabytes(total) : "不明")}"
        : null;

    public string PrimaryButtonText => State == UpdateDialogState.Failed
        ? "もう一度試す"
        : CanAutoApply ? "更新する" : "配布ページを開く";

    public bool IsPrimaryEnabled => !IsInProgress;

    public string SecondaryButtonText => IsInProgress ? "中止" : "閉じる";

    public bool IsSecondaryEnabled => State is not (UpdateDialogState.Ready or UpdateDialogState.Applying) && !IsCancelRequested;

    public bool IsSkipEnabled => !IsInProgress;

    public bool IsOpenBrowserVisible => State == UpdateDialogState.Failed;
    public async Task ExecutePrimaryAsync()
    {
        if (!CanAutoApply)
        {
            RequestClose(UpdateDialogChoice.Download);
            return;
        }

        if (IsInProgress)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsCancelRequested = false;
        FailureDetail = null;
        DownloadProgress = null;
        State = UpdateDialogState.Preparing;
        try
        {
            var prepared = await _prepare!(new Progress<UpdatePreparationProgress>(ApplyProgress), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            State = UpdateDialogState.Ready;
            State = UpdateDialogState.Applying;
            if (_startApplier!(prepared))
            {
                RequestClose(UpdateDialogChoice.Applied);
                return;
            }

            Fail("更新を開始できませんでした。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            State = UpdateDialogState.Idle;
        }
        catch (Exception ex)
        {
            Fail(ex is UpdatePreparationException ? ex.Message : $"予期しないエラーが発生しました: {ex.Message}");
        }
        finally
        {
            _cancellation = null;
            IsCancelRequested = false;
        }

        if (_closeAfterCancel)
        {
            _closeAfterCancel = false;
            RequestClose(UpdateDialogChoice.RemindLater);
        }
    }
    public void ExecuteSecondary()
    {
        if (IsInProgress)
        {
            RequestCancel();
            return;
        }

        RequestClose(UpdateDialogChoice.RemindLater);
    }

    public void ExecuteSkip()
    {
        if (!IsInProgress)
        {
            RequestClose(UpdateDialogChoice.Cancel);
        }
    }

    public void ExecuteOpenBrowser()
    {
        if (!IsInProgress) RequestClose(UpdateDialogChoice.Download);
    }
    public bool TryClose()
    {
        if (!IsInProgress)
        {
            return true;
        }

        if (State is UpdateDialogState.Downloading or UpdateDialogState.Preparing
            or UpdateDialogState.Verifying or UpdateDialogState.Extracting)
        {
            _closeAfterCancel = true;
            RequestCancel();
        }

        return false;
    }
    internal void ApplyProgress(UpdatePreparationProgress progress)
    {
        if (State is not (UpdateDialogState.Preparing or UpdateDialogState.Downloading
            or UpdateDialogState.Verifying or UpdateDialogState.Extracting))
        {
            return;
        }

        switch (progress.Stage)
        {
            case UpdatePreparationStage.Preparing:
                State = UpdateDialogState.Preparing;
                break;
            case UpdatePreparationStage.Downloading:
                DownloadProgress = progress.Download ?? DownloadProgress;
                State = UpdateDialogState.Downloading;
                break;
            case UpdatePreparationStage.Verifying:
                State = UpdateDialogState.Verifying;
                break;
            case UpdatePreparationStage.Extracting:
                State = UpdateDialogState.Extracting;
                break;
        }
    }

    private void RequestCancel()
    {
        if (_cancellation is null || IsCancelRequested)
        {
            return;
        }

        IsCancelRequested = true;
        _cancellation.Cancel();
    }

    private void Fail(string detail)
    {
        FailureDetail = detail;
        State = UpdateDialogState.Failed;
    }

    private void RequestClose(UpdateDialogChoice choice) => CloseRequested?.Invoke(this, choice);

    private static string FormatMegabytes(long bytes) =>
        (bytes / BytesPerMegabyte).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
