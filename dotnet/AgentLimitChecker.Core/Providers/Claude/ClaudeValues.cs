using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentLimitChecker.Core.Providers.Claude;

internal static class ClaudeValues
{
    internal static JsonNode? Get(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;
    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }
    internal static double? CoerceNumber(JsonNode? node)
    {
        if (node == null) return 0;
        if (Number(node) is { } number) return number;
        if (node is JsonValue value && value.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        var text = node is JsonArray array ? ArrayText(array) : Text(node);
        if (text == null) return null;
        text = text.Trim();
        if (text.Length == 0) return 0;
        if (text is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (text == "-Infinity") return double.NegativeInfinity;
        if (text.Length > 2 && text[0] == '0')
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, 'o' => 8, _ => 0 };
            if (radix != 0)
            {
                double result = 0;
                foreach (var digit in text.AsSpan(2))
                {
                    var valueOfDigit = digit is >= '0' and <= '9' ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10;
                    if (valueOfDigit < 0 || valueOfDigit >= radix) return null;
                    result = result * radix + valueOfDigit;
                }
                return result;
            }
        }
        if (!Regex.IsMatch(text, @"^[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$")) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    private static string ArrayText(JsonArray array) => string.Join(",", array.Select(item => item switch
    {
        null => "", JsonArray nested => ArrayText(nested), JsonObject => "[object Object]",
        _ => Text(item) ?? item.ToJsonString()
    }));
    internal static bool Truthy(JsonNode? node) => node != null && (node is not JsonValue ||
        (Text(node) is { } text ? text.Length > 0 : Number(node) is { } n ? n != 0 && !double.IsNaN(n) : node.ToJsonString() != "false"));
    internal static JsonNode? Or(JsonNode? first, JsonNode? second) => Truthy(first) ? first : second;
    internal static double? Date(JsonNode? node) => Text(node) is { } text && DateTimeOffset.TryParse(text,
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToUnixTimeMilliseconds() : null;
    internal static double? NormalizeExpiresAt(JsonNode? value)
    {
        if (value == null) return null;
        var n = CoerceNumber(value);
        return n is { } v && double.IsFinite(v) && v > 0 ? v < 10_000_000_000 ? v * 1000 : v : null;
    }
    // CLI と同時に更新すると回転する refresh token が失効するため、前倒しで更新しない。
    internal static bool ShouldRefresh(JsonNode? oauth, double now) => NormalizeExpiresAt(Get(oauth, "expiresAt")) is { } expiry && expiry <= now;
    internal static JsonNode NormalizeScopes(JsonNode? value, JsonNode fallback)
    {
        if (value is JsonArray) return value.DeepClone();
        if (Text(value) is { } text)
        {
            var parts = Regex.Split(text, @"\s+").Where(p => p.Length > 0).ToArray();
            if (parts.Length > 0) return new JsonArray(parts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        }
        return fallback.DeepClone();
    }
    internal static JsonNode? ResolveExpiry(JsonNode? absolute, JsonNode? relative, JsonNode? fallback, double now)
    {
        if (NormalizeExpiresAt(absolute) is { } expiry) return JsonValue.Create(expiry);
        if (relative != null && CoerceNumber(relative) is { } seconds && double.IsFinite(seconds) && seconds > 0)
            return JsonValue.Create(now + seconds * 1000);
        return fallback?.DeepClone();
    }
    internal static JsonObject MergeRefreshResponse(JsonObject existing, JsonNode? json, double now)
    {
        var access = Or(Get(json, "accessToken"), Get(json, "access_token"));
        if (Text(access) is not { Length: > 0 }) throw new ProviderException("claude_refresh_invalid", "OAuth refresh レスポンスに access token がありません。");
        var result = (JsonObject)existing.DeepClone();
        result["accessToken"] = access!.DeepClone();
        result["refreshToken"] = Or(Or(Get(json, "refreshToken"), Get(json, "refresh_token")), existing["refreshToken"])?.DeepClone();
        result["expiresAt"] = ResolveExpiry(Or(Get(json, "expiresAt"), Get(json, "expires_at")),
            Get(json, "expiresIn") ?? Get(json, "expires_in"), existing["expiresAt"], now);
        result["refreshTokenExpiresAt"] = ResolveExpiry(Or(Get(json, "refreshTokenExpiresAt"), Get(json, "refresh_token_expires_at")),
            Get(json, "refreshTokenExpiresIn") ?? Get(json, "refresh_token_expires_in"), existing["refreshTokenExpiresAt"], now);
        result["scopes"] = NormalizeScopes(Or(Get(json, "scopes"), Get(json, "scope")), NormalizeScopes(existing["scopes"], new JsonArray()));
        return result;
    }
    internal static RateLimit? ParseBucket(JsonNode? bucket) => Number(Get(bucket, "utilization")) is { } n ? new(n / 100, Date(Get(bucket, "resets_at"))) : null;
    internal static IReadOnlyList<WeeklyScopedLimit> ParseWeeklyScoped(JsonNode? json)
    {
        var result = new List<WeeklyScopedLimit>();
        if (Get(json, "limits") is JsonArray limits)
            foreach (var entry in limits)
            {
                if (Text(Get(entry, "group")) != "weekly" || Text(Get(entry, "kind")) != "weekly_scoped" || Number(Get(entry, "percent")) is not { } n) continue;
                var model = Get(Get(entry, "scope"), "model");
                var id = Text(Get(model, "id"));
                var label = Text(Get(model, "display_name"));
                result.Add(new(string.IsNullOrEmpty(id) ? null : id, string.IsNullOrEmpty(label) ? "スコープ" : label, n / 100, Date(Get(entry, "resets_at"))));
            }
        if (result.Count > 0) return result;
        var legacy = ParseBucket(Get(json, "seven_day_sonnet"));
        return legacy == null ? [] : [new(null, "Sonnet", legacy.Utilization, legacy.ResetsAt)];
    }
    internal static CreditBalance? ParseCredits(JsonNode? json)
    {
        var balance = Get(Get(json, "spend"), "balance");
        if (balance is not JsonObject obj || !obj.ContainsKey("amount_minor") || !obj.ContainsKey("exponent")) return null;
        var minor = CoerceNumber(obj["amount_minor"]);
        var exponent = CoerceNumber(obj["exponent"]);
        if (minor is not { } m || exponent is not { } e || !double.IsFinite(m) || !double.IsFinite(e)) return null;
        var amount = m / Math.Pow(10, e);
        var currency = Text(obj["currency"]);
        return amount > 0 ? new(amount, string.IsNullOrEmpty(currency) ? "USD" : currency, false) : null;
    }
    internal static CloudCreditBalance? ParseCloudCredit(JsonNode? json)
    {
        var bucket = Get(json, "iguana_necktie");
        var limit = Number(Get(bucket, "limit_dollars"));
        if (limit is not { } l || !double.IsFinite(l) || l <= 0) return null;
        var used = Number(Get(bucket, "used_dollars"));
        var remaining = Number(Get(bucket, "remaining_dollars"));
        if (used is { } u && !double.IsFinite(u)) used = null;
        if (remaining is { } r && !double.IsFinite(r)) remaining = null;
        used ??= remaining is { } rem ? l - rem : null;
        remaining ??= used is { } use ? l - use : null;
        return used is { } usedValue && remaining is { } remainingValue
            ? new(l, usedValue, remainingValue, usedValue / l, Date(Get(bucket, "resets_at")), Text(Get(bucket, "locked_reason"))) : null;
    }
    internal static string? ExtractPlanLabel(JsonNode? oauth)
    {
        var tier = Text(Get(oauth, "rateLimitTier")) ?? "";
        var match = Regex.Match(tier, @"claude[_-](pro|max|team[s]?|enterprise|free)(?:[_-](\d+x))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var name = match.Groups[1].Value.ToLowerInvariant().TrimEnd('s');
            var label = char.ToUpperInvariant(name[0]) + name[1..];
            return match.Groups[2].Success ? label + " " + match.Groups[2].Value.ToLowerInvariant() : label;
        }
        var sub = Text(Get(oauth, "subscriptionType"));
        return string.IsNullOrEmpty(sub) ? null : char.ToUpperInvariant(sub[0]) + sub[1..].ToLowerInvariant();
    }
}
