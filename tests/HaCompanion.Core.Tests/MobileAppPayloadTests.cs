// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using HaCompanion.Core.MobileApp;
using Xunit;

namespace HaCompanion.Core.Tests;

/// <summary>
/// Pins the wire format of the mobile_app payloads: HA expects snake_case, while the
/// repo-wide serializer default (JsonSerializerDefaults.Web) is camelCase — every
/// property must carry an explicit JsonPropertyName.
/// </summary>
public class MobileAppPayloadTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Registration_request_serializes_snake_case()
    {
        var req = new MobileAppRegistrationRequest(
            "deadbeef", "hacompanion.windows", "HA Companion", "0.9.0",
            "ELIAS-PC", "Custom", "Desktop", "Windows", "11", false);
        var json = JsonSerializer.Serialize(req, Web);

        Assert.Contains("\"device_id\":\"deadbeef\"", json);
        Assert.Contains("\"app_id\":\"hacompanion.windows\"", json);
        Assert.Contains("\"app_name\"", json);
        Assert.Contains("\"app_version\"", json);
        Assert.Contains("\"device_name\":\"ELIAS-PC\"", json);
        Assert.Contains("\"os_name\"", json);
        Assert.Contains("\"os_version\"", json);
        Assert.Contains("\"supports_encryption\":false", json);
        Assert.DoesNotContain("deviceId", json);
    }

    [Fact]
    public void Registration_result_reads_webhook_id()
    {
        var result = JsonSerializer.Deserialize<MobileAppRegistrationResult>(
            "{\"cloudhook_url\":null,\"remote_ui_url\":null,\"secret\":null,\"webhook_id\":\"abc123\"}", Web);
        Assert.Equal("abc123", result!.WebhookId);
    }

    private static readonly JsonSerializerOptions WebNoNulls = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Register_sensor_envelope_has_type_and_data()
    {
        var sensor = new SensorDefinition("is_locked", "Gesperrt", "binary_sensor", true, Icon: "mdi:lock");
        var json = JsonSerializer.Serialize(new WebhookEnvelope("register_sensor", sensor), WebNoNulls);

        Assert.Contains("\"type\":\"register_sensor\"", json);
        Assert.Contains("\"data\":{", json);
        Assert.Contains("\"unique_id\":\"is_locked\"", json);
        Assert.Contains("\"state\":true", json); // binary state is a JSON bool, not a string
        Assert.Contains("\"icon\":\"mdi:lock\"", json);
        // HA silently drops binary_sensor registrations that carry null optionals —
        // the wire format must OMIT them (HaRestClient serializes with WhenWritingNull).
        Assert.DoesNotContain("device_class", json);
        Assert.DoesNotContain("unit_of_measurement", json);
        Assert.DoesNotContain("state_class", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Registration_with_app_data_serializes_the_push_flag()
    {
        var req = new MobileAppRegistrationRequest(
            "deadbeef", "hacompanion.windows", "HA Companion", "0.9.0",
            "ELIAS-PC", "Custom", "Desktop", "Windows", "11", false,
            MobileAppRegistrationRequest.WebsocketPushAppData);
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"app_data\":{\"push_websocket_channel\":true}", json);
    }

    [Fact]
    public void Update_registration_envelope_has_the_right_shape()
    {
        var req = new MobileAppRegistrationRequest(
            "deadbeef", "hacompanion.windows", "HA Companion", "0.9.0",
            "ELIAS-PC", "Custom", "Desktop", "Windows", "11", false,
            MobileAppRegistrationRequest.WebsocketPushAppData);
        var json = JsonSerializer.Serialize(new WebhookEnvelope("update_registration", req), Web);
        Assert.Contains("\"type\":\"update_registration\"", json);
        Assert.Contains("\"data\":{", json);
        Assert.Contains("\"push_websocket_channel\":true", json);
    }

    [Fact]
    public void Update_states_envelope_wraps_an_array()
    {
        var states = new[]
        {
            new SensorState("is_locked", "binary_sensor", false),
            new SensorState("idle_minutes", "sensor", 12),
        };
        var json = JsonSerializer.Serialize(new WebhookEnvelope("update_sensor_states", states), Web);

        Assert.Contains("\"type\":\"update_sensor_states\"", json);
        Assert.Contains("\"data\":[", json);
        Assert.Contains("\"state\":false", json);
        Assert.Contains("\"state\":12", json);
    }
}
