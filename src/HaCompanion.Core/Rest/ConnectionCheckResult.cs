// SPDX-License-Identifier: AGPL-3.0-only
namespace HaCompanion.Core.Rest;

/// <summary>Why a connection check succeeded or failed — one bucket per user-fixable cause.</summary>
public enum ConnectionCheckStatus
{
    Ok,
    /// <summary>The server rejected the token (HTTP 401/403).</summary>
    AuthFailed,
    /// <summary>TLS handshake failed (typically a self-signed certificate).</summary>
    TlsError,
    /// <summary>The host name could not be resolved.</summary>
    DnsError,
    /// <summary>The server did not answer within the HTTP timeout.</summary>
    Timeout,
    /// <summary>Reachability problem other than DNS (refused, unreachable, reset, ...).</summary>
    NetworkError,
    /// <summary>The server answered, but with an unexpected HTTP status.</summary>
    HttpError,
}

/// <summary>Outcome of validating a base URL + token against a Home Assistant server.</summary>
public sealed record ConnectionCheckResult(ConnectionCheckStatus Status, int HttpStatusCode = 0)
{
    public static ConnectionCheckResult Success { get; } = new(ConnectionCheckStatus.Ok);

    public bool IsOk => Status == ConnectionCheckStatus.Ok;

    /// <summary>
    /// The app-side localization key for this outcome. Lives here (not in the UI) so the
    /// status→message mapping is covered by the platform-independent test suite.
    /// The <see cref="ConnectionCheckStatus.HttpError"/> text takes the status code as {0}.
    /// </summary>
    public string I18nKey => Status switch
    {
        ConnectionCheckStatus.Ok => "Set_MsgConnected",
        ConnectionCheckStatus.AuthFailed => "Set_ErrAuth",
        ConnectionCheckStatus.TlsError => "Set_ErrTls",
        ConnectionCheckStatus.DnsError => "Set_ErrDns",
        ConnectionCheckStatus.Timeout => "Set_ErrTimeout",
        ConnectionCheckStatus.NetworkError => "Set_ErrNetwork",
        ConnectionCheckStatus.HttpError => "Set_ErrHttp",
        _ => "Set_MsgFailed",
    };
}
