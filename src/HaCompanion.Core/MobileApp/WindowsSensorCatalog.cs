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
    DateTimeOffset LastStart);

/// <summary>
/// The fixed set of PC sensors pushed to HA. unique_ids are stable English identifiers
/// (they form the entity_ids); display names come localized from the caller.
/// </summary>
public static class WindowsSensorCatalog
{
    public const string Sensor = "sensor";
    public const string BinarySensor = "binary_sensor";

    /// <summary>All sensors with their current state (audio omitted when unavailable).</summary>
    public static IReadOnlyList<SensorDefinition> BuildDefinitions(
        IReadOnlyDictionary<string, string> names, WindowsSensorValues v)
    {
        string Name(string uniqueId) => names.TryGetValue(uniqueId, out var n) ? n : uniqueId;

        var list = new List<SensorDefinition>
        {
            new("is_locked", Name("is_locked"), BinarySensor, v.IsLocked, Icon: "mdi:lock"),
            new("session_state", Name("session_state"), Sensor, v.SessionState, Icon: "mdi:account"),
            new("is_idle", Name("is_idle"), BinarySensor, v.IsIdle, Icon: "mdi:sleep"),
            new("idle_minutes", Name("idle_minutes"), Sensor, v.IdleMinutes, Icon: "mdi:timer-sand",
                UnitOfMeasurement: "min", StateClass: "measurement"),
            new("active_program", Name("active_program"), Sensor, v.ForegroundProcess ?? "", Icon: "mdi:application"),
            new("fullscreen", Name("fullscreen"), BinarySensor, v.IsFullscreen, Icon: "mdi:fullscreen"),
            new("microphone_in_use", Name("microphone_in_use"), BinarySensor, v.MicInUse, Icon: "mdi:microphone"),
            new("camera_in_use", Name("camera_in_use"), BinarySensor, v.CamInUse, Icon: "mdi:webcam"),
            new("display_on", Name("display_on"), BinarySensor, v.DisplayOn, Icon: "mdi:monitor"),
            new("last_start", Name("last_start"), Sensor, v.LastStart.ToString("o"), Icon: "mdi:clock-start",
                DeviceClass: "timestamp"),
        };
        if (v.AudioPlaying is not null)
            list.Insert(9, new SensorDefinition(
                "audio_playing", Name("audio_playing"), BinarySensor, v.AudioPlaying, Icon: "mdi:volume-high"));
        return list;
    }

    public static IReadOnlyList<SensorState> BuildStates(WindowsSensorValues v) =>
        BuildDefinitions(new Dictionary<string, string>(), v)
            .Select(d => new SensorState(d.UniqueId, d.Type, d.State))
            .ToList();
}
