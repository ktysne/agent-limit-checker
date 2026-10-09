using AgentLimitChecker.Core;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.App;

internal sealed class LoggingDetailsPresenter(AppLogger logger) : IDetailsPresenter
{
    public bool Toggle(ShellSnapshot snapshot) { Show(snapshot); return true; }
    public void Show(ShellSnapshot snapshot) => logger.Info("[app] show requested");
}
