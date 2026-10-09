namespace AgentLimitChecker.Tests;

internal sealed class ProviderTestDirectory : IDisposable
{
    internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), ".cache", "p2-tests", Guid.NewGuid().ToString("N"));
    internal ProviderTestDirectory() => Directory.CreateDirectory(Root);
    internal string FileAt(params string[] segments)
    {
        var file = Path.Combine(new[] { Root }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        return file;
    }
    public void Dispose() => Directory.Delete(Root, true);
}
