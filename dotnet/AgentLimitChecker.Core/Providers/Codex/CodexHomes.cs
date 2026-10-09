namespace AgentLimitChecker.Core.Providers.Codex;

public sealed record CodexAccount(string Id, string Label, string Home, string AuthFile, bool IsDefault, string? DisplayName = null);

public static class CodexHomes
{
    public static string NormalizeHomePath(string home) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)).ToLowerInvariant();
    public static string DefaultCodexHome(string? homeDir = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var configured = CliPaths.Get(env ?? CliPaths.CurrentEnvironment(), "CODEX_HOME");
        return !string.IsNullOrEmpty(configured) ? configured : Path.Combine(homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public static IReadOnlyList<CodexAccount> DiscoverCodexHomes(string? homeDir = null, IReadOnlyDictionary<string, string>? env = null)
    {
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        env ??= CliPaths.CurrentEnvironment();
        var candidates = new List<string>();
        try { candidates.AddRange(Directory.GetFileSystemEntries(homeDir).Where(p => Path.GetFileName(p).StartsWith(".codex", StringComparison.Ordinal))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var configured = CliPaths.Get(env, "CODEX_HOME");
        if (!string.IsNullOrEmpty(configured)) candidates.Add(configured);
        var defaultKey = NormalizeHomePath(DefaultCodexHome(homeDir, env));
        var accounts = new Dictionary<string, CodexAccount>();
        foreach (var candidate in candidates)
        {
            try
            {
                var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                var key = NormalizeHomePath(resolved);
                if (accounts.ContainsKey(key) || !Directory.Exists(resolved)) continue;
                if (!File.Exists(Path.Combine(resolved, "auth.json")) && !File.Exists(Path.Combine(resolved, "config.toml"))) continue;
                accounts[key] = new(key, Path.GetFileName(Path.TrimEndingDirectorySeparator(resolved)), resolved,
                    Path.Combine(resolved, "auth.json"), key == defaultKey);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return accounts.Values.OrderByDescending(a => a.IsDefault).ThenBy(a => a.Label, StringComparer.CurrentCulture).ToArray();
    }

    public static string AccountDisplayName(CodexAccount? account, IReadOnlyList<CodexAccount>? accounts,
        IReadOnlyDictionary<string, object?>? names = null)
    {
        if (string.IsNullOrEmpty(account?.Label)) return "Codex";
        if (names?.GetValueOrDefault(account.Label) is string custom && !string.IsNullOrWhiteSpace(custom)) return custom;
        return accounts == null || accounts.Count <= 1 ? "Codex" : $"Codex ({account.Label})";
    }

    public static IReadOnlyList<CodexAccount> CodexLoginTargets(IReadOnlyList<CodexAccount>? accounts, CodexAccount? defaultAccount)
    {
        var list = (accounts ?? []).ToList();
        if (defaultAccount == null || list.Any(a => a.IsDefault)) return list;
        list.Add(defaultAccount);
        list[^1] = defaultAccount with { DisplayName = AccountDisplayName(defaultAccount, list) };
        return list;
    }
}
