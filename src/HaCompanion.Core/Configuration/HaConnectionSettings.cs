// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Configuration;

/// <summary>
/// Connection parameters for a Home Assistant instance. <see cref="Token"/> is a
/// Home Assistant "Long-Lived Access Token".
/// </summary>
public sealed class HaConnectionSettings
{
    public string BaseUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Accept self-signed / untrusted TLS certificates. Useful for local instances
    /// behind a self-signed reverse proxy. Off by default.
    /// </summary>
    public bool IgnoreCertificateErrors { get; set; }

    public bool IsValid =>
        IsUsableBaseUrl(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

    /// <summary>
    /// True when the string is an absolute http(s) URL. The UI must check this BEFORE probing:
    /// a bare "homeassistant.local:8123" otherwise reaches <c>BaseUri</c> and throws.
    /// </summary>
    public static bool IsUsableBaseUrl(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// True when both URLs address the same origin (scheme + host + port).
    ///
    /// The stored token is a credential FOR ONE HOST and must never travel to a different
    /// origin: the connect path REFUSES to proceed while the token field still holds the old
    /// host's secret — otherwise the very first probe would hand it to the new host, which is
    /// exactly how a spoofed mDNS responder on the LAN would harvest it. Only once a different
    /// token is entered (and the probe succeeds) are the old host's webhook and device ids
    /// dropped. An unparseable URL is never "the same origin" as anything (fail closed).
    /// </summary>
    public static bool IsSameOrigin(string? a, string? b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) || !Uri.TryCreate(b, UriKind.Absolute, out var ub))
            return false;
        return string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
            && ua.Port == ub.Port;
    }

    /// <summary>
    /// True when moving from <paramref name="from"/> to <paramref name="to"/> is purely an
    /// http→https upgrade of the SAME host (same explicit port, or both on their scheme's
    /// default). Strictly more secure than before, so the stored token may travel with it —
    /// forcing a re-auth here would punish exactly the user doing the right thing. The reverse
    /// (https→http) is a downgrade and deliberately not covered.
    /// </summary>
    public static bool IsSchemeUpgrade(string? from, string? to)
    {
        if (!Uri.TryCreate(from, UriKind.Absolute, out var ua) || !Uri.TryCreate(to, UriKind.Absolute, out var ub))
            return false;
        return string.Equals(ua.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ub.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
            && (ua.Port == ub.Port || (ua.IsDefaultPort && ub.IsDefaultPort));
    }

    public Uri BaseUri => new(BaseUrl, UriKind.Absolute);

    /// <summary>The Home Assistant WebSocket API endpoint derived from <see cref="BaseUrl"/>.</summary>
    public Uri WebSocketUri
    {
        get
        {
            var builder = new UriBuilder(BaseUri)
            {
                Scheme = string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? "wss"
                    : "ws",
            };
            builder.Path = builder.Path.TrimEnd('/') + "/api/websocket";
            return builder.Uri;
        }
    }
}
