// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.Sockets;
using System.Security.Authentication;
using HaCompanion.Core.Rest;
using Xunit;

namespace HaCompanion.Core.Tests;

public class ConnectionCheckTests
{
    // ----- exception classification (the WHY behind a failed connect) -----

    [Fact]
    public void Tls_error_is_detected_anywhere_in_the_exception_chain()
    {
        var direct = new AuthenticationException("cert invalid");
        var nested = new HttpRequestException("outer", new AuthenticationException("cert invalid"));
        var deep = new HttpRequestException("outer",
            new IOException("mid", new AuthenticationException("cert invalid")));

        Assert.Equal(ConnectionCheckStatus.TlsError, HaRestClient.ClassifyException(direct));
        Assert.Equal(ConnectionCheckStatus.TlsError, HaRestClient.ClassifyException(nested));
        Assert.Equal(ConnectionCheckStatus.TlsError, HaRestClient.ClassifyException(deep));
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    [InlineData(SocketError.TryAgain)]
    public void Dns_failures_map_to_DnsError(SocketError error)
    {
        var ex = new HttpRequestException("outer", new SocketException((int)error));
        Assert.Equal(ConnectionCheckStatus.DnsError, HaRestClient.ClassifyException(ex));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.ConnectionReset)]
    public void Other_socket_failures_map_to_NetworkError(SocketError error)
    {
        var ex = new HttpRequestException("outer", new SocketException((int)error));
        Assert.Equal(ConnectionCheckStatus.NetworkError, HaRestClient.ClassifyException(ex));
    }

    [Fact]
    public void Unrecognized_exceptions_fall_back_to_NetworkError()
    {
        Assert.Equal(ConnectionCheckStatus.NetworkError,
            HaRestClient.ClassifyException(new HttpRequestException("plain")));
        Assert.Equal(ConnectionCheckStatus.NetworkError,
            HaRestClient.ClassifyException(new InvalidOperationException("odd")));
    }

    // ----- HTTP status mapping -----

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void Success_codes_map_to_Ok(int code)
    {
        var result = HaRestClient.FromStatusCode(code);
        Assert.True(result.IsOk);
        Assert.Equal(ConnectionCheckStatus.Ok, result.Status);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Auth_codes_map_to_AuthFailed_and_keep_the_code(int code)
    {
        var result = HaRestClient.FromStatusCode(code);
        Assert.Equal(ConnectionCheckStatus.AuthFailed, result.Status);
        Assert.Equal(code, result.HttpStatusCode);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(302)]
    public void Other_codes_map_to_HttpError_and_keep_the_code(int code)
    {
        var result = HaRestClient.FromStatusCode(code);
        Assert.Equal(ConnectionCheckStatus.HttpError, result.Status);
        Assert.Equal(code, result.HttpStatusCode);
    }

    // ----- status -> localization key map (consumed by the settings page) -----

    [Theory]
    [InlineData(ConnectionCheckStatus.Ok, "Set_MsgConnected")]
    [InlineData(ConnectionCheckStatus.AuthFailed, "Set_ErrAuth")]
    [InlineData(ConnectionCheckStatus.TlsError, "Set_ErrTls")]
    [InlineData(ConnectionCheckStatus.DnsError, "Set_ErrDns")]
    [InlineData(ConnectionCheckStatus.Timeout, "Set_ErrTimeout")]
    [InlineData(ConnectionCheckStatus.NetworkError, "Set_ErrNetwork")]
    [InlineData(ConnectionCheckStatus.HttpError, "Set_ErrHttp")]
    public void Every_status_has_a_localization_key(ConnectionCheckStatus status, string expectedKey)
    {
        Assert.Equal(expectedKey, new ConnectionCheckResult(status).I18nKey);
    }

    [Fact]
    public void Success_singleton_is_ok_with_no_http_code()
    {
        Assert.True(ConnectionCheckResult.Success.IsOk);
        Assert.Equal(0, ConnectionCheckResult.Success.HttpStatusCode);
    }
}
