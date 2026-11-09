namespace WinStatusBar.Core.Settings;

public static class BarSpace
{
    public static (double Left, double Right) Allocate(double width, double trigger, double leftMinimum, double rightMinimum)
    {
        // The search must remain at the geometric center regardless of either group's contents.
        double half = Math.Max(0, width - trigger) / 2;
        return (half, half);
    }
}
