namespace AgentLimitChecker.Core.Providers;

public static class CliPaths
{
    internal static IReadOnlyDictionary<string, string> CurrentEnvironment() => Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);

    internal static string? Get(IReadOnlyDictionary<string, string> env, string key) => env.GetValueOrDefault(key);
    private static IEnumerable<string> PathDirs(IReadOnlyDictionary<string, string> env) =>
        (Get(env, "PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    private static bool Exists(string? path) => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

    public static string? ResolveExecutableFromPath(IEnumerable<string> commandNames,
        IReadOnlyDictionary<string, string>? env = null, IEnumerable<string>? extraDirs = null)
    {
        env ??= CurrentEnvironment();
        foreach (var dir in PathDirs(env).Concat(extraDirs ?? []))
            foreach (var name in commandNames)
            {
                var candidate = Path.Combine(dir, name);
                if (Exists(candidate)) return candidate;
            }
        return null;
    }

    public static IReadOnlyList<string> ResolveCodexExecutables(IReadOnlyDictionary<string, string>? env = null)
    {
        env ??= CurrentEnvironment();
        var configured = Get(env, "CODEX_PATH");
        if (Exists(configured)) return [configured!];
        var normalDirs = PathDirs(env).Where(d => !IsChatgptExtensionBin(d)).ToArray();
        var npmDirs = new[] { Path.Combine(Get(env, "APPDATA") ?? "", "npm") };
        string[] extensionDirs = [];
        try
        {
            var root = Path.Combine(Get(env, "USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
            var latest = Directory.GetDirectories(root).Where(d => Path.GetFileName(d).StartsWith("openai.chatgpt-", StringComparison.Ordinal))
                .OrderDescending(StringComparer.CurrentCulture).FirstOrDefault();
            if (latest != null) extensionDirs = [Path.Combine(latest, "bin", "windows-x86_64")];
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var found = new List<string>();
        foreach (var dirs in new[] { normalDirs, npmDirs, extensionDirs })
            foreach (var name in new[] { "codex.exe", "codex.cmd", "codex.bat", "codex.ps1", "codex" })
                foreach (var dir in dirs)
                {
                    var candidate = Path.Combine(dir, name);
                    if (Exists(candidate)) found.Add(candidate);
                }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? ResolveCodexExecutable(IReadOnlyDictionary<string, string>? env = null) => ResolveCodexExecutables(env).FirstOrDefault();
    public static string? ResolveClaudeExecutable(IReadOnlyDictionary<string, string>? env = null)
    {
        env ??= CurrentEnvironment();
        var configured = Get(env, "CLAUDE_PATH");
        if (Exists(configured)) return configured;
        return ResolveExecutableFromPath(["claude.exe", "claude.ps1", "claude.cmd", "claude.bat", "claude"], env,
            [Path.Combine(Get(env, "APPDATA") ?? "", "npm")]);
    }

    internal static bool IsChatgptExtensionBin(string dir)
    {
        var normalized = dir.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/.vscode/extensions/openai.chatgpt-", StringComparison.Ordinal)
            && normalized.EndsWith("/bin/windows-x86_64", StringComparison.Ordinal);
    }
}
