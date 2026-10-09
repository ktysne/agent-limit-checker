using System.IO.Compression;

namespace AgentLimitChecker.Core.Updates;

public enum UpdatePreparationStage
{
    Preparing,
    Downloading,
    Verifying,
    Extracting,
    Ready,
}
public sealed record UpdateDownloadProgress(long ReceivedBytes, long? TotalBytes);

public sealed record UpdatePreparationProgress(UpdatePreparationStage Stage, UpdateDownloadProgress? Download = null);
public sealed record PreparedUpdate(string UpdateId, string UpdateDirectory, string StagingDirectory, bool Reused);
public sealed class UpdatePreparationException : Exception
{
    public UpdatePreparationException(string message)
        : base(message)
    {
    }

    public UpdatePreparationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IUpdatePackageDownloader
{
    Task DownloadAsync(
        Uri url,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
public sealed class UpdatePreparationService
{
    private const int CopyBufferSize = 81920;

    private readonly IUpdatePackageDownloader _downloader;
    private readonly IUpdateFileOperations _files;
    private readonly string _updateRootDirectory;
    private readonly Action<string> _log;

    public UpdatePreparationService(
        IUpdatePackageDownloader downloader,
        IUpdateFileOperations files,
        string updateRootDirectory,
        Action<string> log)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _updateRootDirectory = updateRootDirectory;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<PreparedUpdate> PrepareAsync(
        UpdateInfo update,
        IProgress<UpdatePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Sha256 is null)
        {
            throw new UpdatePreparationException("配布サイトの情報に照合用の値がありません。");
        }

        progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Preparing));
        Directory.CreateDirectory(_updateRootDirectory);

        if (await FindReusableAsync(update, cancellationToken).ConfigureAwait(false) is { } reused)
        {
            _log($"準備済みの更新を再利用します: {reused.UpdateDirectory}");
            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Ready));
            return reused;
        }

        var updateId = UpdateLayout.NewUpdateId();
        var updateDirectory = Path.Combine(_updateRootDirectory, updateId);
        var stagingDirectory = Path.Combine(updateDirectory, UpdateLayout.StagingDirectoryName);
        Directory.CreateDirectory(updateDirectory);
        try
        {
            var packagePath = Path.Combine(updateDirectory, UpdateLayout.PackageFileName(update.VersionText));
            var partialPath = packagePath + UpdateLayout.PartialSuffix;

            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Downloading, new UpdateDownloadProgress(0, null)));
            var downloadProgress = progress is null
                ? null
                : new SynchronousProgress<UpdateDownloadProgress>(value =>
                    progress.Report(new UpdatePreparationProgress(UpdatePreparationStage.Downloading, value)));
            await _downloader.DownloadAsync(update.DownloadUrl, partialPath, downloadProgress, cancellationToken).ConfigureAwait(false);
            File.Move(partialPath, packagePath, overwrite: false);

            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Verifying));
            var actual = await UpdateSha256.ComputeFileHexAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (!UpdateSha256.Matches(actual, update.Sha256))
            {
                throw new UpdatePreparationException("ダウンロードしたファイルが配布サイトの情報と一致しません。");
            }

            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Extracting));
            await ExtractAsync(packagePath, stagingDirectory, cancellationToken).ConfigureAwait(false);

            var prepared = UpdateRecordSerializer.Serialize(new UpdatePreparedRecord(update.VersionText, actual));
            if (!_files.WriteNewFileAtomically(Path.Combine(updateDirectory, UpdateLayout.PreparedRecordFileName), prepared))
            {
                throw new UpdatePreparationException("準備の記録を書き込めませんでした。");
            }

            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Ready));
            _log($"更新の準備ができました: 版={update.VersionText}、フォルダー={updateDirectory}");
            return new PreparedUpdate(updateId, updateDirectory, stagingDirectory, Reused: false);
        }
        catch (Exception ex)
        {
            if (!_files.DeleteDirectoryTree(updateDirectory))
            {
                _log($"準備に失敗したフォルダーを削除できませんでした: {updateDirectory}");
            }

            if (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new UpdatePreparationException($"更新ファイルを保存できませんでした: {ex.Message}", ex);
            }

            throw;
        }
    }
    private async Task<PreparedUpdate?> FindReusableAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        var listing = _files.ListDirectory(_updateRootDirectory);
        if (listing is null)
        {
            return null;
        }

        foreach (var name in listing.Directories.Where(UpdateLayout.IsUpdateId))
        {
            var updateDirectory = Path.Combine(_updateRootDirectory, name);
            if (_files.IsReparsePoint(updateDirectory))
            {
                continue;
            }

            var record = UpdateRecordSerializer.ParsePrepared(
                _files.ReadAllText(Path.Combine(updateDirectory, UpdateLayout.PreparedRecordFileName)));
            if (record is null
                || !string.Equals(record.Version, update.VersionText, StringComparison.Ordinal)
                || !UpdateSha256.Matches(record.Sha256, update.Sha256))
            {
                continue;
            }

            var stagingDirectory = Path.Combine(updateDirectory, UpdateLayout.StagingDirectoryName);
            var packagePath = Path.Combine(updateDirectory, UpdateLayout.PackageFileName(update.VersionText));
            if (!_files.FileExists(packagePath)
                || !_files.FileExists(Path.Combine(stagingDirectory, UpdateLayout.ExecutableFileName)))
            {
                continue;
            }

            string actual;
            try
            {
                actual = await UpdateSha256.ComputeFileHexAsync(packagePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (UpdateSha256.Matches(actual, update.Sha256))
            {
                return new PreparedUpdate(name, updateDirectory, stagingDirectory, Reused: true);
            }
        }

        return null;
    }

    private static async Task ExtractAsync(string packagePath, string stagingDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var entries = archive.Entries.Select(entry => new UpdatePackageEntry(entry.FullName, entry.Length)).ToList();
        var validation = UpdatePackageValidator.Validate(entries);
        if (!validation.IsValid)
        {
            throw new UpdatePreparationException(validation.Error!);
        }

        Directory.CreateDirectory(stagingDirectory);
        var buffer = new byte[CopyBufferSize];
        foreach (var entry in archive.Entries)
        {
            var destination = Path.Combine(stagingDirectory, entry.FullName);
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > entry.Length)
                {
                    throw new UpdatePreparationException($"zip の中身が宣言された長さと一致しません: {entry.FullName}");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (written != entry.Length)
            {
                throw new UpdatePreparationException($"zip の中身が宣言された長さと一致しません: {entry.FullName}");
            }
        }
    }
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
