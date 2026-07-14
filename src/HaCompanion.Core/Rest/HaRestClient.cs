// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HaCompanion.Core.MobileApp;
using HaCompanion.Core.Models;
using Microsoft.Extensions.Logging;

namespace HaCompanion.Core.Rest;

/// <summary>
/// Thin REST client for the Home Assistant HTTP API: validate the connection,
/// fetch entity states and call services.
/// </summary>
public sealed class HaRestClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // mobile_app rejects binary_sensor registrations whose optional fields are present as
    // null (silent 200 without registering) — omit nulls on every mobile_app payload.
    private static readonly JsonSerializerOptions JsonOptionsNoNulls = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<HaRestClient> _logger;
    private HttpClient? _http;
    private Uri? _baseUri;
    private string _token = string.Empty;

    public HaRestClient(ILogger<HaRestClient> logger) => _logger = logger;

    /// <summary>Configure the client for a specific instance. Safe to call again to reconfigure.</summary>
    public void Configure(Uri baseUri, string token, bool ignoreCertErrors = false)
    {
        var text = baseUri.ToString();
        if (!text.EndsWith('/'))
            text += "/";
        _baseUri = new Uri(text, UriKind.Absolute);
        _token = token;

        _http?.Dispose();
        var handler = new HttpClientHandler();
        if (ignoreCertErrors)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Returns true if the base URL + token reach a working Home Assistant API.</summary>
    public async Task<bool> ValidateAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Get, "api/");
            using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home Assistant validation request failed");
            return false;
        }
    }

    public async Task<IReadOnlyList<HaEntityState>> GetStatesAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "api/states");
        using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var states = await res.Content
            .ReadFromJsonAsync<List<HaEntityState>>(JsonOptions, ct)
            .ConfigureAwait(false);
        return states ?? [];
    }

    public async Task CallServiceAsync(string domain, string service, object? data = null, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"api/services/{domain}/{service}");
        req.Content = JsonContent.Create(data ?? new { }, options: JsonOptions);
        using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Register this machine as a mobile_app device. Null on failure (logged).</summary>
    public async Task<MobileAppRegistrationResult?> RegisterMobileAppAsync(
        MobileAppRegistrationRequest request, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Post, "api/mobile_app/registrations");
            req.Content = JsonContent.Create(request, options: JsonOptionsNoNulls);
            using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("mobile_app registration failed: HTTP {Status}", (int)res.StatusCode);
                return null;
            }
            return await res.Content
                .ReadFromJsonAsync<MobileAppRegistrationResult>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mobile_app registration failed");
            return null;
        }
    }

    /// <summary>
    /// POST api/webhook/{id} — mobile_app data channel. Deliberately WITHOUT the
    /// Authorization header (the webhook id is the credential). Never throws;
    /// HTTP 410 means the registration was deleted in HA (re-register).
    /// </summary>
    public async Task<WebhookPostResult> PostWebhookAsync(string webhookId, object payload, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Post, $"api/webhook/{webhookId}", authorize: false);
            req.Content = JsonContent.Create(payload, options: JsonOptionsNoNulls);
            using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // mobile_app always answers with a JSON body. An UNKNOWN webhook id (deleted
            // registration) is answered with an EMPTY 200 (anti-enumeration), not 410 —
            // treat both as "registration gone".
            var outcome = res.StatusCode == HttpStatusCode.Gone
                          || (res.IsSuccessStatusCode && string.IsNullOrWhiteSpace(body))
                ? WebhookOutcome.RegistrationGone
                : res.IsSuccessStatusCode
                    ? WebhookOutcome.Success
                    : WebhookOutcome.Failed;
            return new WebhookPostResult(outcome, (int)res.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook post failed");
            return new WebhookPostResult(WebhookOutcome.Failed, 0);
        }
    }

    private HttpClient Client =>
        _http ?? throw new InvalidOperationException("HaRestClient is not configured; call Configure() first.");

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativePath, bool authorize = true)
    {
        if (_baseUri is null)
            throw new InvalidOperationException("HaRestClient is not configured; call Configure() first.");

        var req = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        if (authorize)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public void Dispose() => _http?.Dispose();
}
