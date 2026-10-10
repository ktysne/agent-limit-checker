using System.Globalization;
using System.Text;

namespace AgentLimitChecker.Core.Updates;
public sealed class UpdateApplyLog
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();

    public UpdateApplyLog(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = new FileStream(
                    FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(Utf8WithoutBom.GetBytes(line));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
