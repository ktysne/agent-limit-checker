using System.Text;
using System.Text.Json.Nodes;
using AgentLimitChecker.Core.Settings;

namespace AgentLimitChecker.Tests;

public sealed class SettingsStoreTests
{
    // 生文字列の改行は checkout 時の改行設定で CRLF になり得るので、保存形式と同じ LF にそろえる。
    private static readonly string DefaultSettingsJson = """
        {
          "pollingIntervalSec": 300,
          "autoLaunch": false,
          "checkForUpdatesOnStartup": true,
          "ntfy": {
            "topicUrl": "",
            "accessToken": "",
            "notifyFiveHour": false,
            "notifyWeekly": false,
            "notifyResetCreditsExpiry": false
          },
          "codexAccountNames": {}
        }
        """.ReplaceLineEndings("\n");

    private static readonly string FullSettingsJson = """
        {
          "pollingIntervalSec": 60,
          "autoLaunch": true,
          "ntfy": {
            "topicUrl": "https://ntfy.sh/agent_limit_checker",
            "accessToken": "tk_test_only",
            "notifyFiveHour": true,
            "notifyWeekly": false,
            "notifyResetCreditsExpiry": true
          },
          "codexAccountNames": {
            ".codex": "Codex Main",
            ".codex-review": "レビュー用"
          }
        }
        """.ReplaceLineEndings("\n");

    [Fact]
    public void NormalizeSettings_DefaultsCodexAccountNamesToAnEmptyMap()
    {
        Assert.Empty(Normalize("{}").CodexAccountNames);
        Assert.Empty(SettingsStore.Normalize(null).CodexAccountNames);
    }

    [Fact]
    public void NormalizeSettings_DropsCodexAccountNamesThatAreNotObjects()
    {
        foreach (var value in new[] { "\".codex\"", "42", "null", "true", "[\".codex\"]" })
        {
            Assert.Empty(Normalize($"{{\"codexAccountNames\":{value}}}").CodexAccountNames);
        }
    }

    [Fact]
    public void NormalizeSettings_KeepsOnlyStringEntriesAndTrimsThem()
    {
        var settings = Normalize("""
            {"codexAccountNames":{
              ".codex":"  Codex Main  ",
              "  .codex-sub  ":"Codex Sub",
              ".codex-number":7,
              ".codex-object":{"name":"x"},
              ".codex-null":null}}
            """);

        Assert.Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".codex"] = "Codex Main",
            [".codex-sub"] = "Codex Sub",
        }, settings.CodexAccountNames);
    }

    [Fact]
    public void NormalizeSettings_DiscardsEmptyNamesSoAccountsKeepTheirDefault()
    {
        var settings = Normalize("""
            {"codexAccountNames":{".codex":"",".codex-sub":"   ","":"nameless"}}
            """);

        Assert.Empty(settings.CodexAccountNames);
    }

    [Fact]
    public void NormalizeSettings_TruncatesDisplayNamesToFortyCharacters()
    {
        var longName = new string('あ', 60);
        var settings = SettingsStore.Normalize(new JsonObject
        {
            ["codexAccountNames"] = new JsonObject { [".codex"] = longName },
        });

        Assert.Equal(40, settings.CodexAccountNames[".codex"].Length);
        Assert.Equal(new string('あ', 40), settings.CodexAccountNames[".codex"]);
    }

    [Fact]
    public void NormalizeSettings_LeavesOtherSettingsUntouched()
    {
        var settings = Normalize("""
            {"pollingIntervalSec":60,"autoLaunch":true,"ntfy":{"topicUrl":" https://ntfy.sh/topic "},"codexAccountNames":{".codex":"Codex Main"}}
            """);

        Assert.Equal(60, settings.PollingIntervalSec);
        Assert.True(settings.AutoLaunch);
        Assert.Equal("https://ntfy.sh/topic", settings.Ntfy.TopicUrl);
        Assert.False(settings.Ntfy.NotifyResetCreditsExpiry);
        Assert.Equal("Codex Main", settings.CodexAccountNames[".codex"]);
    }

    [Fact]
    public void NormalizeSettings_DefaultsResetCreditExpiryNotificationsOffForOldSettings()
    {
        Assert.False(Normalize("{}").Ntfy.NotifyResetCreditsExpiry);
        Assert.False(Normalize("{\"ntfy\":{\"notifyFiveHour\":true}}").Ntfy.NotifyResetCreditsExpiry);
    }

    [Fact]
    public void NormalizeSettings_PreservesResetCreditExpiryNotificationBooleanValues()
    {
        Assert.True(Normalize("{\"ntfy\":{\"notifyResetCreditsExpiry\":true}}").Ntfy.NotifyResetCreditsExpiry);
        Assert.False(Normalize("{\"ntfy\":{\"notifyResetCreditsExpiry\":false}}").Ntfy.NotifyResetCreditsExpiry);
    }

    [Fact]
    public void NormalizeCodexAccountNames_ReturnsANewMap()
    {
        var input = JsonNode.Parse("{\".codex\":\"Codex Main\"}")!;
        var names = SettingsStore.Normalize(new JsonObject { ["codexAccountNames"] = input }).CodexAccountNames;

        Assert.NotSame(input, names);
        Assert.Equal("Codex Main", names[".codex"]);
    }

    [Fact]
    public void Save_WritesJavascriptDefaultsWithTwoSpaceIndentation()
    {
        using var directory = new ProviderTestDirectory();
        var path = Path.Combine(directory.Root, "settings.json");
        var store = new SettingsStore(path);

        store.Save(new JsonObject());

        Assert.Equal(DefaultSettingsJson, File.ReadAllText(path, Encoding.UTF8));
        File.WriteAllText(path, DefaultSettingsJson, new UTF8Encoding(false));
        new SettingsStore(path).Save(new JsonObject());
        Assert.Equal(DefaultSettingsJson, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Save_KeepsNewSettingsInMemoryAndLogsWhenTheFileCannotBeWritten()
    {
        using var directory = new ProviderTestDirectory();
        // 設定ファイルの場所にディレクトリがあると、置き換えが必ず失敗する。
        var path = Path.Combine(directory.Root, "settings.json");
        Directory.CreateDirectory(path);
        var errors = new List<string>();
        var store = new SettingsStore(path, errors.Add);

        var saved = store.Save(new JsonObject { ["pollingIntervalSec"] = 60 });

        Assert.Equal(60, saved.PollingIntervalSec);
        Assert.Equal(60, store.Load().PollingIntervalSec);
        Assert.Equal(["[settings] failed to write"], errors);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void LoadAndSave_PreserveFullyPopulatedElectronSettingsJson()
    {
        using var directory = new ProviderTestDirectory();
        var path = Path.Combine(directory.Root, "settings.json");
        File.WriteAllText(path, FullSettingsJson, new UTF8Encoding(false));
        var store = new SettingsStore(path);

        var settings = store.Load();
        Assert.Equal(60, settings.PollingIntervalSec);
        Assert.Equal("tk_test_only", settings.Ntfy.AccessToken);
        Assert.Equal("レビュー用", settings.CodexAccountNames[".codex-review"]);
        store.Save(new JsonObject());

        Assert.Equal(FullSettingsJson.Replace("  \"ntfy\":", "  \"checkForUpdatesOnStartup\": true,\n  \"ntfy\":"),
            File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Load_UsesDefaultsForMissingAndMalformedFiles()
    {
        using var directory = new ProviderTestDirectory();
        var path = Path.Combine(directory.Root, "missing", "settings.json");
        Assert.Equal(DefaultSettingsJson, Serialize(new SettingsStore(path).Load()));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json", new UTF8Encoding(false));
        Assert.Equal(DefaultSettingsJson, Serialize(new SettingsStore(path).Load()));
    }

    [Fact]
    public void Save_MergesNtfyAndAccountNamesButReplacesOtherSettings()
    {
        using var directory = new ProviderTestDirectory();
        var path = Path.Combine(directory.Root, "settings.json");
        File.WriteAllText(path, """
            {"pollingIntervalSec":60,"autoLaunch":true,"ntfy":{"topicUrl":"https://ntfy.sh/old","notifyFiveHour":true},"codexAccountNames":{".codex":"Main",".codex-review":"Review"}}
            """, new UTF8Encoding(false));
        var store = new SettingsStore(path);

        var result = store.Save(JsonNode.Parse("""
            {"pollingIntervalSec":120,"ntfy":{"notifyWeekly":true},"codexAccountNames":{".codex":"Renamed",".codex-review":""}}
            """) as JsonObject);

        Assert.Equal(120, result.PollingIntervalSec);
        Assert.True(result.AutoLaunch);
        Assert.Equal("https://ntfy.sh/old", result.Ntfy.TopicUrl);
        Assert.True(result.Ntfy.NotifyFiveHour);
        Assert.True(result.Ntfy.NotifyWeekly);
        Assert.Equal(new Dictionary<string, string> { [".codex"] = "Renamed" }, result.CodexAccountNames);
        Assert.Equal(Serialize(result), Serialize(new SettingsStore(path).Load()));
    }

    [Fact]
    public void NormalizeSettings_UsesOnlyAllowedPollingIntervalsAndJavascriptTruthiness()
    {
        var settings = Normalize("""
            {"pollingIntervalSec":90,"autoLaunch":"false","ntfy":{"notifyFiveHour":[],"notifyWeekly":0,"notifyResetCreditsExpiry":""}}
            """);

        Assert.Equal(300, settings.PollingIntervalSec);
        Assert.True(settings.AutoLaunch);
        Assert.True(settings.Ntfy.NotifyFiveHour);
        Assert.False(settings.Ntfy.NotifyWeekly);
        Assert.False(settings.Ntfy.NotifyResetCreditsExpiry);

        var programmaticSettings = SettingsStore.Normalize(new JsonObject
        {
            ["pollingIntervalSec"] = 120,
            ["autoLaunch"] = "false",
            ["ntfy"] = new JsonObject { ["notifyFiveHour"] = new JsonArray() },
        });
        Assert.Equal(120, programmaticSettings.PollingIntervalSec);
        Assert.True(programmaticSettings.AutoLaunch);
        Assert.True(programmaticSettings.Ntfy.NotifyFiveHour);
    }

    [Fact]
    public void NormalizeSettings_PreservesUnknownTopLevelProperties()
    {
        var settings = Normalize("{\"futureOption\":{\"enabled\":true}}");
        Assert.True(settings.AdditionalProperties["futureOption"].GetProperty("enabled").GetBoolean());
    }

    private static AppSettings Normalize(string json) => SettingsStore.Normalize(JsonNode.Parse(json));

    private static string Serialize(AppSettings settings) => System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

}
