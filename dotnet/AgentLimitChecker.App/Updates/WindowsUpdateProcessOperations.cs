using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AgentLimitChecker.Core.Updates;
using Microsoft.Win32.SafeHandles;

namespace AgentLimitChecker.App.Updates;
internal sealed class WindowsUpdateProcessOperations : IUpdateProcessOperations
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
    public IDisposable? TryAcquireApplierLock(TimeSpan timeout) => ApplierLock.TryAcquire(timeout);

    public bool WaitForInstanceProcessesToExit(string installExecutablePath, TimeSpan timeout)
    {
        var deadline = _stopwatch.Elapsed + timeout;
        var expected = UpdateLayout.NormalizeFullPath(installExecutablePath);
        var processName = Path.GetFileNameWithoutExtension(installExecutablePath);
        var selfId = Environment.ProcessId;

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == selfId || !IsInstanceOf(process, expected))
                {
                    continue;
                }

                var remaining = deadline - _stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero || !WaitForExit(process, remaining))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public UpdateLaunchResult LaunchAndWaitForStartup(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string startupEventName,
        TimeSpan survivalTimeout)
    {
        // 起動前にイベントを用意し、新しい版が先に送った合図を取りこぼさないようにする。
        using var startupEvent = new EventWaitHandle(false, EventResetMode.ManualReset, startupEventName);

        Process? process;
        try
        {
            process = Process.Start(CreateStartInfo(executablePath, arguments, workingDirectory));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new UpdateLaunchResult(UpdateLaunchOutcome.LaunchFailed, ex.Message);
        }

        if (process is null)
        {
            return new UpdateLaunchResult(UpdateLaunchOutcome.LaunchFailed, "プロセスを開始できませんでした。");
        }

        using (process)
        using (var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false) })
        {
            var signaled = WaitHandle.WaitAny([startupEvent, exited], survivalTimeout);
            if (signaled == 0 || startupEvent.WaitOne(0))
            {
                return new UpdateLaunchResult(UpdateLaunchOutcome.Signaled);
            }

            if (signaled == WaitHandle.WaitTimeout)
            {
                return new UpdateLaunchResult(UpdateLaunchOutcome.Survived);
            }

            return new UpdateLaunchResult(UpdateLaunchOutcome.ExitedWithoutSignal, $"exit code={SafeExitCode(process)}");
        }
    }

    public bool LaunchDetached(string executablePath, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(executablePath, [], workingDirectory));
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
    private static bool IsInstanceOf(Process process, string? expectedExecutablePath)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            var actual = UpdateLayout.NormalizeFullPath(process.MainModule?.FileName);
            return actual is null || expectedExecutablePath is null || UpdateLayout.PathEquals(actual, expectedExecutablePath);
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool WaitForExit(Process process, TimeSpan timeout)
    {
        try
        {
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return true;
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return "unknown";
        }
    }
}
internal sealed class ApplierLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    private ApplierLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static ApplierLock? TryAcquire(TimeSpan timeout)
    {
        var mutex = new Mutex(false, UpdateLayout.ApplierMutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // 放棄された Mutex の所有権は、このスレッドへ移っている。
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new ApplierLock(mutex);
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
