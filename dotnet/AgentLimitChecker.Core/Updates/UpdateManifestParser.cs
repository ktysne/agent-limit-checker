using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentLimitChecker.Core.Updates;
public sealed record UpdateManifestParseResult(UpdateInfo? Info, string? Error)
{
    public static UpdateManifestParseResult Success(UpdateInfo info) => new(info, null);

    public static UpdateManifestParseResult Failure(string error) => new(null, error);
}
public static class UpdateManifestParser
{
    public const int SupportedSchema = 2;
    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
    private static readonly Regex Sha256Pattern = new(
        @"^[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
    public static UpdateManifestParseResult Parse(string? json, bool allowDevelopmentHosts = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return UpdateManifestParseResult.Failure("最新バージョンの情報が空でした。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return UpdateManifestParseResult.Failure($"最新バージョンの情報を JSON として解釈できませんでした: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateManifestParseResult.Failure("最新バージョンの情報の形式が想定と異なります。");
            }

            if (!root.TryGetProperty("schema", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schema))
            {
                return UpdateManifestParseResult.Failure("最新バージョンの情報に schema がありません。");
            }

            if (schema != SupportedSchema)
            {
                return UpdateManifestParseResult.Failure($"未対応の schema です: {schema}");
            }

            if (!root.TryGetProperty("latest", out var latest) || latest.ValueKind != JsonValueKind.Object)
            {
                return UpdateManifestParseResult.Failure("最新バージョンの情報に latest がありません。");
            }

            var versionText = ReadString(latest, "version");
            if (versionText is null)
            {
                return UpdateManifestParseResult.Failure("最新バージョンの情報に version がありません。");
            }

            if (!VersionPattern.IsMatch(versionText) || !Version.TryParse(versionText, out var version))
            {
                return UpdateManifestParseResult.Failure($"version の形式が X.Y.Z ではありません: {versionText}");
            }

            var urlText = ReadString(latest, "url");
            if (urlText is null)
            {
                return UpdateManifestParseResult.Failure("最新バージョンの情報に url がありません。");
            }

            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url))
            {
                return UpdateManifestParseResult.Failure($"url が絶対 URI ではありません: {urlText}");
            }

            if (!UpdateUrlPolicy.IsAllowedPackageUrl(url, versionText, allowDevelopmentHosts))
            {
                if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return UpdateManifestParseResult.Failure($"url が https ではありません: {urlText}");
                }

                return UpdateManifestParseResult.Failure($"url のホストが配布サイトではありません: {url.Host}");
            }

            var sha256Text = ReadString(latest, "sha256");
            if (sha256Text is null || !Sha256Pattern.IsMatch(sha256Text))
            {
                return UpdateManifestParseResult.Failure("sha256 は必須の 64 桁の 16 進文字列です。");
            }

            return UpdateManifestParseResult.Success(new UpdateInfo(version, versionText, url, sha256Text.ToLowerInvariant()));
        }
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
