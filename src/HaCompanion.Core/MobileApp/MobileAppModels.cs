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

public enum WebhookOutcome
{
    Success,
    /// <summary>HTTP 410 — the mobile_app registration was deleted in HA; re-register.</summary>
    RegistrationGone,
    Failed,
}

public sealed record WebhookPostResult(WebhookOutcome Outcome, int StatusCode);
