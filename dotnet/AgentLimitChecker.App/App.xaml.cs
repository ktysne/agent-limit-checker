using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AgentLimitChecker.Core;
using Forms = System.Windows.Forms;

namespace AgentLimitChecker.App;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\AgentLimitChecker.Instance";
    private const string ActivationEventName = @"Local\AgentLimitChecker.Activate";

    private readonly AppLogger _logger = AppLogger.CreateDefault();
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _contextMenu;
    private Icon? _trayIcon;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ApplyVisualDefaults();
    }

    // Electron 版のポップオーバー(renderer/style.css)と同じフォントにする。
    // 言語を指定しないと、WPF は日本語の文字に意図しない代替のフォントを使うことがある。
    // メタデータの上書きは、最初のウィンドウを作る前に 1 回だけ行う必要がある。
    private static void ApplyVisualDefaults()
    {
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ja-JP")));
        Control.FontFamilyProperty.OverrideMetadata(
            typeof(Window),
            new FrameworkPropertyMetadata(new System.Windows.Media.FontFamily("Segoe UI, Yu Gothic UI, Meiryo")));
        Control.FontSizeProperty.OverrideMetadata(
            typeof(Window),
            new FrameworkPropertyMetadata(13.0));
        FrameworkElement.UseLayoutRoundingProperty.OverrideMetadata(
            typeof(Window),
            new FrameworkPropertyMetadata(true));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            activationEvent.Set();
            activationEvent.Dispose();
            instanceMutex.Dispose();
            Shutdown();
            return;
        }

        _activationEvent = activationEvent;
        _instanceMutex = instanceMutex;
        _ownsMutex = true;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => _logger.Info("[app] show requested"),
            null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        _logger.Info($"[app] ready {GetVersion()}");
        if (e.Args.Contains("--hidden", StringComparer.OrdinalIgnoreCase))
        {
            _logger.Info("[app] started hidden");
        }

        ShowTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _notifyIcon?.Dispose();
        _contextMenu?.Dispose();
        _trayIcon?.Dispose();
        _activationEvent?.Dispose();

        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ShowTrayIcon()
    {
        using var iconStream = typeof(App).Assembly.GetManifestResourceStream("AgentLimitChecker.app.ico")
            ?? throw new InvalidOperationException("アプリケーションアイコンを読み込めません。");
        _trayIcon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        _contextMenu = new Forms.ContextMenuStrip();
        var exitItem = _contextMenu.Items.Add("終了");
        exitItem.Click += (_, _) =>
        {
            if (_notifyIcon is not null)
            {
                _notifyIcon.Visible = false;
            }

            Shutdown();
        };

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Agent Limit Checker",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
    }

    private static string GetVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version?.ToString(3) ?? "0.0.0";
    }
}
