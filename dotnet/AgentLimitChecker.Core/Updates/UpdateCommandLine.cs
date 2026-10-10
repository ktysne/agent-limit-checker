using System.Globalization;

namespace AgentLimitChecker.Core.Updates;

public enum UpdateCommandKind
{
    Normal,
    Apply,
    InvalidApply,
    Updated,
}
public sealed record UpdateCommandLine(
    UpdateCommandKind Kind,
    int ProcessId = 0,
    string? StagingDirectory = null,
    string? InstallDirectory = null,
    string? UpdatedVersion = null,
    string? UpdateId = null)
{
    public const string ApplyUpdateOption = "--apply-update";
    public const string UpdatedOption = "--updated";
    public const string UpdateIdOption = "--update-id";

    public static readonly UpdateCommandLine NormalStartup = new(UpdateCommandKind.Normal);
    public static UpdateCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count > 0 && string.Equals(args[0], ApplyUpdateOption, StringComparison.Ordinal))
        {
            if (args.Count != 4 || !TryParseProcessId(args[1], out var processId)
                || string.IsNullOrWhiteSpace(args[2]) || string.IsNullOrWhiteSpace(args[3]))
            {
                return new UpdateCommandLine(UpdateCommandKind.InvalidApply);
            }

            return new UpdateCommandLine(UpdateCommandKind.Apply, processId, args[2], args[3]);
        }

        if (args.Count == 4
            && string.Equals(args[0], UpdatedOption, StringComparison.Ordinal)
            && string.Equals(args[2], UpdateIdOption, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(args[1])
            && UpdateLayout.IsUpdateId(args[3]))
        {
            return new UpdateCommandLine(UpdateCommandKind.Updated, UpdatedVersion: args[1], UpdateId: args[3]);
        }

        return NormalStartup;
    }
    public static IReadOnlyList<string> BuildApplyArguments(int processId, string stagingDirectory, string installDirectory) =>
        [ApplyUpdateOption, processId.ToString(CultureInfo.InvariantCulture), stagingDirectory, installDirectory];
    public static IReadOnlyList<string> BuildUpdatedArguments(string version, string updateId) =>
        [UpdatedOption, version, UpdateIdOption, updateId];

    private static bool TryParseProcessId(string text, out int processId)
    {
        processId = 0;
        return !string.IsNullOrEmpty(text)
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out processId)
            && processId > 0;
    }
}
