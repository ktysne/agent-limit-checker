using System.IO;
using AgentLimitChecker.Core.Shell;
using Microsoft.Win32;

namespace AgentLimitChecker.App;

internal sealed class AutoLaunchRegistry : IAutoLaunchRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? GetRunValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(valueName) as string;
    }

    public void SetRunValue(string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("Run キーを開けません。");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteRunValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public byte[]? GetStartupApprovedValue(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
    }
}
