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
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(Token);

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
