namespace AgentLimitChecker.Core.Providers.Codex;

public sealed class CodexProvider : IDisposable
{
    private readonly Dictionary<string, CodexClient> clients = new();
    private readonly object gate = new();
    private readonly Func<string, CodexClient> create;
    private bool disposed;

    public CodexProvider() : this(home => new CodexClient(home)) { }
    internal CodexProvider(Func<string, CodexClient> create) => this.create = create;

    public async Task<UsageSnapshot> FetchAsync(string? home = null)
    {
        home ??= CodexHomes.DefaultCodexHome();
        CodexClient client;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = CodexHomes.NormalizeHomePath(home);
            if (!clients.TryGetValue(key, out client!)) clients[key] = client = create(home);
        }
        var dto = await client.ReadRateLimitsAsync().ConfigureAwait(false);
        return new(CodexUsageParser.PickWindow(dto, 300), CodexUsageParser.PickWindow(dto, 10080), [],
            CodexUsageParser.ParseCredits(dto), CodexUsageParser.ParseResetCredits(dto), CodexUsageParser.ExtractPlanLabel(dto));
    }

    public void Shutdown(string? home = null)
    {
        lock (gate)
        {
            if (home == null)
            {
                foreach (var client in clients.Values) client.Close();
                clients.Clear();
            }
            else if (clients.Remove(CodexHomes.NormalizeHomePath(home), out var client)) client.Close();
        }
    }
    internal static string CodexAuthFile(string? home = null) => Path.Combine(home ?? CodexHomes.DefaultCodexHome(), "auth.json");
    public static string AuthFilePath(string? home = null) => CodexAuthFile(home);
    public void Dispose() { lock (gate) { disposed = true; Shutdown(); } }
}
