using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.Tests.Updates;
internal sealed class FakeUpdateFileOperations : IUpdateFileOperations
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> LockedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailMoveFrom { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailCopyTo { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailDelete { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool FailWrite { get; set; }

    public List<string> Operations { get; } = [];
    public Action<string>? OnExclusiveCheck { get; set; }

    public void AddFile(string path, string contents = "")
    {
        Files[path] = contents;
        AddDirectory(Path.GetDirectoryName(path)!);
    }

    public void AddDirectory(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            Directories.Add(current);
            current = Path.GetDirectoryName(current);
        }
    }

    public bool FileExists(string path) => Files.ContainsKey(path);

    public bool DirectoryExists(string path) => Directories.Contains(path);

    public bool PathExists(string path) => FileExists(path) || DirectoryExists(path);

    public bool IsReparsePoint(string path) => ReparsePoints.Contains(path);

    public DirectoryListing? ListDirectory(string directory)
    {
        if (!DirectoryExists(directory))
        {
            return null;
        }

        var files = Files.Keys
            .Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path))
            .ToList();
        var directories = Directories
            .Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path))
            .ToList();
        return new DirectoryListing(files, directories);
    }

    public bool MoveFile(string sourcePath, string destinationPath)
    {
        Operations.Add($"move {sourcePath} -> {destinationPath}");
        if (FailMoveFrom.Contains(sourcePath) || !Files.ContainsKey(sourcePath) || PathExists(destinationPath))
        {
            return false;
        }

        Files[destinationPath] = Files[sourcePath];
        Files.Remove(sourcePath);
        return true;
    }

    public bool CopyFile(string sourcePath, string destinationPath)
    {
        Operations.Add($"copy {sourcePath} -> {destinationPath}");
        BeforeCopy?.Invoke(destinationPath);
        if (FailCopyTo.Contains(destinationPath) || !Files.ContainsKey(sourcePath) || PathExists(destinationPath))
        {
            return false;
        }

        Files[destinationPath] = Files[sourcePath];
        return true;
    }

    public bool DeleteFile(string path)
    {
        Operations.Add($"delete {path}");
        if (FailDelete.Contains(path))
        {
            return false;
        }

        Files.Remove(path);
        return true;
    }

    public bool CanOpenExclusively(string path)
    {
        OnExclusiveCheck?.Invoke(path);
        return !Files.ContainsKey(path) || !LockedFiles.Contains(path);
    }

    public string? ReadAllText(string path) => Files.TryGetValue(path, out var contents) ? contents : null;

    public bool WriteNewFileAtomically(string path, string contents)
    {
        Operations.Add($"write {path}");
        if (FailWrite || PathExists(path))
        {
            return false;
        }

        Files[path] = contents;
        return true;
    }

    public bool ReplaceFileAtomically(string path, string contents)
    {
        Operations.Add($"replace {path}");
        if (FailWrite)
        {
            return false;
        }

        Files[path] = contents;
        return true;
    }
    public Action<string>? BeforeCopy { get; set; }

    public bool DeleteDirectoryTree(string path)
    {
        Operations.Add($"rmdir {path}");
        if (FailDelete.Contains(path))
        {
            return false;
        }

        var prefix = path + Path.DirectorySeparatorChar;
        foreach (var file in Files.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Files.Remove(file);
        }

        Directories.RemoveWhere(dir => string.Equals(dir, path, StringComparison.OrdinalIgnoreCase)
            || dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return true;
    }
}
internal sealed class FakeUpdateProcessOperations : IUpdateProcessOperations
{
    public bool LockAvailable { get; set; } = true;

    public bool OldProcessesExit { get; set; } = true;

    public UpdateLaunchResult LaunchResult { get; set; } = new(UpdateLaunchOutcome.Signaled);

    public bool LaunchPreviousSucceeds { get; set; } = true;

    public List<string> Calls { get; } = [];

    public IReadOnlyList<string>? LastLaunchArguments { get; private set; }

    public string? LastStartupEventName { get; private set; }

    public TimeSpan? LastLockTimeout { get; private set; }

    public bool IsLockHeld { get; private set; }

    public TimeSpan Elapsed { get; private set; }

    public Action? OnLaunch { get; set; }

    public void Sleep(TimeSpan duration) => Elapsed += duration;

    public IDisposable? TryAcquireApplierLock(TimeSpan timeout)
    {
        LastLockTimeout = timeout;
        Calls.Add("lock");
        if (!LockAvailable)
        {
            return null;
        }

        IsLockHeld = true;
        return new Releaser(() =>
        {
            IsLockHeld = false;
            Calls.Add("unlock");
        });
    }

    public bool WaitForInstanceProcessesToExit(string installExecutablePath, TimeSpan timeout)
    {
        Calls.Add($"wait-old {installExecutablePath}");
        return OldProcessesExit;
    }

    public UpdateLaunchResult LaunchAndWaitForStartup(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string startupEventName,
        TimeSpan survivalTimeout)
    {
        Calls.Add($"launch-new {executablePath} cwd={workingDirectory}");
        LastLaunchArguments = arguments;
        LastStartupEventName = startupEventName;
        OnLaunch?.Invoke();
        return LaunchResult;
    }

    public bool LaunchDetached(string executablePath, string workingDirectory)
    {
        Calls.Add($"launch-previous {executablePath}");
        return LaunchPreviousSucceeds;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                release();
            }
        }
    }
}

internal sealed class RecordingApplyReporter : IUpdateApplyReporter
{
    public List<string> Logs { get; } = [];

    public List<string> Errors { get; } = [];

    public void Log(string message) => Logs.Add(message);

    public void ShowError(string message) => Errors.Add(message);
}
