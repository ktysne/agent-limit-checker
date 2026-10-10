namespace AgentLimitChecker.Core.Shell;

public readonly record struct PopoverRect(double X, double Y, double Width, double Height);
public readonly record struct PopoverLocation(double Left, double Top, double MaxHeight);

public static class PopoverPlacement
{
    public const double Width = 360;
    public const double MinimumHeight = 200;
    public const double HardMaximumHeight = 900;
    public const double EdgeMargin = 4;
    public const double TrayGap = 8;

    public static double MaximumHeight(PopoverRect workArea) => Math.Max(0, Math.Min(HardMaximumHeight, workArea.Height - EdgeMargin * 2));

    public static PopoverLocation Calculate(PopoverRect tray, PopoverRect workArea, double height)
    {
        var maxHeight = MaximumHeight(workArea);
        height = Math.Min(height, maxHeight);
        var left = Math.Floor(tray.X + tray.Width / 2 - Width / 2 + .5);
        var top = Math.Floor(tray.Y - height - TrayGap + .5);
        if (top < workArea.Y) top = tray.Y + tray.Height + TrayGap;
        left = Math.Max(workArea.X + EdgeMargin, Math.Min(workArea.X + workArea.Width - Width - EdgeMargin, left));
        top = Math.Max(workArea.Y + EdgeMargin, Math.Min(workArea.Y + workArea.Height - height - EdgeMargin, top));
        return new(left, top, maxHeight);
    }
}
