using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentLimitChecker.Core.Providers.Codex;

internal static partial class CodexUsageParser
{
    internal static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    private static IEnumerable<JsonElement> Profiles(JsonElement dto)
    {
        var profiles = Property(dto, "rateLimitsByLimitId");
        return profiles.ValueKind == JsonValueKind.Object ? profiles.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Value) : [];
    }
    private static IEnumerable<JsonElement> Snapshots(JsonElement dto) => new[] { Property(dto, "rateLimits") }.Concat(Profiles(dto));

    internal static string? ExtractPlanLabel(JsonElement dto)
    {
        var raw = Text(Property(Property(dto, "rateLimits"), "planType"));
        if (string.IsNullOrEmpty(raw)) raw = Profiles(dto).Select(p => Text(Property(p, "planType"))).FirstOrDefault(p => p != null);
        return string.IsNullOrEmpty(raw) ? null : char.ToUpperInvariant(raw[0]) + raw[1..].ToLowerInvariant();
    }

    internal static RateLimit? PickWindow(JsonElement dto, int minutes)
    {
        foreach (var snapshot in Snapshots(dto))
            foreach (var name in new[] { "primary", "secondary" })
            {
                var window = Property(snapshot, name);
                if (Number(Property(window, "windowDurationMins")) != minutes) continue;
                var used = Number(Property(window, "usedPercent"));
                var resets = Number(Property(window, "resetsAt"));
                return used.HasValue && resets.HasValue ? new(Math.Max(0, used.Value / 100), resets.Value * 1000) : null;
            }
        return null;
    }

    internal static CreditBalance? ParseCredits(JsonElement dto)
    {
        var nodes = Snapshots(dto).Select(p => Property(p, "credits")).Where(p => p.ValueKind == JsonValueKind.Object).ToArray();
        var node = nodes.FirstOrDefault(p => Property(p, "hasCredits").ValueKind == JsonValueKind.True);
        if (node.ValueKind == JsonValueKind.Undefined) node = nodes.FirstOrDefault();
        if (Property(node, "hasCredits").ValueKind != JsonValueKind.True) return null;
        if (Property(node, "unlimited").ValueKind == JsonValueKind.True) return new(null, null, true);
        var balance = Property(node, "balance");
        double? amount = Number(balance);
        if (balance.ValueKind == JsonValueKind.String && double.TryParse(balance.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) amount = parsed;
        return amount.HasValue && double.IsFinite(amount.Value) && amount > 0 ? new(amount, null, false) : null;
    }

    internal static ResetCreditBalance? ParseResetCredits(JsonElement dto)
    {
        var node = Property(dto, "rateLimitResetCredits");
        var count = Number(Property(node, "availableCount"));
        if (!count.HasValue || count <= 0) return null;
        var credits = Property(node, "credits");
        var expirations = credits.ValueKind == JsonValueKind.Array ? credits.EnumerateArray()
            .Where(p => Text(Property(p, "status")) == "available").Select(p => Number(Property(p, "expiresAt")))
            .Where(p => p.HasValue).Select(p => p!.Value * 1000).ToArray() : [];
        return new(count.Value, expirations.Length > 0 ? expirations.Min() : null);
    }

    internal static bool IsRestartableError(ProviderException? error) => error != null &&
        (error.Code == "codex_process_exited" || error.Code == "codex_rpc_error" && (error.Restartable || AuthErrorPattern().IsMatch(error.Message)));

    internal static ProviderException MakeCodexRpcError(JsonElement raw)
    {
        var restartable = AuthErrorPattern().IsMatch(raw.GetRawText());
        return new("codex_rpc_error", restartable ? "Codex の認証が失効しています。ログインし直してください。" : "Codex RPC エラー: rate limit の取得に失敗しました", restartable);
    }

    [GeneratedRegex(@"401|unauthor|token_invalid|invalid_grant|sign(?:ed|ing)?\s*in", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthErrorPattern();
}
