using System.Text.RegularExpressions;
using AgentLimitChecker.Core;

namespace AgentLimitChecker.Tests;

public sealed class AppLoggerTests
{
    [Fact]
    public void Info_WritesUtcTimestampLevelAndMessage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-limit-checker-{Guid.NewGuid():N}");
        var logFilePath = Path.Combine(directory, "agent-limit-checker.log");

        try
        {
            new AppLogger(logFilePath).Info("[app] ready 4.0.0");

            var line = File.ReadAllText(logFilePath).TrimEnd('\r', '\n');
            Assert.Matches(
                new Regex(@"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] INFO \[app\] ready 4\.0\.0$"),
                line);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Error_DoesNotThrowWhenTheOutputPathIsADirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-limit-checker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var exception = Record.Exception(() => new AppLogger(directory).Error("write failure"));

            Assert.Null(exception);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Info_WritesConcurrentMessagesOnSeparateLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-limit-checker-{Guid.NewGuid():N}");
        var logFilePath = Path.Combine(directory, "agent-limit-checker.log");
        var messages = Enumerable.Range(0, 100).Select(index => $"message-{index}").ToArray();

        try
        {
            var logger = new AppLogger(logFilePath);
            Parallel.ForEach(messages, logger.Info);

            var lines = File.ReadAllLines(logFilePath);
            var matches = lines.Select(line => Regex.Match(
                line,
                @"^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] INFO (message-\d+)$")).ToArray();

            Assert.Equal(messages.Length, lines.Length);
            Assert.All(matches, match => Assert.True(match.Success));
            Assert.Equal(messages.OrderBy(message => message), matches.Select(match => match.Groups[1].Value).OrderBy(message => message));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
