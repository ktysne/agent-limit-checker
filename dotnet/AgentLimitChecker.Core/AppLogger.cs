using System.Globalization;
using System.Text;

namespace AgentLimitChecker.Core;

public sealed class AppLogger(string logFilePath)
{
    private static readonly object WriteLock = new();

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var line = $"[{timestamp}] {level} {message}{Environment.NewLine}";

        lock (WriteLock)
        {
            try
            {
                var directory = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(logFilePath, line, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    public static AppLogger CreateDefault()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "agent-limit-checker",
            "logs",
            "agent-limit-checker.log");

        return new AppLogger(logPath);
    }
}
