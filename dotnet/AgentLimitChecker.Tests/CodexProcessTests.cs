using System.Diagnostics;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Codex;

namespace AgentLimitChecker.Tests;

public sealed class CodexProcessTests
{
    [Theory]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    public async Task NativeProcess_CommunicatesThroughShellAndKillsDescendantsOnStop(string extension)
    {
        using var directory = new ProviderTestDirectory();
        var server = directory.FileAt("server.ps1");
        File.WriteAllText(server, """
            [Console]::Error.WriteLine('ignored diagnostics')
            [Console]::WriteLine('non-json startup line')
            while ($null -ne ($line = [Console]::ReadLine())) {
                $request = $line | ConvertFrom-Json
                if ($null -eq $request.id) { continue }
                if ($request.method -eq 'initialize') { $result = @{} }
                else { $result = @{ rateLimits = @{ primary = @{ windowDurationMins = 300; usedPercent = 25; resetsAt = $PID } } } }
                $response = @{ id = $request.id; result = $result } | ConvertTo-Json -Depth 8 -Compress
                [Console]::Write($response.Substring(0, 2))
                [Console]::WriteLine($response.Substring(2))
            }
            """);
        var shim = directory.FileAt("shim" + extension);
        File.WriteAllText(shim, extension == ".cmd" ? $"@echo off\r\npowershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{server}\"\r\n"
            : $"& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File '{server.Replace("'", "''", StringComparison.Ordinal)}'\n");
        using var provider = new CodexProvider(home => new(home, executable: () => shim));
        try
        {
            var usage = await provider.FetchAsync(directory.Root);
            Assert.NotNull(usage.FiveHour); Assert.Equal(0.25, usage.FiveHour.Utilization);
            using var descendant = Process.GetProcessById((int)(usage.FiveHour.ResetsAt!.Value / 1000));
            Assert.False(descendant.HasExited);
            provider.Shutdown();
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(descendant.HasExited);
        }
        finally { provider.Shutdown(); }
    }
}
