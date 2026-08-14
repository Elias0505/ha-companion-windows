// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.MobileApp;

/// <summary>Platform-neutral bag of the PC state values the sensors publish.</summary>
public sealed record WindowsSensorValues(
    bool IsLocked,
    string SessionState,
    bool IsIdle,
    int IdleMinutes,
    string? ForegroundProcess,
    bool IsFullscreen,
    bool MicInUse,
    bool CamInUse,
    bool DisplayOn,
    bool? AudioPlaying,      // null = probe unavailable -> sensor omitted entirely
    DateTimeOffset LastStart,
    // The companion's own window is in front. Kept separate from ForegroundProcess so the
    // automation triggers stay silent about the app itself while the active_program sensor
    // can still say "hacompanion" instead of going blank (#10).
    bool IsOwnAppForeground = false);

/// <summary>
/// The fixed set of PC sensors pushed to HA. unique_ids are stable English identifiers
/// (they form the entity_ids); display names come localized from the caller.
/// </summary>
public static class WindowsSensorCatalog
{
    public const string Sensor = "sensor";
    public const string BinarySensor = "binary_sensor";

    /// <summary>Registry-metadata category for supporting/diagnostic sensors.</summary>
    private const string Diagnostic = "diagnostic";

    /// <summary>
    /// All sensors with their current state (audio omitted when unavailable).
    /// <paramref name="disabled"/> re-registers every sensor as disabled — used when the
    /// user turns reporting off, so HA hides the entities instead of leaving them stale.
    /// The flag is ALWAYS sent explicitly: omitting it would leave a previously disabled
    /// entity disabled forever (HA only changes disabled_by when the field is present),
    /// while an explicit false only lifts integration-disabled — a user-disabled entity
    /// in HA stays respected.
    /// </summary>
    public static IReadOnlyList<SensorDefinition> BuildDefinitions(
        IReadOnlyDictionary<string, string> names, WindowsSensorValues v, bool disabled = false)
    {
        string Name(string uniqueId) => names.TryGetValue(uniqueId, out var n) ? n : uniqueId;
        bool? dis = disabled;

        var list = new List<SensorDefinition>
        {
            // is_locked deliberately has NO device_class: HA's "lock" binary class means
            // on = UNLOCKED — adopting it would invert the meaning in the HA UI, and
            // inverting our state instead would break every existing user automation.
            new("is_locked", Name("is_locked"), BinarySensor, v.IsLocked, Icon: "mdi:lock", Disabled: dis),
            new("session_state", Name("session_state"), Sensor, v.SessionState, Icon: "mdi:account",
                EntityCategory: Diagnostic, Disabled: dis),
            new("is_idle", Name("is_idle"), BinarySensor, v.IsIdle, Icon: "mdi:sleep", Disabled: dis),
            new("idle_minutes", Name("idle_minutes"), Sensor, v.IdleMinutes, Icon: "mdi:timer-sand",
                DeviceClass: "duration", UnitOfMeasurement: "min", StateClass: "measurement",
                EntityCategory: Diagnostic, Disabled: dis),
            // Own app in front reports its process-style name like every other program would —
            // a blank state looked broken (#10). Lowercase on purpose: sensor STATES stay
            // language-independent (unlike the localized display names).
            new("active_program", Name("active_program"), Sensor,
                v.IsOwnAppForeground ? "hacompanion" : v.ForegroundProcess ?? "", Icon: "mdi:application",
                Disabled: dis),
            new("fullscreen", Name("fullscreen"), BinarySensor, v.IsFullscreen, Icon: "mdi:fullscreen", Disabled: dis),
            new("microphone_in_use", Name("microphone_in_use"), BinarySensor, v.MicInUse, Icon: "mdi:microphone",
                Disabled: dis),
            new("camera_in_use", Name("camera_in_use"), BinarySensor, v.CamInUse, Icon: "mdi:webcam", Disabled: dis),
            new("display_on", Name("display_on"), BinarySensor, v.DisplayOn, Icon: "mdi:monitor", Disabled: dis),
            new("last_start", Name("last_start"), Sensor, v.LastStart.ToString("o"), Icon: "mdi:clock-start",
                DeviceClass: "timestamp", EntityCategory: Diagnostic, Disabled: dis),
        };
        if (v.AudioPlaying is not null)
            list.Insert(9, new SensorDefinition(
                "audio_playing", Name("audio_playing"), BinarySensor, v.AudioPlaying, Icon: "mdi:volume-high",
                DeviceClass: "sound", Disabled: dis));
        return list;
    }

    public static IReadOnlyList<SensorState> BuildStates(WindowsSensorValues v) =>
        BuildDefinitions(new Dictionary<string, string>(), v)
            .Select(d => new SensorState(d.UniqueId, d.Type, d.State))
            .ToList();
}
