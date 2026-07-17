// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Automations;

/// <summary>
/// Windows-side events that can start an automation rule. The stable string keys
/// (see <see cref="WindowsTriggers.ToKey"/>) are what automations.json persists and
/// what the i18n labels (Trig_&lt;key&gt;) are keyed by — never rename them.
/// </summary>
public enum WindowsTrigger
{
    Startup,                            // app/PC start (autostart-at-logon covers boot)
    Lock, Unlock, Logon, Logoff,        // session switches
    Suspend, Resume,                    // power transitions
    Shutdown,                           // session ending (shutdown or logoff)
    DisplayOn, DisplayOff,              // console display power state
    IdleStart, IdleEnd,                 // input idle threshold crossing (param: minutes)
    FullscreenStart, FullscreenEnd,     // a fullscreen/presentation app took/left the screen
    AppStart, AppStop,                  // FOREGROUND app changed (param: process name)
    MicOn, MicOff, CamOn, CamOff,       // capability consent store usage
    AudioStart, AudioStop,              // default render device peak activity
    Schedule,                           // a time of day on chosen weekdays (param: ScheduleSpec)
}

/// <summary>Which extra input a trigger needs from the user.</summary>
public enum TriggerParamKind
{
    None,
    Minutes,      // idle threshold
    ProcessName,  // foreground process to watch
    Schedule,     // time of day + weekday mask (ScheduleSpec)
}

public static class WindowsTriggers
{
    /// <summary>Stable UI/persistence order.</summary>
    public static IReadOnlyList<WindowsTrigger> All { get; } = Enum.GetValues<WindowsTrigger>();

    public static string ToKey(WindowsTrigger trigger) => trigger switch
    {
        WindowsTrigger.Startup => "startup",
        WindowsTrigger.Lock => "lock",
        WindowsTrigger.Unlock => "unlock",
        WindowsTrigger.Logon => "logon",
        WindowsTrigger.Logoff => "logoff",
        WindowsTrigger.Suspend => "suspend",
        WindowsTrigger.Resume => "resume",
        WindowsTrigger.Shutdown => "shutdown",
        WindowsTrigger.DisplayOn => "display_on",
        WindowsTrigger.DisplayOff => "display_off",
        WindowsTrigger.IdleStart => "idle_start",
        WindowsTrigger.IdleEnd => "idle_end",
        WindowsTrigger.FullscreenStart => "fullscreen_start",
        WindowsTrigger.FullscreenEnd => "fullscreen_end",
        WindowsTrigger.AppStart => "app_start",
        WindowsTrigger.AppStop => "app_stop",
        WindowsTrigger.MicOn => "mic_on",
        WindowsTrigger.MicOff => "mic_off",
        WindowsTrigger.CamOn => "cam_on",
        WindowsTrigger.CamOff => "cam_off",
        WindowsTrigger.AudioStart => "audio_start",
        WindowsTrigger.AudioStop => "audio_stop",
        WindowsTrigger.Schedule => "schedule",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
    };

    private static readonly Dictionary<string, WindowsTrigger> ByKey =
        All.ToDictionary(ToKey, t => t, StringComparer.Ordinal);

    public static bool TryParse(string? key, out WindowsTrigger trigger)
    {
        if (key is not null && ByKey.TryGetValue(key, out trigger))
            return true;
        trigger = default;
        return false;
    }

    public static TriggerParamKind ParamKind(WindowsTrigger trigger) => trigger switch
    {
        WindowsTrigger.IdleStart or WindowsTrigger.IdleEnd => TriggerParamKind.Minutes,
        WindowsTrigger.AppStart or WindowsTrigger.AppStop => TriggerParamKind.ProcessName,
        WindowsTrigger.Schedule => TriggerParamKind.Schedule,
        _ => TriggerParamKind.None,
    };

    /// <summary>
    /// Triggers describing an ongoing state (usable for a "currently true" live indicator);
    /// pulse-like triggers (startup, resume, shutdown...) have no meaningful current value.
    /// </summary>
    public static bool IsStateLike(WindowsTrigger trigger) => trigger switch
    {
        WindowsTrigger.Lock or WindowsTrigger.Unlock
            or WindowsTrigger.DisplayOn or WindowsTrigger.DisplayOff
            or WindowsTrigger.IdleStart or WindowsTrigger.IdleEnd
            or WindowsTrigger.FullscreenStart or WindowsTrigger.FullscreenEnd
            or WindowsTrigger.AppStart or WindowsTrigger.AppStop
            or WindowsTrigger.MicOn or WindowsTrigger.MicOff
            or WindowsTrigger.CamOn or WindowsTrigger.CamOff
            or WindowsTrigger.AudioStart or WindowsTrigger.AudioStop => true,
        _ => false,
    };
}
