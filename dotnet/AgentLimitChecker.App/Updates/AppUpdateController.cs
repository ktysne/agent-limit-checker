using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AgentLimitChecker.App.ViewModels;
using AgentLimitChecker.App.Views;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.App.Updates;

internal sealed class AppUpdateController(string version, Action shutdown, Action<string> log)
{
    private readonly UpdateManifestSource source = UpdateUrlPolicy.ResolveManifestSource(
        Environment.GetEnvironmentVariable(UpdateCheckDefaults.ManifestUrlEnvironmentVariable));
    private Task cleanup = Task.CompletedTask;
    private bool prompting;

    public static string UpdateRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-limit-checker", "update");

    public static bool IsSingleFilePublish =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(App).Assembly.GetName().Name + ".dll"));

    public void StartBackgroundCleanup()
    {
        // Mutex は取得したスレッドで解放する必要があるため、専用スレッドで同期処理を実行する。
        cleanup = Task.Factory.StartNew(() =>
        {
            try
            {
                new UpdateCleanupService(new FileSystemUpdateFileOperations(), new WindowsUpdateProcessOperations(), log)
                    .Run(Path.GetDirectoryName(Environment.ProcessPath), version, UpdateRootDirectory);
            }
            catch (Exception ex) { log($"[update] cleanup failed: {ex.Message}"); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        var fetcher = new HttpUpdateManifestFetcher(version, source.ManifestUri);
        var result = await new UpdateChecker(fetcher.FetchAsync, version, source.IsDevelopmentOverride)
            .CheckAsync(null, token).ConfigureAwait(false);
        log($"[update] {result.Kind} current={version} latest={result.Update?.VersionText} error={result.Error}");
        return result;
    }

    public void ShowPrompt(UpdateInfo update)
    {
        if (prompting) return;
        prompting = true;
        try
        {
            var decision = UpdateAutoApplyEligibility.Evaluate(update, new UpdateAutoApplyEnvironment(
                Environment.ProcessPath, IsSingleFilePublish, Path.GetTempPath(), UpdateRootDirectory,
                source.IsDevelopmentOverride, CanWriteDirectory));
            var model = new UpdateAvailableViewModel(version, update.VersionText, update.DownloadUrl,
                decision.UnavailableReason,
                decision.CanAutoApply ? (progress, token) => PrepareAsync(update, progress, token) : null,
                decision.InstallDirectory is { } install ? prepared => StartApplier(prepared, install) : null);
            var dialog = new UpdateAvailableWindow(model);
            dialog.ShowDialog();
            if (dialog.Choice == UpdateDialogChoice.Applied) shutdown();
            else if (dialog.Choice == UpdateDialogChoice.Download) OpenBrowser(new Uri(UpdateCheckDefaults.DistributionPage));
        }
        finally { prompting = false; }
    }

    private async Task<PreparedUpdate> PrepareAsync(UpdateInfo update, IProgress<UpdatePreparationProgress> progress,
        CancellationToken token)
    {
        progress.Report(new(UpdatePreparationStage.Preparing));
        await cleanup.WaitAsync(token);
        // UI スレッドで取得と解放を行い、適用役の起動前に所有権を手放す。
        using var applierLock = ApplierLock.TryAcquire(TimeSpan.Zero)
            ?? throw new UpdatePreparationException("別の更新処理が実行中です。しばらく待ってから再試行してください。");
        return await new UpdatePreparationService(new HttpUpdatePackageDownloader(version),
            new FileSystemUpdateFileOperations(), UpdateRootDirectory, log).PrepareAsync(update, progress, token);
    }

    private bool StartApplier(PreparedUpdate prepared, string installDirectory)
    {
        try
        {
            using var process = Process.Start(WindowsUpdateProcessOperations.CreateStartInfo(
                Path.Combine(prepared.StagingDirectory, UpdateLayout.ExecutableFileName),
                UpdateCommandLine.BuildApplyArguments(Environment.ProcessId, prepared.StagingDirectory, installDirectory),
                prepared.UpdateDirectory));
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log($"[update] applier launch failed: {ex.Message}");
            return false;
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(directory, $".alc-update-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    private static void OpenBrowser(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"ブラウザを開けませんでした。次の URL を開いてください。\n\n{url}",
                "Agent Limit Checker", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
