using System.Text.Json;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Codex;

namespace AgentLimitChecker.Tests;

public sealed class CodexClientTests
{
    private sealed class FakeProcess : ICodexProcess
    {
        public bool HasExited { get; private set; }
        public event Action<string>? Output;
        public event Action<int?>? Exited;
        public event Action? Failed;
        internal List<JsonElement> Writes { get; } = [];
        internal Action<FakeProcess, JsonElement>? OnWrite { get; set; }
        internal bool ThrowOnWrite { get; set; }
        public void StartReading() { }
        public void WriteLine(string line)
        {
            if (ThrowOnWrite) throw new IOException("secret-token response body");
            var msg = CodexProviderTests.Json(line); Writes.Add(msg);
            if (msg.GetProperty("method").GetString() == "initialize") Reply(msg, "{}");
            else OnWrite?.Invoke(this, msg);
        }
        internal void Reply(JsonElement request, string result) => Emit($"{{\"id\":{request.GetProperty("id").GetInt64()},\"result\":{result}}}");
        internal void RpcError(JsonElement request, string error) => Emit($"{{\"id\":{request.GetProperty("id").GetInt64()},\"error\":{error}}}");
        internal void Emit(string line) => Output?.Invoke(line);
        internal void Exit(int? code) { HasExited = true; Exited?.Invoke(code); }
        internal void Fail() => Failed?.Invoke();
        public void Dispose() => HasExited = true;
    }
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state); timers.Add(timer); return timer;
        }
        internal void Fire() { foreach (var timer in timers.ToArray()) timer.Fire(); }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static CodexClient Client(FakeProcess process, TimeProvider? time = null) => new("test-home", _ => process, () => "codex.exe", _ => null, new Dictionary<string, string>(), time);
    private static void ReplyToReads(FakeProcess process) => process.OnWrite = (p, msg) => { if (msg.GetProperty("method").GetString() == "account/rateLimits/read") p.Reply(msg, "{}"); };

    [Fact] public async Task ReadRateLimits_ReusesProcessAndInitializesBeforeReading()
    {
        var process = new FakeProcess(); ReplyToReads(process); var starts = 0;
        var client = new CodexClient("home", _ => { starts++; return process; }, () => "codex.exe", _ => null);
        await client.ReadRateLimitsAsync(); await client.ReadRateLimitsAsync();
        Assert.Equal(1, starts);
        Assert.Equal(new[] { "initialize", "initialized", "account/rateLimits/read", "account/rateLimits/read" }, process.Writes.Select(m => m.GetProperty("method").GetString()));
        var init = process.Writes[0].GetProperty("params");
        Assert.Equal("agent-limit-checker", init.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("0.1.0", init.GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Object, init.GetProperty("capabilities").ValueKind);
        client.Stop(); Assert.True(process.HasExited);
    }
    [Fact] public async Task Request_MatchesOutOfOrderIdsAndIgnoresNoise()
    {
        var process = new FakeProcess(); ReplyToReads(process); var client = Client(process);
        await client.ReadRateLimitsAsync(); process.OnWrite = null;
        var first = client.RequestAsync("first", new { }, TimeSpan.FromSeconds(10));
        var second = client.RequestAsync("second", new { }, TimeSpan.FromSeconds(10));
        process.Emit("not json"); process.Emit("{}"); process.Emit("{\"id\":null}"); process.Emit("{\"id\":9999,\"result\":{}}");
        process.Reply(process.Writes[^1], "{\"value\":2}"); process.Reply(process.Writes[^2], "{\"value\":1}");
        Assert.Equal(1, (await first).GetProperty("result").GetProperty("value").GetInt32());
        Assert.Equal(2, (await second).GetProperty("result").GetProperty("value").GetInt32()); client.Stop();
    }
    [Fact] public async Task Request_TimesOutUsingInjectedClock()
    {
        var process = new FakeProcess(); ReplyToReads(process); var time = new ManualTime(); var client = Client(process, time);
        await client.ReadRateLimitsAsync(); process.OnWrite = null;
        var task = client.RequestAsync("pending", new { }, TimeSpan.FromSeconds(10)); time.Fire();
        var error = await Assert.ThrowsAsync<ProviderException>(() => task);
        Assert.Equal("codex_timeout", error.Code); Assert.Equal("RPC pending がタイムアウトしました", error.Message); client.Stop();
    }
    [Fact] public async Task Exit_FailsEveryPendingRequest()
    {
        var process = new FakeProcess(); ReplyToReads(process); var client = Client(process); await client.ReadRateLimitsAsync(); process.OnWrite = null;
        var a = client.RequestAsync("a", new { }, TimeSpan.FromSeconds(10)); var b = client.RequestAsync("b", new { }, TimeSpan.FromSeconds(10)); process.Exit(7);
        foreach (var task in new[] { a, b }) { var error = await Assert.ThrowsAsync<ProviderException>(() => task); Assert.Equal("codex_process_exited", error.Code); Assert.Equal("codex app-server が終了しました (exit 7)", error.Message); }
    }
    [Fact] public async Task Stop_FailsPendingAndIgnoresStaleEvents()
    {
        var processes = new List<FakeProcess>();
        var client = new CodexClient("home", _ => { var p = new FakeProcess(); ReplyToReads(p); processes.Add(p); return p; }, () => "codex.exe", _ => null);
        await client.ReadRateLimitsAsync(); var old = processes[0]; old.OnWrite = null;
        var task = client.RequestAsync("pending", new { }, TimeSpan.FromSeconds(10)); client.Stop();
        Assert.Equal("codex_process_exited", (await Assert.ThrowsAsync<ProviderException>(() => task)).Code);
        await client.ReadRateLimitsAsync(); old.Exit(9); old.Emit("{\"id\":5,\"error\":{}}"); old.Fail();
        await client.ReadRateLimitsAsync(); Assert.Equal(2, processes.Count); Assert.False(processes[1].HasExited); client.Stop();
    }
    [Fact] public async Task AuthSignatureChange_RestartsBeforeReadAndIgnoresUnknownSignature()
    {
        string? signature = "a"; var processes = new List<FakeProcess>();
        var client = new CodexClient("home", _ => { var p = new FakeProcess(); ReplyToReads(p); processes.Add(p); return p; }, () => "codex.exe", _ => signature);
        await client.ReadRateLimitsAsync(); signature = null; await client.ReadRateLimitsAsync(); Assert.Single(processes);
        signature = "b"; await client.ReadRateLimitsAsync(); Assert.Equal(2, processes.Count); Assert.True(processes[0].HasExited); client.Stop();
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task RestartableReadFailure_RetriesExactlyOnce(bool authentication)
    {
        var processes = new List<FakeProcess>();
        var client = new CodexClient("home", _ =>
        {
            var p = new FakeProcess(); processes.Add(p);
            p.OnWrite = (source, msg) =>
            {
                if (msg.GetProperty("method").GetString() != "account/rateLimits/read") return;
                if (authentication) source.RpcError(msg, "{\"data\":{\"status\":401},\"message\":\"secret-token\"}"); else source.Exit(null);
            };
            return p;
        }, () => "codex.exe", _ => null);
        var error = await Assert.ThrowsAsync<ProviderException>(() => client.ReadRateLimitsAsync());
        Assert.Equal(authentication ? "codex_rpc_error" : "codex_process_exited", error.Code);
        Assert.Equal(2, processes.Count); Assert.DoesNotContain("secret-token", error.ToString()); client.Stop();
    }
    [Fact] public async Task NonAuthErrorAndMissingResult_DoNotRestart()
    {
        var process = new FakeProcess(); var starts = 0;
        var client = new CodexClient("home", _ => { starts++; return process; }, () => "codex.exe", _ => null);
        process.OnWrite = (p, msg) => { if (msg.GetProperty("method").GetString() == "account/rateLimits/read") p.RpcError(msg, "{\"message\":\"secret-token\"}"); };
        Assert.Equal("codex_rpc_error", (await Assert.ThrowsAsync<ProviderException>(() => client.ReadRateLimitsAsync())).Code);
        process.OnWrite = (p, msg) => { if (msg.TryGetProperty("id", out var id)) p.Emit($"{{\"id\":{id}}}"); };
        Assert.Equal("account/rateLimits/read のレスポンスに result がありません", (await Assert.ThrowsAsync<ProviderException>(() => client.ReadRateLimitsAsync())).Message);
        Assert.Equal(1, starts); client.Stop();
    }
    [Fact] public async Task WriteFailure_RedactsExternalException()
    {
        var process = new FakeProcess(); ReplyToReads(process); var client = Client(process); await client.ReadRateLimitsAsync(); process.ThrowOnWrite = true;
        var error = await Assert.ThrowsAsync<ProviderException>(() => client.ReadRateLimitsAsync());
        Assert.Equal("codex_write_failed", error.Code); Assert.DoesNotContain("secret-token", error.ToString()); client.Stop();
    }
    [Fact] public async Task MissingCli_ReportsJapaneseMessage()
    {
        var client = new CodexClient("home", executable: () => null);
        var error = await Assert.ThrowsAsync<ProviderException>(() => client.ReadRateLimitsAsync());
        Assert.Equal("codex_cli_missing", error.Code);
        Assert.Equal("Codex CLI が見つかりません。`npm i -g @openai/codex` を実行してください。", error.Message);
    }
    [Fact] public async Task FailedInitialize_DisposesProcess()
    {
        var process = new FakeProcess { ThrowOnWrite = true }; var client = Client(process);
        Assert.Equal("codex_write_failed", (await Assert.ThrowsAsync<ProviderException>(() => client.ReadRateLimitsAsync())).Code);
        Assert.True(process.HasExited);
    }
    [Theory] [InlineData("codex.ps1", "powershell.exe")] [InlineData("codex.cmd", "cmd.exe")] [InlineData("codex.bat", "cmd.exe")] [InlineData("codex.exe", "codex.exe")]
    public void StartInfo_WrapsScriptsAndPinsAllowlistedHome(string exe, string host)
    {
        var info = CodexClient.BuildStartInfo(exe, new Dictionary<string, string> { ["PATH"] = "path", ["CODEX_HOME"] = "other", ["SECRET_TOKEN"] = "secret", ["ComSpec"] = "cmd.exe" }, "home");
        Assert.Equal(host, info.FileName); Assert.Equal("home", info.Environment["CODEX_HOME"]); Assert.Equal("path", info.Environment["PATH"]);
        Assert.False(info.Environment.ContainsKey("SECRET_TOKEN")); Assert.False(info.Environment.ContainsKey("ComSpec"));
        Assert.Equal(exe.EndsWith(".ps1", StringComparison.Ordinal) ? "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"codex.ps1\" app-server" : host == "cmd.exe" ? $"/d /s /c \"\"{exe}\" app-server\"" : "app-server", info.Arguments);
    }
    [Fact] public async Task Provider_NormalizesHomesAndShutsDownOneOrAll()
    {
        using var directory = new ProviderTestDirectory(); var processes = new List<FakeProcess>();
        using var provider = new CodexProvider(home => new(home, _ => { var p = new FakeProcess(); ReplyToReads(p); processes.Add(p); return p; }, () => "codex.exe", _ => null));
        var a = Path.Combine(directory.Root, "a"); var b = Path.Combine(directory.Root, "b");
        var usage = await provider.FetchAsync(a); await provider.FetchAsync(a.ToUpperInvariant()); Assert.Single(processes);
        Assert.Null(usage.FiveHour); Assert.Null(usage.Weekly); Assert.Empty(usage.WeeklyScoped);
        await provider.FetchAsync(b); provider.Shutdown(a); Assert.True(processes[0].HasExited); Assert.False(processes[1].HasExited);
        await provider.FetchAsync(a); Assert.Equal(3, processes.Count); provider.Shutdown(); Assert.All(processes, p => Assert.True(p.HasExited));
    }
    [Fact] public void AuthSignature_TracksMetadataWithoutReadingTokens()
    {
        using var directory = new ProviderTestDirectory(); Assert.Null(CodexClient.AuthSignature(directory.Root));
        var auth = directory.FileAt("auth.json"); var first = CodexClient.AuthSignature(directory.Root);
        File.WriteAllText(auth, "secret-token"); var second = CodexClient.AuthSignature(directory.Root);
        Assert.NotNull(first); Assert.NotEqual(first, second); Assert.DoesNotContain("secret-token", second!);
    }
}
