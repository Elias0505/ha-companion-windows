// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    private HttpClient Client =>
        _http ?? throw new InvalidOperationException("HaRestClient is not configured; call Configure() first.");

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativePath)
    {
        if (_baseUri is null)
            throw new InvalidOperationException("HaRestClient is not configured; call Configure() first.");

        var req = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public void Dispose() => _http?.Dispose();
}
