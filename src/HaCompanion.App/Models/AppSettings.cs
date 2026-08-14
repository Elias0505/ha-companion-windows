// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Configuration;

namespace HaCompanion.App.Models;

/// <summary>User-configurable application settings (persisted; token stored encrypted).</summary>
public sealed class AppSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>Human-readable hotkey label, e.g. "Win+Ctrl+H".</summary>
    public string Hotkey { get; set; } = "Win+Ctrl+H";

    /// <summary>Hide the quick panel automatically when it loses focus (you click elsewhere).</summary>
    public bool AutoHideQuickPanel { get; set; } = true;

    /// <summary>Quick panel width in device-independent pixels (320–900).</summary>
    public int QuickPanelWidth { get; set; } = 400;

    /// <summary>UI language code (en, de, es, fr, zh, hi).</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// What the quick panel shows on every open: "last" (remember the last view),
    /// "favorites", or "dash:&lt;url_path&gt;" for a specific HA dashboard.
    /// </summary>
    public string QuickPanelStartView { get; set; } = "last";

    /// <summary>
    /// The view the panel actually showed last ("favorites" or "dash:&lt;url_path&gt;") —
    /// persisted on every switch so "remember last view" survives app restarts.
    /// </summary>
    public string QuickPanelLastView { get; set; } = "favorites";

    /// <summary>Allow resizing the quick panel by dragging the grip on its left edge.</summary>
    public bool QuickPanelDragResize { get; set; } = true;

    /// <summary>Sort quick-panel favourites by category (start-page order) instead of manual order.</summary>
    public bool QuickPanelSortByCategory { get; set; }

    /// <summary>
    /// Which display docks the quick panel: "primary" (default) or a GDI device name
    /// (\\.\DISPLAYn). Unknown/vanished displays fall back to the primary at runtime.
    /// </summary>
    public string QuickPanelMonitor { get; set; } = "primary";

    /// <summary>Show Home Assistant persistent notifications as native Windows toasts.</summary>
    public bool ShowHaNotifications { get; set; } = true;

    /// <summary>
    /// App name shown as the heading (attribution line) of Windows toasts.
    /// Empty = the default "HA Companion". (#9)
    /// </summary>
    public string ToastAppName { get; set; } = string.Empty;

    /// <summary>Report this PC's state to HA as a mobile_app device (opt-in: the active
    /// program and mic/cam state leave the machine).</summary>
    public bool ReportSensors { get; set; }

    /// <summary>
    /// Device name shown in Home Assistant (Settings → Devices). Empty = this PC's
    /// computer name. Renaming updates the HA DEVICE only; existing entity_ids and the
    /// notify.mobile_app_&lt;slug&gt; service keep their original slug. (#8)
    /// </summary>
    public string HaDeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Opt-in: report this PC "home" to the mobile_app device tracker while the app is
    /// connected, and "not_home" on lock/suspend/shutdown. Off by default — for a laptop
    /// reaching HA over VPN/cloud, "connected" does not mean "at home". (#11)
    /// </summary>
    public bool ReportTrackerHome { get; set; }

    /// <summary>Stable device id for the mobile_app registration (GUID, created on enable).</summary>
    public string MobileAppDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// The device name that was actually sent when the mobile_app device was REGISTERED —
    /// HA derives notify.mobile_app_&lt;slug&gt; and the entity_ids from it once, at that moment.
    /// Kept so the UI can show the real service name even after the display name was renamed.
    /// Empty = legacy install (assume the computer name).
    /// </summary>
    public string MobileAppRegisteredName { get; set; } = string.Empty;

    /// <summary>Webhook id returned by the registration (stored encrypted — it grants HA write access).</summary>
    public string MobileAppWebhookId { get; set; } = string.Empty;

    /// <summary>Minutes without input before the "is idle" sensor turns on (1–720).</summary>
    public int IdleSensorThresholdMinutes { get; set; } = 5;

    // --- PC commands HA may send via notify.mobile_app_<device> (per-command opt-in;
    //     ALL commands stay off until the user flips them — existing installs keep
    //     whatever they saved, these defaults apply to fresh ones only) ---

    public bool AllowCmdLock { get; set; }

    public bool AllowCmdMonitorOff { get; set; }

    public bool AllowCmdVolume { get; set; }

    public bool AllowCmdSleep { get; set; }

    public bool AllowCmdShutdown { get; set; }

    public bool AllowCmdLaunch { get; set; }

    /// <summary>Only these executables may be started by command_launch (full paths).</summary>
    public List<string> LaunchWhitelist { get; set; } = new();

    public bool HasConnection =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    public HaConnectionSettings ToConnectionSettings() => new()
    {
        BaseUrl = BaseUrl,
        Token = Token,
        IgnoreCertificateErrors = IgnoreCertificateErrors,
    };
}
