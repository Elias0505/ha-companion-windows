// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json.Serialization;

namespace HaCompanion.Core.MobileApp;

// The mobile_app integration speaks snake_case; the repo-wide JsonSerializerDefaults.Web
// would produce camelCase — every payload property carries an explicit [JsonPropertyName]
// (pinned by MobileAppPayloadTests).

/// <summary>POST api/mobile_app/registrations — registers this PC as a device in HA.
/// app_data {"push_websocket_channel": true} makes HA create notify.mobile_app_&lt;device&gt;
/// and deliver its calls over the websocket push channel.</summary>
public sealed record MobileAppRegistrationRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("app_id")] string AppId,
    [property: JsonPropertyName("app_name")] string AppName,
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("os_name")] string OsName,
    [property: JsonPropertyName("os_version")] string OsVersion,
    [property: JsonPropertyName("supports_encryption")] bool SupportsEncryption,
    [property: JsonPropertyName("app_data")] IReadOnlyDictionary<string, object>? AppData = null)
{
    public static IReadOnlyDictionary<string, object> WebsocketPushAppData { get; } =
        new Dictionary<string, object> { ["push_websocket_channel"] = true };
}

/// <summary>Payload of the <c>update_registration</c> webhook. HA's UPDATE schema only
/// permits these six keys — identity fields from the initial registration (app_id,
/// app_name, device_id, os_name, supports_encryption) are rejected with "extra keys
/// not allowed" and, because HA answers schema errors with an empty 200, the update
/// silently never applies. Deliberately a separate record from
/// <see cref="MobileAppRegistrationRequest"/> so the two schemas cannot drift together.</summary>
public sealed record MobileAppRegistrationUpdate(
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("os_version")] string OsVersion,
    [property: JsonPropertyName("app_data")] IReadOnlyDictionary<string, object>? AppData = null)
{
    /// <summary>The update view of a full registration request — same values, legal keys only.</summary>
    public static MobileAppRegistrationUpdate FromRegistration(MobileAppRegistrationRequest request) =>
        new(request.AppVersion, request.DeviceName, request.Manufacturer, request.Model,
            request.OsVersion, request.AppData);
}

public sealed record MobileAppRegistrationResult(
    [property: JsonPropertyName("webhook_id")] string WebhookId);

/// <summary>One sensor as registered via the webhook ("register_sensor").
/// Nulls are omitted from the payload (JsonOptionsNoNulls), so the optional metadata
/// keeps existing registrations byte-identical unless explicitly set.</summary>
public sealed record SensorDefinition(
    [property: JsonPropertyName("unique_id")] string UniqueId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type, // "sensor" | "binary_sensor"
    [property: JsonPropertyName("state")] object? State,
    [property: JsonPropertyName("icon")] string? Icon = null,
    [property: JsonPropertyName("device_class")] string? DeviceClass = null,
    [property: JsonPropertyName("unit_of_measurement")] string? UnitOfMeasurement = null,
    [property: JsonPropertyName("state_class")] string? StateClass = null,
    [property: JsonPropertyName("entity_category")] string? EntityCategory = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null);

/// <summary>One state update as sent via "update_sensor_states".</summary>
public sealed record SensorState(
    [property: JsonPropertyName("unique_id")] string UniqueId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("state")] object? State);

/// <summary>Envelope for every webhook message: {"type": ..., "data": ...}.</summary>
public sealed record WebhookEnvelope(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] object Data);

/// <summary>
/// Payload of an <c>update_location</c> webhook message (#11). Only <c>location_name</c> is
/// used — "home"/"not_home" map straight to the device_tracker state; this app never sends
/// GPS coordinates.
/// </summary>
public sealed record LocationUpdate(
    [property: JsonPropertyName("location_name")] string LocationName);

public enum WebhookOutcome
{
    Success,
    /// <summary>HTTP 410 — the mobile_app registration was deleted in HA; re-register.</summary>
    RegistrationGone,
    Failed,
}

public sealed record WebhookPostResult(WebhookOutcome Outcome, int StatusCode);
