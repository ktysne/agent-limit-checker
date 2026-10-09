using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.Tests;

public sealed class AutoLaunchTests
{
    [Theory]
    [InlineData("C:\\AgentLimitChecker.exe", "\"C:\\AgentLimitChecker.exe\" --hidden")]
    [InlineData("C:\\Program Files\\Agent Limit Checker\\AgentLimitChecker.exe", "\"C:\\Program Files\\Agent Limit Checker\\AgentLimitChecker.exe\" --hidden")]
    public void BuildValue_QuotesExecutableAndAddsHiddenArgument(string executablePath, string expected)
    {
        Assert.Equal(expected, AutoLaunchPolicy.BuildValue(executablePath));
    }

    [Fact]
    public void IsEnabled_ReturnsFalseWhenRunValueIsMissing()
    {
        Assert.False(AutoLaunchPolicy.IsEnabled(null, null));
    }

    [Fact]
    public void IsEnabled_ReturnsTrueWhenRunValueExistsWithoutStartupApprovalValue()
    {
        Assert.True(AutoLaunchPolicy.IsEnabled("\"C:\\old.exe\" --hidden", null));
    }

    [Fact]
    public void IsEnabled_ReturnsFalseWhenStartupApprovalMarksRunValueDisabled()
    {
        Assert.False(AutoLaunchPolicy.IsEnabled("\"C:\\old.exe\" --hidden", [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void IsEnabled_ReturnsTrueWhenRunValuePointsToAnotherExecutable()
    {
        Assert.True(AutoLaunchPolicy.IsEnabled("\"C:\\old.exe\" --hidden", [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void SetEnabled_WritesRunValueAndDeletesItWhenDisabled()
    {
        var registry = new FakeAutoLaunchRegistry();
        var service = new RegistryAutoLaunchService(registry, "C:\\Program Files\\Agent Limit Checker\\AgentLimitChecker.exe");

        service.SetEnabled(true);

        Assert.Equal("\"C:\\Program Files\\Agent Limit Checker\\AgentLimitChecker.exe\" --hidden",
            registry.GetRunValue(AutoLaunchPolicy.DefaultValueName));
        Assert.True(service.IsEnabled);

        service.SetEnabled(false);

        Assert.Null(registry.GetRunValue(AutoLaunchPolicy.DefaultValueName));
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void Startup_RegistersCurrentExecutableWhenSettingIsEnabled()
    {
        const string valueName = "com.agent-limit-checker.probe";
        var registry = new FakeAutoLaunchRegistry();
        registry.SetRunValue(valueName, "\"C:\\old\\AgentLimitChecker.exe\" --hidden");
        var service = new RegistryAutoLaunchService(registry, "C:\\current\\AgentLimitChecker.exe", valueName);

        AutoLaunchStartup.Initialize(autoLaunchEnabled: true, debugBuild: false, service, _ => { });

        Assert.Equal("\"C:\\current\\AgentLimitChecker.exe\" --hidden", registry.GetRunValue(valueName));
    }

    [Fact]
    public void Startup_DoesNotChangeRegistryWhenSettingIsDisabled()
    {
        const string valueName = "com.agent-limit-checker.probe";
        var registry = new FakeAutoLaunchRegistry();
        const string existingValue = "\"C:\\old\\AgentLimitChecker.exe\" --hidden";
        registry.SetRunValue(valueName, existingValue);
        registry.ResetCounts();
        var service = new RegistryAutoLaunchService(registry, "C:\\current\\AgentLimitChecker.exe", valueName);

        AutoLaunchStartup.Initialize(autoLaunchEnabled: false, debugBuild: false, service, _ => { });

        Assert.Equal(existingValue, registry.GetRunValue(valueName));
        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public void Startup_DebugBuildLogsAndDoesNotReregister()
    {
        var registry = new FakeAutoLaunchRegistry();
        var service = new RegistryAutoLaunchService(registry, "C:\\current\\AgentLimitChecker.exe");
        var messages = new List<string>();

        AutoLaunchStartup.Initialize(autoLaunchEnabled: true, debugBuild: true, service, messages.Add);

        Assert.Equal(new[] { "debug build: skip auto-launch re-registration" }, messages);
        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public void RegistryFailures_AreLoggedAndReportedAsDisabledWithoutThrowing()
    {
        var errors = new List<string>();
        var service = new RegistryAutoLaunchService(new FailingAutoLaunchRegistry(), @"C:\Apps\AgentLimitChecker.exe",
            logError: errors.Add);

        Assert.False(service.IsEnabled);
        service.SetEnabled(true);
        service.SetEnabled(false);

        Assert.Equal(new[] { "[autoLaunch] isEnabled failed", "[autoLaunch] setEnabled failed", "[autoLaunch] setEnabled failed" }, errors);
    }

    private sealed class FailingAutoLaunchRegistry : IAutoLaunchRegistry
    {
        public string? GetRunValue(string valueName) => throw new UnauthorizedAccessException();
        public byte[]? GetStartupApprovedValue(string valueName) => throw new UnauthorizedAccessException();
        public void SetRunValue(string valueName, string value) => throw new UnauthorizedAccessException();
        public void DeleteRunValue(string valueName) => throw new UnauthorizedAccessException();
    }

    private sealed class FakeAutoLaunchRegistry : IAutoLaunchRegistry
    {
        private readonly Dictionary<string, string> runValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> startupApprovedValues = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }

        public string? GetRunValue(string valueName) => runValues.GetValueOrDefault(valueName);
        public byte[]? GetStartupApprovedValue(string valueName) => startupApprovedValues.GetValueOrDefault(valueName);

        public void SetRunValue(string valueName, string value)
        {
            WriteCount++;
            runValues[valueName] = value;
        }

        public void DeleteRunValue(string valueName)
        {
            DeleteCount++;
            runValues.Remove(valueName);
        }

        public void ResetCounts()
        {
            WriteCount = 0;
            DeleteCount = 0;
        }
    }
}
