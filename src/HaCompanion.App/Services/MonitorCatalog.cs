// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices;

namespace HaCompanion.App.Services;

/// <summary>One attached display, as offered in the quick panel's monitor picker.</summary>
public sealed record MonitorEntry(string DeviceKey, int Width, int Height, bool IsPrimary, IntPtr Handle);

/// <summary>
/// Enumerates attached displays and resolves the stored quick-panel monitor setting.
/// The device key is the GDI name (\\.\DISPLAYn) — stable while the topology is
/// unchanged; when the stored display is gone (undocked laptop, unplugged screen)
/// the panel falls back to the primary one, mirroring the "unknown value → default"
/// handling of QuickPanelStartView.
/// </summary>
public static class MonitorCatalog
{
    /// <summary>Sentinel meaning "follow the primary display" (the default).</summary>
    public const string PrimaryKey = "primary";

    public static IReadOnlyList<MonitorEntry> Enumerate()
    {
        var list = new List<MonitorEntry>();
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hMon, ref mi))
                list.Add(new MonitorEntry(
                    mi.szDevice,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    hMon));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// The HMONITOR for the stored setting; the primary display when the setting is
    /// unset/unknown; IntPtr.Zero only if enumeration itself fails (caller falls back).
    /// </summary>
    public static IntPtr Resolve(string? deviceKey)
    {
        var monitors = Enumerate();
        if (monitors.Count == 0)
            return IntPtr.Zero;
        if (!string.IsNullOrEmpty(deviceKey) && deviceKey != PrimaryKey
            && monitors.FirstOrDefault(m => string.Equals(m.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match.Handle;
        return (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).Handle;
    }

    private const int MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW mi);
}
