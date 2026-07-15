// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Rest;

namespace HaCompanion.Core.MobileApp;

/// <summary>Typed wrapper around the mobile_app registration + webhook endpoints.</summary>
public interface IMobileAppClient
{
    Task<MobileAppRegistrationResult?> RegisterAsync(MobileAppRegistrationRequest request, CancellationToken ct = default);

    /// <summary>register_sensor is one webhook POST per sensor; stops early on 410.</summary>
    Task<WebhookPostResult> RegisterSensorsAsync(string webhookId, IEnumerable<SensorDefinition> sensors, CancellationToken ct = default);

    /// <summary>Upgrade an existing registration in place (e.g. add app_data for websocket push).</summary>
    Task<WebhookPostResult> UpdateRegistrationAsync(string webhookId, MobileAppRegistrationRequest request, CancellationToken ct = default);

    Task<WebhookPostResult> UpdateStatesAsync(string webhookId, IEnumerable<SensorState> states, CancellationToken ct = default);
}

/// <inheritdoc cref="IMobileAppClient"/>
public sealed class MobileAppClient : IMobileAppClient
{
    private readonly HaRestClient _rest;

    public MobileAppClient(HaRestClient rest) => _rest = rest;

    public Task<MobileAppRegistrationResult?> RegisterAsync(MobileAppRegistrationRequest request, CancellationToken ct = default) =>
        _rest.RegisterMobileAppAsync(request, ct);

    public async Task<WebhookPostResult> RegisterSensorsAsync(string webhookId, IEnumerable<SensorDefinition> sensors, CancellationToken ct = default)
    {
        var result = new WebhookPostResult(WebhookOutcome.Success, 200);
        foreach (var sensor in sensors)
        {
            result = await _rest.PostWebhookAsync(webhookId, new WebhookEnvelope("register_sensor", sensor), ct)
                .ConfigureAwait(false);
            if (result.Outcome == WebhookOutcome.RegistrationGone)
                return result; // the registration is dead — no point registering the rest
        }
        return result;
    }

    public Task<WebhookPostResult> UpdateStatesAsync(string webhookId, IEnumerable<SensorState> states, CancellationToken ct = default) =>
        _rest.PostWebhookAsync(webhookId, new WebhookEnvelope("update_sensor_states", states.ToList()), ct);

    public Task<WebhookPostResult> UpdateRegistrationAsync(string webhookId, MobileAppRegistrationRequest request, CancellationToken ct = default) =>
        _rest.PostWebhookAsync(webhookId, new WebhookEnvelope("update_registration", request), ct);
}
