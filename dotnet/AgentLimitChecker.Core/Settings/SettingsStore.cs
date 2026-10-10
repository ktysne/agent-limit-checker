using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentLimitChecker.Core.Settings;

public sealed class SettingsStore
{
    private static readonly int[] AllowedIntervals = [30, 60, 120, 300, 600];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false,
    };

    private readonly string settingsPath;
    private AppSettings? cache;

    private readonly Action<string> logError;

    public SettingsStore(Action<string>? logError = null) : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "agent-limit-checker",
        "settings.json"), logError)
    {
    }

    public SettingsStore(string settingsPath, Action<string>? logError = null)
    {
        this.settingsPath = Path.GetFullPath(settingsPath);
        this.logError = logError ?? (_ => { });
    }

    public AppSettings Load()
    {
        if (cache is not null) return cache;
        try
        {
            var parsed = JsonNode.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
            cache = Normalize(parsed);
        }
        catch
        {
            cache = AppSettings.Defaults;
        }
        return cache;
    }

    public AppSettings Save(JsonObject? partial)
    {
        var current = JsonSerializer.SerializeToNode(Load(), SerializerOptions) as JsonObject ?? new JsonObject();
        var merged = (JsonObject)current.DeepClone();
        if (partial is not null)
        {
            foreach (var property in partial)
            {
                if (property.Key is "ntfy" or "codexAccountNames") continue;
                merged[property.Key] = property.Value?.DeepClone();
            }

            if (partial.ContainsKey("ntfy"))
            {
                merged["ntfy"] = MergeObject(current["ntfy"] as JsonObject, partial["ntfy"] as JsonObject);
            }

            if (partial.ContainsKey("codexAccountNames"))
            {
                merged["codexAccountNames"] = MergeObject(
                    current["codexAccountNames"] as JsonObject,
                    partial["codexAccountNames"] as JsonObject);
            }
        }

        // 書き込みに失敗しても稼働中は新しい設定で動かす(Electron 版と同じ)。失敗はログにだけ残す。
        cache = Normalize(merged);
        try
        {
            WriteAtomically(JsonSerializer.Serialize(cache, SerializerOptions));
        }
        catch (Exception)
        {
            logError("[settings] failed to write");
        }
        return cache;
    }

    internal static AppSettings Normalize(JsonNode? value)
    {
        var raw = value as JsonObject;
        var interval = ReadInteger(raw?["pollingIntervalSec"]);
        var ntfy = raw?["ntfy"] as JsonObject;
        var names = NormalizeAccountNames(raw?["codexAccountNames"]);
        var additionalProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (raw is not null)
        {
            foreach (var property in raw)
            {
                if (property.Key is "pollingIntervalSec" or "autoLaunch" or "ntfy" or "codexAccountNames" or "checkForUpdatesOnStartup") continue;
                if (property.Value is not null)
                {
                    additionalProperties[property.Key] = JsonSerializer.Deserialize<JsonElement>(property.Value.ToJsonString());
                }
                else
                {
                    additionalProperties[property.Key] = JsonSerializer.SerializeToElement<object?>(null);
                }
            }
        }

        return new AppSettings
        {
            PollingIntervalSec = interval is { } allowed && AllowedIntervals.Contains(allowed) ? allowed : 300,
            AutoLaunch = IsTruthy(raw?["autoLaunch"]),
            CheckForUpdatesOnStartup = raw?["checkForUpdatesOnStartup"] is null || IsTruthy(raw["checkForUpdatesOnStartup"]),
            Ntfy = new NtfySettings
            {
                TopicUrl = ReadTrimmedString(ntfy?["topicUrl"]),
                AccessToken = ReadTrimmedString(ntfy?["accessToken"]),
                NotifyFiveHour = IsTruthy(ntfy?["notifyFiveHour"]),
                NotifyWeekly = IsTruthy(ntfy?["notifyWeekly"]),
                NotifyResetCreditsExpiry = IsTruthy(ntfy?["notifyResetCreditsExpiry"]),
            },
            CodexAccountNames = names,
            AdditionalProperties = additionalProperties,
        };
    }

    private void WriteAtomically(string contents)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
            File.Move(temporaryPath, settingsPath, true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static JsonObject MergeObject(JsonObject? current, JsonObject? incoming)
    {
        var merged = current is null ? new JsonObject() : (JsonObject)current.DeepClone();
        if (incoming is not null)
        {
            foreach (var property in incoming)
            {
                merged[property.Key] = property.Value?.DeepClone();
            }
        }
        return merged;
    }

    private static Dictionary<string, string> NormalizeAccountNames(JsonNode? value)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value is not JsonObject raw) return names;
        foreach (var property in raw)
        {
            if (string.IsNullOrWhiteSpace(property.Key) || property.Value is not JsonValue jsonValue ||
                !jsonValue.TryGetValue<string>(out var name)) continue;
            var label = property.Key.Trim();
            var displayName = name.Trim();
            if (label.Length == 0 || displayName.Length == 0) continue;
            if (displayName.Length > 40) displayName = displayName[..40];
            names[label] = displayName;
        }
        return names;
    }

    private static int? ReadInteger(JsonNode? value)
    {
        if (value is null) return null;
        var element = ToJsonElement(value);
        if (element.ValueKind != JsonValueKind.Number) return null;
        if (!element.TryGetDouble(out var number) || !double.IsFinite(number) || number != Math.Truncate(number) ||
            number < int.MinValue || number > int.MaxValue) return null;
        return (int)number;
    }

    private static string ReadTrimmedString(JsonNode? value)
    {
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text.Trim() : "";
    }

    private static bool IsTruthy(JsonNode? value)
    {
        if (value is null) return false;
        if (value is JsonObject or JsonArray) return true;
        if (value is not JsonValue) return false;
        var element = ToJsonElement(value);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => element.GetString()!.Length > 0,
            JsonValueKind.Number => element.TryGetDouble(out var number) && number != 0 && !double.IsNaN(number),
            _ => false,
        };
    }

    private static JsonElement ToJsonElement(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }
}
