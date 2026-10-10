using System.Windows.Interop;
using System.Windows.Threading;
using AgentLimitChecker.App.Views;
using AgentLimitChecker.Core.Shell;
using Forms = System.Windows.Forms;

namespace AgentLimitChecker.App;

internal sealed class PopoverDetailsPresenter : IDetailsPresenter, IDisposable
{
    private readonly ShellController controller;
    private readonly PopoverWindow window;
    private readonly Func<Forms.NotifyIcon?> tray;
    private readonly DispatcherTimer fade;
    private int fadeStep;
    private double fadeStart;
    private double fadeTarget;
    private bool repositioning;
    private bool positionQueued;
    private bool disposed;

    internal PopoverDetailsPresenter(ShellController controller, Func<Forms.NotifyIcon?> tray, Action quit)
    {
        this.controller = controller; this.tray = tray;
        window = new(controller, quit);
        fade = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Normal, OnFade, window.Dispatcher);
        fade.Stop();
        window.HideRequested += Hide;
        window.SizeChanged += (_, _) => QueuePosition();
        window.DpiChanged += (_, _) => QueuePosition();
        controller.SnapshotChanged += OnSnapshot;
    }

    private void OnSnapshot(ShellSnapshot snapshot) => window.Dispatcher.BeginInvoke(() =>
    {
        if (!disposed) { window.UpdateSnapshot(snapshot); QueuePosition(); }
    });

    public bool Toggle(ShellSnapshot snapshot)
    {
        window.Dispatcher.VerifyAccess();
        if (window.IsVisible && fadeTarget != 0) { Hide(); return false; }
        Show(snapshot); return true;
    }

    public void Show(ShellSnapshot snapshot)
    {
        window.Dispatcher.VerifyAccess();
        window.UpdateSnapshot(snapshot);
        Position();
        if (window.IsVisible) { if (fadeTarget == 0) FadeTo(1); return; }
        window.Opacity = 0;
        window.Show();
        Position();
        window.Activate();
        FadeTo(1);
    }

    private void QueuePosition()
    {
        if (positionQueued || disposed || !window.IsVisible) return;
        positionQueued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            positionQueued = false;
            if (!disposed && window.IsVisible) Position();
        });
    }

    internal void Reposition() => QueuePosition();

    private void Position()
    {
        if (repositioning) return;
        repositioning = true;
        try
        {
            var anchor = PopoverAnchor.Read(tray());
            window.MaxHeight = PopoverPlacement.MaximumHeight(anchor.WorkArea);
            window.MinHeight = Math.Min(PopoverPlacement.MinimumHeight, window.MaxHeight);
            var handle = new WindowInteropHelper(window).EnsureHandle();
            // 先に対象モニターへ移し、WM_DPICHANGED による WPF の計測倍率を確定させる。
            PopoverAnchor.Move(handle, anchor.WorkArea.X * anchor.ScaleX + 8, anchor.WorkArea.Y * anchor.ScaleY + 8);
            window.UpdateLayout();
            var height = window.ActualHeight > 0 ? window.ActualHeight : window.MinHeight;
            var location = PopoverPlacement.Calculate(anchor.Tray, anchor.WorkArea, height);
            // 異なる DPI の仮想デスクトップ原点を WPF に再換算させないため、最終位置は物理座標で設定する。
            PopoverAnchor.Move(handle, location.Left * anchor.ScaleX, location.Top * anchor.ScaleY);
        }
        finally { repositioning = false; }
    }

    private void Hide()
    {
        if (window.IsVisible) FadeTo(0);
    }
    private void FadeTo(double target)
    {
        fade.Stop(); fadeStart = window.Opacity; fadeTarget = target; fadeStep = 0; fade.Start();
    }
    private void OnFade(object? sender, EventArgs e)
    {
        fadeStep++;
        window.Opacity = Math.Clamp(fadeStart + (fadeTarget - fadeStart) * fadeStep / 6, 0, 1);
        if (fadeStep < 6) return;
        fade.Stop();
        if (fadeTarget == 0) { window.Hide(); window.Opacity = 1; }
    }
    public void Dispose()
    {
        disposed = true; controller.SnapshotChanged -= OnSnapshot; fade.Stop(); window.Close();
    }
}
