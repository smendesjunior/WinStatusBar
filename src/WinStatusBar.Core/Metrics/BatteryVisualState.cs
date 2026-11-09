namespace WinStatusBar.Core.Metrics;

public enum BatteryVisualState { Normal, Plugged, EnergySaver, Charging, SmartCharging, SmartPaused, SlowCharging, Critical, Unknown }

public static class BatteryVisualStates
{
    public static BatteryVisualState Select(int percent, bool charging, bool plugged, bool saver, bool smartCharging = false, bool slowCharging = false)
    {
        if (percent < 0 || percent > 100) return BatteryVisualState.Unknown;
        if (slowCharging) return BatteryVisualState.SlowCharging;
        if (smartCharging && plugged && !charging) return BatteryVisualState.SmartPaused;
        if (charging) return smartCharging ? BatteryVisualState.SmartCharging : BatteryVisualState.Charging;
        if (plugged) return BatteryVisualState.Plugged;
        if (percent <= 5) return BatteryVisualState.Critical;
        if (saver || percent <= 20) return BatteryVisualState.EnergySaver;
        return BatteryVisualState.Normal;
    }
}
