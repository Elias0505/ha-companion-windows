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

    // Matches the WebSocket receive cap — /api/states is the same payload class either way.
    private const int MaxResponseBytes = 32 * 1024 * 1024;

    private readonly ILogger<HaRestClient> _logger;
    // One immutable object holds client+base+token together: Configure swaps the whole thing in
    // a single reference assignment, so an in-flight request can never see the new base URL with
    // the old token (or vice versa), and never has its HttpClient disposed underneath it.
    private volatile Session? _session;

    /// <param name="Http">For the authenticated API. Follows redirects: instances behind a
    /// proxy that upgrades http→https rely on it, and .NET strips the Authorization header on a
    /// cross-host redirect, so the token cannot travel.</param>
    /// <param name="WebhookHttp">For webhook posts only. Does NOT follow redirects: those posts
    /// carry no Authorization header because the webhook id in the URL PATH *is* the credential,
    /// and a redirect would hand that secret to whatever host the response names.</param>
    private sealed record Session(HttpClient Http, HttpClient WebhookHttp, Uri BaseUri, string Token)
    {
        // The generated record ToString would print the bearer token in full — a latent leak
        // the moment anyone logs or interpolates a session.
        public override string ToString() => $"Session({BaseUri})";
    }

    public HaRestClient(ILogger<HaRestClient> logger) => _logger = logger;

    /// <summary>Configure the client for a specific instance. Safe to call again to reconfigure.</summary>
    public void Configure(Uri baseUri, string token, bool ignoreCertErrors = false)
    {
        var text = baseUri.ToString();
        if (!text.EndsWith('/'))
            text += "/";
        var normalized = new Uri(text, UriKind.Absolute);

        // Publish the new session as one reference assignment; never dispose the outgoing client
        // here, because another thread (the sensor heartbeat) may be inside a request on it.
        _session = new Session(
            MakeClient(normalized, ignoreCertErrors, followRedirects: true),
            MakeClient(normalized, ignoreCertErrors, followRedirects: false),
            normalized,
            token);
    }

    private static HttpClient MakeClient(Uri baseUri, bool ignoreCertErrors, bool followRedirects)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = followRedirects };
        if (ignoreCertErrors)
        {
            // Scoped to the configured host only — a blanket validator would also skip
            // validation for every other host this client ever reaches (including a redirect
            // target). Everything else keeps normal checking while the opt-in is active.
            var trustedHost = baseUri.IdnHost;
            handler.ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || string.Equals(request.RequestUri?.IdnHost, trustedHost, StringComparison.OrdinalIgnoreCase);
        }
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>Returns true if the base URL + token reach a working Home Assistant API.</summary>
    public async Task<bool> ValidateAsync(CancellationToken ct = default) =>
        (await CheckAsync(ct).ConfigureAwait(false)).IsOk;

    /// <summary>
    /// Like <see cref="ValidateAsync"/>, but classifies WHY it failed (auth vs. TLS vs.
    /// DNS vs. timeout ...) so the UI can tell the user what to fix.
    /// </summary>
    public async Task<ConnectionCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Get, "api/");
            using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
            return FromStatusCode((int)res.StatusCode);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ConnectionCheckResult(ConnectionCheckStatus.Timeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home Assistant connection check failed");
            return new ConnectionCheckResult(ClassifyException(ex));
        }
    }

    internal static ConnectionCheckResult FromStatusCode(int statusCode) =>
        statusCode is >= 200 and < 300 ? ConnectionCheckResult.Success
        : statusCode is 401 or 403 ? new ConnectionCheckResult(ConnectionCheckStatus.AuthFailed, statusCode)
        : new ConnectionCheckResult(ConnectionCheckStatus.HttpError, statusCode);

    /// <summary>
    /// Walk the exception chain to the user-fixable cause. HttpClient wraps the interesting
    /// exceptions (AuthenticationException for TLS, SocketException for DNS/reachability)
    /// inside HttpRequestException — sometimes more than one level deep.
    /// </summary>
    internal static ConnectionCheckStatus ClassifyException(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            switch (e)
            {
                case System.Security.Authentication.AuthenticationException:
                    return ConnectionCheckStatus.TlsError;
                case System.Net.Sockets.SocketException s
                    when s.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                        or System.Net.Sockets.SocketError.NoData
                        or System.Net.Sockets.SocketError.TryAgain:
                    return ConnectionCheckStatus.DnsError;
                case System.Net.Sockets.SocketException:
                    return ConnectionCheckStatus.NetworkError;
            }
        }
        return ConnectionCheckStatus.NetworkError;
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
        using var req = BuildRequest(HttpMethod.Post, $"api/services/{Seg(domain)}/{Seg(service)}");
        req.Content = JsonContent.Create(data ?? new { }, options: JsonOptions);
        using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>Fire a custom event on the HA event bus (notification action callbacks).</summary>
    public async Task<bool> FireEventAsync(string eventType, object? data = null, CancellationToken ct = default)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Post, $"api/events/{Seg(eventType)}");
            req.Content = JsonContent.Create(data ?? new { }, options: JsonOptionsNoNulls);
            using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                _logger.LogWarning("Fire event {Event} failed: HTTP {Status}", eventType, (int)res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fire event {Event} failed", eventType);
            return false;
        }
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
            // ONE session snapshot for both the URI and the client — reading Current twice could
            // pair session A's URI with session B's handler across a concurrent Configure.
            var session = Current;
            using var req = new HttpRequestMessage(HttpMethod.Post,
                new Uri(session.BaseUri, $"api/webhook/{Seg(webhookId)}"));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = JsonContent.Create(payload, options: JsonOptionsNoNulls);
            using var res = await session.WebhookHttp.SendAsync(req, ct).ConfigureAwait(false);
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // This client does not follow redirects (the webhook id in the path is the
            // credential). If the instance sits behind a redirecting proxy, sensor posts land
            // here as 3xx while api/ (redirects followed) looks healthy — name the cause.
            if ((int)res.StatusCode is >= 300 and < 400)
                _logger.LogWarning(
                    "Webhook post was redirected (HTTP {Status} → {Location}); webhook posts never follow " +
                    "redirects, configure the final URL directly", (int)res.StatusCode,
                    res.Headers.Location?.Host ?? "?");
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

    private Session Current =>
        _session ?? throw new InvalidOperationException("HaRestClient is not configured; call Configure() first.");

    private HttpClient Client => Current.Http;

    /// <summary>
    /// Build a request against the CURRENT session, so base URL and token always belong
    /// together even if Configure runs concurrently.
    /// </summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string relativePath, bool authorize = true)
    {
        var session = Current;
        var req = new HttpRequestMessage(method, new Uri(session.BaseUri, relativePath));
        if (authorize)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    /// <summary>
    /// Escape a value that goes into a URL PATH. Without this a value containing "../", "?" or
    /// "#" re-targets the request: <c>new Uri(base, "api/webhook/../x")</c> normalizes away the
    /// segment, so an HA-supplied webhook id could point the unauthenticated POST elsewhere.
    /// </summary>
    private static string Seg(string value) => Uri.EscapeDataString(value);

    public void Dispose()
    {
        var session = _session;
        session?.Http.Dispose();
        session?.WebhookHttp.Dispose();
    }
}
