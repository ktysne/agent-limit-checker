using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AgentLimitChecker.Core.Shell;

namespace AgentLimitChecker.App;

internal static class TrayIconRenderer
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    public static Icon Create(int size, ShellSnapshot snapshot)
    {
        using var bitmap = CreateBitmap(size, TrayPresentation.Utilization(snapshot.Claude),
            TrayPresentation.CodexUtilization(snapshot), snapshot.Claude is { Ok: false }, snapshot.CodexAccounts.Any(a => !a.Result.Ok));
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    public static Bitmap CreateBitmap(int size, double? claude, double? codex, bool claudeError = false, bool codexError = false)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = TrayBitmap.Draw(size, claude, codex, claudeError, codexError);
                for (var row = 0; row < size; row++) Marshal.Copy(pixels, row * size * 4, data.Scan0 + row * data.Stride, size * 4);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
