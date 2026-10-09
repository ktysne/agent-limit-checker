using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

namespace AgentLimitChecker.Core.Providers.Codex;

internal sealed class CodexClient
{
    private readonly string home;
    private readonly Func<CodexStartInfo, ICodexProcess> start;
    private readonly Func<string?> executable;
    private readonly Func<string, string?> signature;
    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private ICodexProcess? process;
    private string? authSignature;
    private long nextId;
    private bool closed;

    internal CodexClient(string home, Func<CodexStartInfo, ICodexProcess>? start = null, Func<string?>? executable = null,
        Func<string, string?>? signature = null, IReadOnlyDictionary<string, string>? environment = null, TimeProvider? time = null)
    {
        this.home = home;
        this.start = start ?? CodexProcess.Start;
        this.environment = environment ?? CliPaths.CurrentEnvironment();
        this.executable = executable ?? (() => CliPaths.ResolveCodexExecutable(this.environment));
        this.signature = signature ?? AuthSignature;
        this.time = time ?? TimeProvider.System;
    }

    internal static string? AuthSignature(string home)
    {
        try
        {
            var file = new FileInfo(CodexProvider.AuthFilePath(home));
            return file.Exists ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}" : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static IReadOnlyDictionary<string, string> BuildChildEnv(IReadOnlyDictionary<string, string> env, string home)
    {
        string[] allow = ["HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "USERNAME", "TEMP", "TMP", "SystemRoot", "windir", "PATH", "PATHEXT", "LANG", "LC_ALL", "CODEX_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME"];
        var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in allow) if (env.TryGetValue(key, out var value)) child[key] = value;
        child["CODEX_HOME"] = home;
        return child;
    }

    internal static CodexStartInfo BuildStartInfo(string exe, IReadOnlyDictionary<string, string> env, string home, Func<string, bool>? fileExists = null)
    {
        if (exe.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            // npm の .ps1 の shim は標準入力を `$input |` で渡すため、入力が終わるまで node へ届かず initialize が返らない。
            // shim と同じ規則で node と codex.js を直接起動する。
            fileExists ??= File.Exists;
            var dir = Path.GetDirectoryName(exe) ?? "";
            var script = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (fileExists(script))
            {
                var localNode = Path.Combine(dir, "node.exe");
                return new(fileExists(localNode) ? localNode : "node.exe", $"\"{script}\" app-server", BuildChildEnv(env, home));
            }
            return new("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{exe}\" app-server", BuildChildEnv(env, home));
        }
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return new(CliPaths.Get(env, "ComSpec") ?? "cmd.exe", $"/d /s /c \"\"{exe}\" app-server\"", BuildChildEnv(env, home));
        return new(exe, "app-server", BuildChildEnv(env, home));
    }

    internal async Task<JsonElement> ReadRateLimitsAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync().ConfigureAwait(false);
            var currentSignature = signature(home);
            if (authSignature != null && currentSignature != null && authSignature != currentSignature) await RestartAsync().ConfigureAwait(false);
            try { return await ReadOnceAsync().ConfigureAwait(false); }
            catch (ProviderException ex) when (CodexUsageParser.IsRestartableError(ex))
            {
                await RestartAsync().ConfigureAwait(false);
                return await ReadOnceAsync().ConfigureAwait(false);
            }
        }
        finally { lifecycle.Release(); }
    }

    private async Task EnsureStartedAsync()
    {
        lock (gate)
        {
            if (closed) throw new ProviderException("codex_process_exited", "codex app-server を停止しました。");
            if (process is { HasExited: false }) return;
        }
        var exe = executable() ?? throw new ProviderException("codex_cli_missing", "Codex CLI が見つかりません。`npm i -g @openai/codex` を実行してください。");
        ICodexProcess child;
        try { child = start(BuildStartInfo(exe, environment, home)); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new ProviderException("codex_spawn_failed", "codex app-server を起動できません: " + SafeFailureDetail(ex));
        }
        lock (gate)
        {
            if (closed) { child.Dispose(); throw new ProviderException("codex_process_exited", "codex app-server を停止しました。"); }
            process = child;
            child.Output += line => OnOutput(child, line);
            child.Exited += code => OnExit(child, code);
            child.Failed += () => OnFailure(child);
            child.StartReading();
        }
        try
        {
            await RequestAsync("initialize", new { clientInfo = new { name = "agent-limit-checker", version = "0.1.0" }, capabilities = new { } }, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            lock (gate)
            {
                if (ReferenceEquals(process, child) && !child.HasExited)
                {
                    try { child.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "initialized", @params = new { } })); }
                    catch (IOException) { }
                    authSignature = signature(home);
                }
            }
        }
        catch { Stop(); throw; }
    }

    private async Task<JsonElement> ReadOnceAsync()
    {
        var envelope = await RequestAsync("account/rateLimits/read", new { }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var result = CodexUsageParser.Property(envelope, "result");
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ProviderException("codex_rpc_error", "account/rateLimits/read のレスポンスに result がありません");
        return result;
    }
    private async Task RestartAsync() { Stop(); await EnsureStartedAsync().ConfigureAwait(false); }

    internal async Task<JsonElement> RequestAsync(string method, object parameters, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closed || process == null || process.HasExited) throw new ProviderException("codex_process_exited", "codex app-server が起動していません");
            pending[id] = completion;
            try { process.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters })); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                pending.TryRemove(id, out _);
                throw new ProviderException("codex_write_failed", "stdin への書き込みに失敗: " + SafeFailureDetail(ex));
            }
        }
        try { return await completion.Task.WaitAsync(timeout, time).ConfigureAwait(false); }
        catch (TimeoutException) { throw new ProviderException("codex_timeout", $"RPC {method} がタイムアウトしました"); }
        finally { pending.TryRemove(id, out _); }
    }

    private void OnOutput(ICodexProcess source, string line)
    {
        lock (gate)
        {
            if (!ReferenceEquals(source, process)) return;
            try
            {
                using var json = JsonDocument.Parse(line);
                var msg = json.RootElement;
                var id = CodexUsageParser.Property(msg, "id");
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var key) || !pending.TryRemove(key, out var entry)) return;
                var error = CodexUsageParser.Property(msg, "error");
                if (error.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False)) entry.TrySetException(CodexUsageParser.MakeCodexRpcError(error));
                else entry.TrySetResult(msg.Clone());
            }
            catch (JsonException) { }
        }
    }
    private void OnExit(ICodexProcess source, int? code)
    {
        lock (gate)
        {
            if (!ReferenceEquals(source, process)) return;
            process = null; authSignature = null;
            FailAll(new("codex_process_exited", $"codex app-server が終了しました (exit {code?.ToString() ?? "null"})"));
        }
        source.Dispose();
    }
    private void OnFailure(ICodexProcess source)
    {
        lock (gate)
        {
            if (ReferenceEquals(source, process)) FailAll(new("codex_process_exited", "codex app-server プロセスエラー: 入出力エラー"));
        }
    }
    internal void Stop()
    {
        ICodexProcess? child;
        lock (gate)
        {
            child = process; process = null; authSignature = null;
            FailAll(new("codex_process_exited", "codex app-server を停止しました。"));
        }
        child?.Dispose();
    }
    internal void Close() { lock (gate) { closed = true; Stop(); } }
    private void FailAll(ProviderException error)
    {
        foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var entry)) entry.TrySetException(error);
    }
    // 外部例外の本文には応答や認証情報が混ざり得るため、種類だけを表示する。
    private static string SafeFailureDetail(Exception error) => error is Win32Exception win32 ? $"Windows エラー {win32.NativeErrorCode}" : "入出力エラー";
}
