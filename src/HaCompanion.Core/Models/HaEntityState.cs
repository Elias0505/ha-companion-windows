// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaCompanion.Core.Models;

/// <summary>
/// A single Home Assistant entity state, as returned by <c>/api/states</c>
/// and inside <c>state_changed</c> events.
/// </summary>
public sealed class HaEntityState
{
    [JsonPropertyName("entity_id")]
    public string EntityId { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("attributes")]
    public Dictionary<string, JsonElement> Attributes { get; init; } = new();

    [JsonPropertyName("last_changed")]
    public DateTimeOffset? LastChanged { get; init; }

    [JsonPropertyName("last_updated")]
    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>The entity domain, e.g. <c>light</c> for <c>light.kitchen</c>.</summary>
    [JsonIgnore]
    public string Domain
    {
        get
        {
            var dot = EntityId.IndexOf('.');
            return dot < 0 ? EntityId : EntityId[..dot];
        }
    }

    /// <summary>The <c>friendly_name</c> attribute, falling back to the entity id.</summary>
    [JsonIgnore]
    public string FriendlyName =>
        Attributes.TryGetValue("friendly_name", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? EntityId
            : EntityId;

    /// <summary>
    /// True for the common "active" states (on / open / home / unlocked). "unlocked" must be
    /// active: locks report locked/unlocked, and without it a lock always read as inactive —
    /// so toggling resolved to "unlock" every time and a lock could never be LOCKED again.
    /// </summary>
    [JsonIgnore]
    public bool IsOn =>
        State.Equals("on", StringComparison.OrdinalIgnoreCase)
        || State.Equals("open", StringComparison.OrdinalIgnoreCase)
        || State.Equals("home", StringComparison.OrdinalIgnoreCase)
        || State.Equals("unlocked", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Home Assistant reports the entity as unavailable/unknown.</summary>
    [JsonIgnore]
    public bool IsUnavailable =>
        State.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
        || State.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    public string? GetAttributeString(string key) =>
        Attributes.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
