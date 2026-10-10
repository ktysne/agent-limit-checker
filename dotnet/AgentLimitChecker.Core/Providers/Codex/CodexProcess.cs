using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentLimitChecker.Core.Providers.Codex;

internal sealed record CodexStartInfo(string FileName, string Arguments, IReadOnlyDictionary<string, string> Environment);

internal interface ICodexProcess : IDisposable
{
    bool HasExited { get; }
    event Action<string>? Output;
    event Action<int?>? Exited;
    event Action? Failed;
    void StartReading();
    void WriteLine(string line);
}

internal sealed class CodexProcess : ICodexProcess
{
    private readonly Process process;
    private readonly SafeFileHandle job;
    private readonly AnonymousPipeServerStream input;
    private readonly AnonymousPipeServerStream output;
    private readonly AnonymousPipeServerStream error;
    private readonly StreamWriter writer;
    private readonly StreamReader reader;
    private readonly StreamReader errorReader;
    private int disposed;
    public bool HasExited => Volatile.Read(ref disposed) != 0 || process.HasExited;
    public event Action<string>? Output;
    public event Action<int?>? Exited;
    public event Action? Failed;

    private CodexProcess(Process process, SafeFileHandle job, AnonymousPipeServerStream input,
        AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        this.process = process;
        _ = process.SafeHandle;
        this.job = job;
        this.input = input;
        this.output = output;
        this.error = error;
        writer = new(input, new UTF8Encoding(false)) { AutoFlush = true };
        reader = new(output, Encoding.UTF8);
        errorReader = new(error, Encoding.UTF8);
    }

    internal static CodexProcess Start(CodexStartInfo info)
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        AnonymousPipeServerStream? input = null, output = null, error = null;
        Native.ProcessInformation child = default;
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new Native.ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = Native.JobObjectLimitKillOnJobClose;
            if (!Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimitInformation>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            var startup = new Native.StartupInfoEx
            {
                StartupInfo = new Native.StartupInfo
                {
                    Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = Native.StartfUseStdHandles,
                    StdInput = input.ClientSafePipeHandle.DangerousGetHandle(),
                    StdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
                    StdError = error.ClientSafePipeHandle.DangerousGetHandle()
                }
            };
            nuint attributeSize = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            startup.Attributes = Marshal.AllocHGlobal((nint)attributeSize);
            var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            var attributesInitialized = false;
            var environment = Marshal.StringToHGlobalUni(string.Join('\0', info.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}")) + "\0\0");
            try
            {
                if (!Native.InitializeProcThreadAttributeList(startup.Attributes, 1, 0, ref attributeSize)) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributesInitialized = true;
                Marshal.Copy(new[] { startup.StartupInfo.StdInput, startup.StartupInfo.StdOutput, startup.StartupInfo.StdError }, 0, handles, 3);
                // 同時起動する別アカウントのパイプを継承すると、停止時に EOF が届かなくなる。
                if (!Native.UpdateProcThreadAttribute(startup.Attributes, 0, Native.ProcThreadAttributeHandleList, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                // 孫プロセスの生成前にジョブへ登録するため、一時停止状態で起動する。
                if (!Native.CreateProcess(null, new StringBuilder($"\"{info.FileName}\" {info.Arguments}"), IntPtr.Zero, IntPtr.Zero,
                    true, Native.CreateNoWindow | Native.CreateSuspended | Native.CreateUnicodeEnvironment | Native.ExtendedStartupInfoPresent,
                    environment, null, ref startup, out child)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(environment); Marshal.FreeHGlobal(handles);
                if (attributesInitialized) Native.DeleteProcThreadAttributeList(startup.Attributes);
                Marshal.FreeHGlobal(startup.Attributes);
            }
            if (!Native.AssignProcessToJobObject(job, child.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            var result = new CodexProcess(Process.GetProcessById((int)child.ProcessId), job, input, output, error);
            if (Native.ResumeThread(child.Thread) == uint.MaxValue) { result.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return result;
        }
        catch
        {
            if (child.Process != IntPtr.Zero) Native.TerminateProcess(child.Process, 1);
            input?.Dispose(); output?.Dispose(); error?.Dispose(); job.Dispose();
            throw;
        }
        finally
        {
            if (child.Thread != IntPtr.Zero) Native.CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) Native.CloseHandle(child.Process);
        }
    }

    public void StartReading()
    {
        _ = PumpOutputAsync();
        _ = DrainErrorAsync();
        _ = WatchExitAsync();
    }
    private async Task PumpOutputAsync()
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) Output?.Invoke(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { if (Volatile.Read(ref disposed) == 0) Failed?.Invoke(); }
    }
    private async Task DrainErrorAsync()
    {
        var buffer = new char[4096];
        try { while (await errorReader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private async Task WatchExitAsync()
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); Exited?.Invoke(process.ExitCode); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }
    public void WriteLine(string line) => writer.WriteLine(line);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        job.Dispose();
        writer.Dispose(); reader.Dispose(); errorReader.Dispose();
        input.Dispose(); output.Dispose(); error.Dispose(); process.Dispose();
    }

    private static class Native
    {
        internal const uint JobObjectLimitKillOnJobClose = 0x2000;
        internal const int JobObjectExtendedLimitInformation = 9;
        internal const uint StartfUseStdHandles = 0x100;
        internal const nuint ProcThreadAttributeHandleList = 0x20002;
        internal const uint CreateNoWindow = 0x08000000;
        internal const uint CreateSuspended = 0x00000004;
        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimitInformation
        {
            internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimitInformation
        {
            internal BasicLimitInformation BasicLimitInformation;
            internal IoCounters IoInfo;
            internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
        {
            internal int Size;
            internal string? Reserved, Desktop, Title;
            internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            internal ushort ShowWindow, ReservedSize;
            internal IntPtr ReservedData, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr Attributes; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
