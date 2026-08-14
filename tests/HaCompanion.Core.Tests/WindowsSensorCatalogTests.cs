// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

public class WindowsSensorCatalogTests
{
    private static WindowsSensorValues Values(bool? audio = true) => new(
        IsLocked: true, SessionState: "locked", IsIdle: false, IdleMinutes: 3,
        ForegroundProcess: "discord", IsFullscreen: false, MicInUse: true, CamInUse: false,
        DisplayOn: true, AudioPlaying: audio,
        LastStart: new DateTimeOffset(2026, 7, 14, 19, 0, 0, TimeSpan.FromHours(2)));

    [Fact]
    public void Eleven_sensors_with_stable_unique_ids()
    {
        var defs = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values());
        Assert.Equal(11, defs.Count);
        Assert.Equal(new[]
        {
            "is_locked", "session_state", "is_idle", "idle_minutes", "active_program",
            "fullscreen", "microphone_in_use", "camera_in_use", "display_on",
            "audio_playing", "last_start",
        }, defs.Select(d => d.UniqueId));
    }

    [Fact]
    public void Audio_sensor_is_omitted_when_the_probe_is_unavailable()
    {
        var defs = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values(audio: null));
        Assert.Equal(10, defs.Count);
        Assert.DoesNotContain(defs, d => d.UniqueId == "audio_playing");
    }

    [Fact]
    public void States_map_the_values()
    {
        var states = WindowsSensorCatalog.BuildStates(Values());
        object? Of(string id) => states.Single(s => s.UniqueId == id).State;

        Assert.Equal(true, Of("is_locked"));
        Assert.Equal("locked", Of("session_state"));
        Assert.Equal(3, Of("idle_minutes"));
        Assert.Equal("discord", Of("active_program"));
        Assert.Equal(true, Of("microphone_in_use"));
        Assert.Equal(false, Of("camera_in_use"));
        Assert.Equal("2026-07-14T19:00:00.0000000+02:00", Of("last_start")); // ISO 8601
    }

    [Fact]
    public void Own_app_in_front_reports_hacompanion_instead_of_blank()
    {
        // The monitor keeps the companion out of the AppStart/AppStop trigger stream, which
        // used to leave the sensor state EMPTY whenever the app itself had focus — looking
        // broken in HA (#10). The flag maps to a process-style lowercase name like every
        // other value.
        var values = Values() with { ForegroundProcess = null, IsOwnAppForeground = true };
        var state = WindowsSensorCatalog.BuildStates(values).Single(s => s.UniqueId == "active_program").State;
        Assert.Equal("hacompanion", state);
    }

    [Fact]
    public void No_foreground_window_still_reports_an_empty_state()
    {
        // Locked screen / secure desktop: genuinely nothing in front stays "", so HA can
        // distinguish "companion focused" from "no window at all".
        var values = Values() with { ForegroundProcess = null };
        var state = WindowsSensorCatalog.BuildStates(values).Single(s => s.UniqueId == "active_program").State;
        Assert.Equal("", state);
    }

    [Fact]
    public void Localized_names_are_applied_and_missing_ones_fall_back_to_the_id()
    {
        var names = new Dictionary<string, string> { ["is_locked"] = "Gesperrt" };
        var defs = WindowsSensorCatalog.BuildDefinitions(names, Values());
        Assert.Equal("Gesperrt", defs.Single(d => d.UniqueId == "is_locked").Name);
        Assert.Equal("session_state", defs.Single(d => d.UniqueId == "session_state").Name);
    }

    [Fact]
    public void Types_and_metadata_are_correct()
    {
        var defs = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values());
        Assert.Equal("binary_sensor", defs.Single(d => d.UniqueId == "is_locked").Type);
        Assert.Equal("sensor", defs.Single(d => d.UniqueId == "idle_minutes").Type);
        Assert.Equal("min", defs.Single(d => d.UniqueId == "idle_minutes").UnitOfMeasurement);
        Assert.Equal("measurement", defs.Single(d => d.UniqueId == "idle_minutes").StateClass);
        Assert.Equal("timestamp", defs.Single(d => d.UniqueId == "last_start").DeviceClass);
    }

    [Theory]
    [InlineData("idle_minutes", "duration", "diagnostic")]
    [InlineData("audio_playing", "sound", null)]
    [InlineData("last_start", "timestamp", "diagnostic")]
    [InlineData("session_state", null, "diagnostic")]
    [InlineData("active_program", null, null)]
    public void Device_classes_and_entity_categories(string id, string? deviceClass, string? category)
    {
        var defs = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values());
        var def = defs.Single(d => d.UniqueId == id);
        Assert.Equal(deviceClass, def.DeviceClass);
        Assert.Equal(category, def.EntityCategory);
    }

    [Fact]
    public void Is_locked_deliberately_has_no_device_class()
    {
        // HA's "lock" binary class means on = UNLOCKED — adopting it would invert the UI
        // meaning; inverting our state instead would break existing user automations.
        var defs = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values());
        Assert.Null(defs.Single(d => d.UniqueId == "is_locked").DeviceClass);
    }

    [Fact]
    public void Disabled_flag_is_always_explicit_on_every_definition()
    {
        // Omitting the flag would leave previously disabled entities disabled forever —
        // HA only changes disabled_by when the field is present in the registration.
        var enabled = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values());
        Assert.All(enabled, d => Assert.False(d.Disabled));

        var disabled = WindowsSensorCatalog.BuildDefinitions(new Dictionary<string, string>(), Values(), disabled: true);
        Assert.All(disabled, d => Assert.True(d.Disabled));
    }
}
