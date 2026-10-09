using System.Reflection;
using System.Runtime.InteropServices;
using AgentLimitChecker.Core.Shell;
using Forms = System.Windows.Forms;

namespace AgentLimitChecker.App;

internal readonly record struct PopoverAnchor(PopoverRect Tray, PopoverRect WorkArea, double ScaleX, double ScaleY)
{
    internal static PopoverAnchor Read(Forms.NotifyIcon? icon)
    {
        var rect = TrayRect(icon);
        var monitor = MonitorFromPoint(new Point(rect.Left, rect.Top), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception();
        var dpiX = 96u; var dpiY = 96u;
        if (GetDpiForMonitor(monitor, 0, out var x, out var y) == 0) { dpiX = x; dpiY = y; }
        var sx = dpiX / 96.0; var sy = dpiY / 96.0;
        return new(new(rect.Left / sx, rect.Top / sy, (rect.Right - rect.Left) / sx, (rect.Bottom - rect.Top) / sy),
            new(info.Work.Left / sx, info.Work.Top / sy, (info.Work.Right - info.Work.Left) / sx,
                (info.Work.Bottom - info.Work.Top) / sy), sx, sy);
    }

    private static Rect TrayRect(Forms.NotifyIcon? icon)
    {
        // NotifyIcon は識別子を公開しないため、WinForms の内部値を使い、取得不能ならカーソルへ退避する。
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (icon is not null && typeof(Forms.NotifyIcon).GetField("_id", flags)?.GetValue(icon) is uint id &&
            typeof(Forms.NotifyIcon).GetField("_window", flags)?.GetValue(icon) is Forms.NativeWindow window)
        {
            var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = window.Handle, Id = id };
            if (Shell_NotifyIconGetRect(ref identifier, out var rect) == 0) return rect;
        }
        GetCursorPos(out var point);
        return new Rect { Left = point.X, Top = point.Y, Right = point.X, Bottom = point.Y };
    }

    internal static void Move(nint window, double leftPixels, double topPixels) =>
        SetWindowPos(window, 0, (int)Math.Round(leftPixels), (int)Math.Round(topPixels), 0, 0, 0x0015);

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
