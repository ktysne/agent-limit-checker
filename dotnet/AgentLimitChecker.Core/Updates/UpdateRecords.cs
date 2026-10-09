using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentLimitChecker.Core.Updates;
public sealed record UpdatePreparedRecord(string Version, string Sha256);
public sealed record UpdateCleanupRecord(string InstallDirectory, string Version, IReadOnlyList<string> Backups);
public static class UpdateRecordSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static string Serialize(UpdatePreparedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Serialize(new PreparedDto { Version = record.Version, Sha256 = record.Sha256 }, Options);
    }

    public static string Serialize(UpdateCleanupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Serialize(
            new CleanupDto
            {
                InstallDirectory = record.InstallDirectory,
                Version = record.Version,
                Backups = record.Backups.ToList(),
            },
            Options);
    }

    public static UpdatePreparedRecord? ParsePrepared(string? json)
    {
        var dto = TryDeserialize<PreparedDto>(json);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Version) || string.IsNullOrWhiteSpace(dto.Sha256))
        {
            return null;
        }

        return new UpdatePreparedRecord(dto.Version, dto.Sha256);
    }

    public static UpdateCleanupRecord? ParseCleanup(string? json)
    {
        var dto = TryDeserialize<CleanupDto>(json);
        if (dto is null
            || string.IsNullOrWhiteSpace(dto.InstallDirectory)
            || string.IsNullOrWhiteSpace(dto.Version)
            || dto.Backups is null
            || dto.Backups.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return new UpdateCleanupRecord(dto.InstallDirectory, dto.Version, dto.Backups);
    }

    private static T? TryDeserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class PreparedDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }

    private sealed class CleanupDto
    {
        [JsonPropertyName("installDir")]
        public string? InstallDirectory { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("backups")]
        public List<string>? Backups { get; set; }
    }
}
