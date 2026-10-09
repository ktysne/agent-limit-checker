using System.Diagnostics;
using System.Text;
using System.Windows;
using AgentLimitChecker.Core;
using AgentLimitChecker.Core.Providers;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.App;

internal sealed class LoginLauncher(AppLogger logger) : ILoginLauncher
{
    public bool Start(string target, CodexAccount? account, bool silent)
    {
        var executable = target == "claude" ? CliPaths.ResolveClaudeExecutable() : CliPaths.ResolveCodexExecutable();
        if (executable is null)
        {
            logger.Warn("[login] executable missing " + target);
            if (!silent)
            {
                var message = LoginCommand.MissingCliMessage(target);
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => MessageBox.Show(
                    message.Message, message.Title, MessageBoxButton.OK, MessageBoxImage.Error));
            }
            return false;
        }
        var args = LoginCommand.ArgsFor(target);
        var command = silent ? LoginCommand.BuildSilentPsCommand(executable, args)
            : LoginCommand.BuildLoginPsCommand(executable, args, account is null ? null : new Dictionary<string, string?> { ["CODEX_HOME"] = account.Home });
        // EncodedCommand は cmd の再解析を経ず、パスの特殊文字もそのまま渡せる。
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = !silent,
            CreateNoWindow = silent,
            WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        if (silent)
        {
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", encoded }) start.ArgumentList.Add(arg);
        }
        else start.Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        using var process = Process.Start(start);
        return process is not null;
    }
}
