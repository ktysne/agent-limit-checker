using AgentLimitChecker.Core.Providers.Codex;

namespace AgentLimitChecker.Tests;

public sealed class CodexHomesTests
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();
    private static CodexAccount Account(string label, bool isDefault = false) => new(label, label, label, Path.Combine(label, "auth.json"), isDefault);
    private static ProviderTestDirectory MakeHomeDir()
    {
        var dir = new ProviderTestDirectory();
        dir.FileAt(".codex", "auth.json"); dir.FileAt(".codex-review", "config.toml");
        Directory.CreateDirectory(Path.Combine(dir.Root, ".codex-empty"));
        dir.FileAt(".codexfile"); dir.FileAt("other", "auth.json");
        return dir;
    }
    [Fact]
    public void DiscoverCodexHomes_TakesOnlyDirectoriesWithMarkers()
    {
        using var dir = MakeHomeDir();
        var accounts = CodexHomes.DiscoverCodexHomes(dir.Root, Empty);
        Assert.Equal(new[] { ".codex", ".codex-review" }, accounts.Select(a => a.Label));
        Assert.Equal(Path.Combine(dir.Root, ".codex"), accounts[0].Home);
        Assert.Equal(Path.Combine(dir.Root, ".codex", "auth.json"), accounts[0].AuthFile);
        Assert.Equal(CodexHomes.NormalizeHomePath(accounts[0].Home), accounts[0].Id);
    }
    [Fact]
    public void DiscoverCodexHomes_PutsDefaultFirstAndMarksIt()
    {
        using var dir = MakeHomeDir();
        var accounts = CodexHomes.DiscoverCodexHomes(dir.Root, Empty);
        Assert.Equal(".codex", accounts[0].Label); Assert.True(accounts[0].IsDefault); Assert.False(accounts[1].IsDefault);
    }
    [Fact]
    public void DiscoverCodexHomes_DeduplicatesConfiguredHome()
    {
        using var dir = MakeHomeDir();
        var accounts = CodexHomes.DiscoverCodexHomes(dir.Root, new Dictionary<string, string> { ["CODEX_HOME"] = Path.Combine(dir.Root, ".codex") });
        Assert.Single(accounts, a => a.Label == ".codex");
        Assert.Equal(new[] { ".codex", ".codex-review" }, accounts.Select(a => a.Label));
    }
    [Fact]
    public void DiscoverCodexHomes_IncludesExternalConfiguredHomeAsDefault()
    {
        using var dir = MakeHomeDir(); using var external = new ProviderTestDirectory(); external.FileAt("auth.json");
        var accounts = CodexHomes.DiscoverCodexHomes(dir.Root, new Dictionary<string, string> { ["CODEX_HOME"] = external.Root });
        Assert.Equal(external.Root, accounts[0].Home); Assert.True(accounts[0].IsDefault);
        Assert.False(accounts.Single(a => a.Label == ".codex").IsDefault);
    }
    [Fact]
    public void DiscoverCodexHomes_ReturnsNothingWithoutMarkers()
    {
        using var dir = new ProviderTestDirectory(); Directory.CreateDirectory(Path.Combine(dir.Root, ".codex-empty"));
        Assert.Empty(CodexHomes.DiscoverCodexHomes(dir.Root, Empty));
    }
    [Fact]
    public void DiscoverCodexHomes_NeverThrowsOnUnreadableHome()
    {
        using var dir = new ProviderTestDirectory();
        Assert.Empty(CodexHomes.DiscoverCodexHomes(Path.Combine(dir.Root, "missing"), Empty));
    }
    [Fact]
    public void DefaultCodexHome_UsesUserHomeUnlessConfigured()
    {
        using var dir = new ProviderTestDirectory();
        Assert.Equal(Path.Combine(dir.Root, ".codex"), CodexHomes.DefaultCodexHome(dir.Root, Empty));
        var custom = Path.Combine(dir.Root, ".codex-review");
        Assert.Equal(custom, CodexHomes.DefaultCodexHome(dir.Root, new Dictionary<string, string> { ["CODEX_HOME"] = custom }));
    }
    [Fact]
    public void AccountDisplayName_LabelsHomeOnlyForMultipleAccounts()
    {
        var a = Account(".codex"); var b = Account(".codex-review");
        Assert.Equal("Codex", CodexHomes.AccountDisplayName(a, [a]));
        Assert.Equal("Codex (.codex)", CodexHomes.AccountDisplayName(a, [a, b]));
        Assert.Equal("Codex (.codex-review)", CodexHomes.AccountDisplayName(b, [a, b]));
    }
    [Fact]
    public void AccountDisplayName_UsesCustomNameVerbatim()
    {
        var a = Account(".codex"); var b = Account(".codex-review");
        var names = new Dictionary<string, object?> { [a.Label] = "Codex Main", [b.Label] = "レビュー用" };
        Assert.Equal("Codex Main", CodexHomes.AccountDisplayName(a, [a], names));
        Assert.Equal("Codex Main", CodexHomes.AccountDisplayName(a, [a, b], names));
        Assert.Equal("レビュー用", CodexHomes.AccountDisplayName(b, [a, b], names));
    }
    [Fact]
    public void AccountDisplayName_FallsBackWhenNoNameIsSet()
    {
        var a = Account(".codex"); var b = Account(".codex-review");
        Assert.Equal("Codex (.codex-review)", CodexHomes.AccountDisplayName(b, [a, b], new Dictionary<string, object?> { [a.Label] = "Codex Main" }));
        Assert.Equal("Codex (.codex)", CodexHomes.AccountDisplayName(a, [a, b], new Dictionary<string, object?>()));
        Assert.Equal("Codex (.codex)", CodexHomes.AccountDisplayName(a, [a, b], null));
    }
    [Fact]
    public void AccountDisplayName_IgnoresEmptyOrNonStringName()
    {
        var a = Account(".codex");
        foreach (var name in new object?[] { "", "   ", 42 }) Assert.Equal("Codex", CodexHomes.AccountDisplayName(a, [a], new Dictionary<string, object?> { [a.Label] = name }));
    }
    [Fact]
    public void CodexLoginTargets_KeepsListWhenDefaultIsPresent()
    {
        CodexAccount[] accounts = [Account(".codex", true), Account(".codex-review")];
        Assert.Equal(accounts, CodexHomes.CodexLoginTargets(accounts, Account(".codex", true)));
    }
    [Fact]
    public void CodexLoginTargets_AppendsAbsentDefault()
    {
        var a = Account(".codex-review"); var target = Account(".codex", true);
        var result = CodexHomes.CodexLoginTargets([a], target);
        Assert.Equal(2, result.Count); Assert.Same(a, result[0]);
        Assert.Equal(target.Id, result[1].Id); Assert.Equal(target.Home, result[1].Home);
        Assert.Equal("Codex (.codex)", result[1].DisplayName); Assert.Null(target.DisplayName);
    }
    [Fact]
    public void CodexLoginTargets_UsesDefaultAloneForEmptyList()
    {
        var target = Account(".codex", true);
        var result = CodexHomes.CodexLoginTargets([], target);
        Assert.Single(result); Assert.Equal(target.Id, result[0].Id); Assert.Equal("Codex", result[0].DisplayName);
        Assert.Equal(result, CodexHomes.CodexLoginTargets(null, target));
    }
    [Fact]
    public void NormalizeHomePath_DeduplicatesCaseRelativeSegmentsAndTrailingSeparators()
    {
        using var dir = new ProviderTestDirectory();
        Assert.Equal(CodexHomes.NormalizeHomePath(dir.Root), CodexHomes.NormalizeHomePath(dir.Root.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.Equal(CodexHomes.NormalizeHomePath(dir.Root), CodexHomes.NormalizeHomePath(Path.Combine(dir.Root, "child", "..")));
    }
}
