using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLimitChecker.Core.Settings;

public sealed class AppSettings
{
    [JsonPropertyName("pollingIntervalSec")]
    public int PollingIntervalSec { get; init; } = 300;

    [JsonPropertyName("autoLaunch")]
    public bool AutoLaunch { get; init; }

    [JsonPropertyName("ntfy")]
    public NtfySettings Ntfy { get; init; } = new();

    [JsonPropertyName("codexAccountNames")]
    public Dictionary<string, string> CodexAccountNames { get; init; } = new(StringComparer.Ordinal);

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; init; } = new(StringComparer.Ordinal);

    public static AppSettings Defaults => new();
}

public sealed class NtfySettings
{
    [JsonPropertyName("topicUrl")]
    public string TopicUrl { get; init; } = "";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("notifyFiveHour")]
    public bool NotifyFiveHour { get; init; }

    [JsonPropertyName("notifyWeekly")]
    public bool NotifyWeekly { get; init; }

    [JsonPropertyName("notifyResetCreditsExpiry")]
    public bool NotifyResetCreditsExpiry { get; init; }
}
