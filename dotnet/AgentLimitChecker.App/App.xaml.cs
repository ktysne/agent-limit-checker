using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AgentLimitChecker.Core;
using AgentLimitChecker.Core.Notifications;
using AgentLimitChecker.Core.Providers.Claude;
using AgentLimitChecker.Core.Providers.Codex;
using AgentLimitChecker.Core.Settings;
using AgentLimitChecker.Core.Shell;
using Microsoft.Win32;
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
    private ShellController? _shell;
    private IDetailsPresenter? _details;
    private bool _exiting;
    private ShellSnapshot? _lastTraySnapshot;

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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--export-tray-icons")
        {
            System.IO.Directory.CreateDirectory(e.Args[1]);
            foreach (var size in new[] { 16, 24, 32 })
                foreach (var state in new[] { "initial", "usage", "error" })
                {
                    using var bitmap = TrayIconRenderer.CreateBitmap(size, state == "initial" ? null : .45,
                        state == "initial" ? null : .9, state == "error", state == "error");
                    bitmap.Save(System.IO.Path.Combine(e.Args[1], $"tray-{size}-{state}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            Shutdown();
            return;
        }

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
            (_, _) => Dispatcher.BeginInvoke(() => RequestDetails()),
            null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        _logger.Info($"[app] ready {GetVersion()}");
        if (e.Args.Contains("--hidden", StringComparer.OrdinalIgnoreCase))
        {
            _logger.Info("[app] started hidden");
        }

        var settings = new SettingsStore(_logger.Error);
        var claude = new ClaudeProvider();
        var codex = new CodexProvider();
        var notifier = new NtfyResetNotifier(settings.Load, logger: _logger);
        _details = new LoggingDetailsPresenter(_logger);
        _shell = new ShellController(settings, ShellProviders.Create(claude, codex), new ShellRuntime(_logger.Error),
            new LoginLauncher(_logger), new SettingsAutoLaunchService(settings), notifier.Update, notifier.Dispose, GetVersion(), _logger.Info);
        _shell.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(() => UpdateTray(snapshot));
        _shell.ShowDetailsRequested += () => Dispatcher.BeginInvoke(() => RequestDetails(show: true));
        ShowTrayIcon();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        UpdateTheme();
        await _shell.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _activationWait?.Unregister(null);
        _shell?.Dispose();
        if (_notifyIcon is not null) _notifyIcon.Visible = false;
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
        _trayIcon = TrayIconRenderer.Create(Forms.SystemInformation.SmallIconSize.Width, _shell!.Snapshot);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = TrayPresentation.StartupTooltip,
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) RequestDetails(); };
        RebuildMenu(_shell.Snapshot);
    }

    private void UpdateTray(ShellSnapshot snapshot)
    {
        if (_exiting || _notifyIcon is null) return;
        if (_lastTraySnapshot is { } previousSnapshot && previousSnapshot.FetchedAt == snapshot.FetchedAt &&
            ReferenceEquals(previousSnapshot.CodexAccounts, snapshot.CodexAccounts) &&
            previousSnapshot.Claude == snapshot.Claude && previousSnapshot.AutoLaunchEnabled == snapshot.AutoLaunchEnabled &&
            previousSnapshot.Settings.PollingIntervalSec == snapshot.Settings.PollingIntervalSec) return;
        _lastTraySnapshot = snapshot;
        try
        {
            var icon = TrayIconRenderer.Create(Forms.SystemInformation.SmallIconSize.Width, snapshot);
            try { _notifyIcon.Icon = icon; }
            catch { icon.Dispose(); throw; }
            var previous = _trayIcon;
            _trayIcon = icon;
            previous?.Dispose();
        }
        catch { _logger.Error("[tray] setImage failed"); }
        _notifyIcon.Text = snapshot.FetchedAt == 0 ? TrayPresentation.StartupTooltip : TrayPresentation.Tooltip(snapshot);
        RebuildMenu(snapshot);
    }

    private void RebuildMenu(ShellSnapshot snapshot)
    {
        // Electron 版のトレイのメニュー(Windows 標準のメニュー)と同じ字形と大きさにする。
        var font = System.Drawing.SystemFonts.MenuFont ?? new Font("Segoe UI", 9f, System.Drawing.FontStyle.Regular, GraphicsUnit.Point);
        var menu = new Forms.ContextMenuStrip { Font = font, Renderer = new TrayMenuRenderer() };
        menu.Disposed += (_, _) => font.Dispose();
        foreach (var item in TrayPresentation.Menu(snapshot)) menu.Items.Add(CreateMenuItem(item));
        var previous = _contextMenu;
        _contextMenu = menu;
        _notifyIcon!.ContextMenuStrip = menu;
        previous?.Dispose();
    }

    private Forms.ToolStripItem CreateMenuItem(TrayMenuItem model)
    {
        if (model.Kind == TrayMenuKind.Separator) return new Forms.ToolStripSeparator();
        var item = new Forms.ToolStripMenuItem(model.Label)
        {
            Enabled = model.Enabled, Checked = model.Checked,
            CheckOnClick = model.Kind == TrayMenuKind.Checkbox
        };
        if (model.Kind == TrayMenuKind.Radio)
        {
            item.Tag = TrayMenuKind.Radio;
        }
        if (model.Children is not null)
            foreach (var child in model.Children) item.DropDownItems.Add(CreateMenuItem(child));
        item.Click += async (_, _) =>
        {
            if (_exiting || _shell is null) return;
            switch (model.Command)
            {
                case "details": RequestDetails(); break;
                case "refresh": await _shell.RefreshNowAsync(); break;
                case "claude-login": await _shell.OpenLoginAsync("claude"); break;
                case "codex-login": await _shell.OpenLoginAsync("codex", model.Argument); break;
                case "auto-launch": _shell.SetAutoLaunch(item.Checked); break;
                case "interval": _shell.SetPollingInterval(int.Parse(model.Argument!, System.Globalization.CultureInfo.InvariantCulture)); break;
                case "quit": Shutdown(); break;
            }
        };
        return item;
    }

    private async void RequestDetails(bool show = false)
    {
        if (_exiting || _shell is null || _details is null) return;
        if (show) _details.Show(_shell.Snapshot);
        if (show || _details.Toggle(_shell.Snapshot)) await _shell.OnDetailsOpenedAsync();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _lastTraySnapshot = null; if (_shell is not null) UpdateTray(_shell.Snapshot); });

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => { if (_exiting) return; _lastTraySnapshot = null; UpdateTheme(); if (_shell is not null) UpdateTray(_shell.Snapshot); });

    private void UpdateTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        _shell?.SetTheme(key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? "dark" : "light");
    }

    private static string GetVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version?.ToString(3) ?? "0.0.0";
    }

    private sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Item.Tag is not TrayMenuKind.Radio) { base.OnRenderItemCheck(e); return; }
            var rect = e.ImageRectangle;
            var diameter = Math.Min(rect.Width, rect.Height) * .4f;
            using var brush = new SolidBrush(e.Item.ForeColor);
            e.Graphics.FillEllipse(brush, rect.Left + (rect.Width - diameter) / 2,
                rect.Top + (rect.Height - diameter) / 2, diameter, diameter);
        }
    }
}
