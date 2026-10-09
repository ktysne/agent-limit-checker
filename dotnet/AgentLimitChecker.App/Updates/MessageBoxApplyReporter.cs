using System.Windows;
using AgentLimitChecker.Core.Updates;

namespace AgentLimitChecker.App.Updates;
internal sealed class MessageBoxApplyReporter(UpdateApplyLog log) : IUpdateApplyReporter
{
    public void Log(string message) => log.Write(message);

    public void ShowError(string message)
    {
        log.Write($"利用者に通知しました: {message}");
        MessageBox.Show(message, "Agent Limit Checker の更新", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
