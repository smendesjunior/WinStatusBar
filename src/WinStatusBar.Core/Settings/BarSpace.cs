namespace WinStatusBar.Core.Settings;

public static class BarSpace
{
    /// <summary>
    /// Splits the bar around the search box. The box stays at the geometric center while both groups fit in their half;
    /// otherwise it moves toward the side with spare room, so every module and shortcut stays visible.
    /// </summary>
    public static (double Left, double Right) Allocate(double width, double trigger, double leftMinimum, double rightMinimum)
    {
        double available = Math.Max(0, width - trigger);
        double half = available / 2;
        leftMinimum = Math.Max(0, leftMinimum);
        rightMinimum = Math.Max(0, rightMinimum);
        if (leftMinimum <= half && rightMinimum <= half) return (half, half);
        if (leftMinimum + rightMinimum >= available)
        {
            // Not even the compacted bar fits: share the shortage in proportion to each side.
            double total = leftMinimum + rightMinimum;
            double left = total <= 0 ? half : available * leftMinimum / total;
            return (left, available - left);
        }
        // Only the larger side grows; the other keeps all its spare room minus what was lent.
        if (rightMinimum > half) return (available - rightMinimum, rightMinimum);
        return (leftMinimum, available - leftMinimum);
    }
}
