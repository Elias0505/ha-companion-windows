// SPDX-License-Identifier: AGPL-3.0-only
using HaCompanion.Core.Configuration;
using Xunit;

namespace HaCompanion.Core.Tests;

public class HaConnectionSettingsTests
{
    [Theory]
    [InlineData("https://ha.example.com:8123", "token", true)]
    [InlineData("http://192.168.1.5:8123", "token", true)]
    [InlineData("ftp://x", "token", false)]
    [InlineData("not a url", "token", false)]
    [InlineData("https://ha.example.com", "", false)]
    public void IsValid_requires_http_url_and_token(string url, string token, bool expected) =>
        Assert.Equal(expected, new HaConnectionSettings { BaseUrl = url, Token = token }.IsValid);

    [Theory]
    [InlineData("https://ha.example.com:8123", "wss://ha.example.com:8123/api/websocket")]
    [InlineData("http://192.168.1.5:8123", "ws://192.168.1.5:8123/api/websocket")]
    [InlineData("https://ha.example.com:8123/", "wss://ha.example.com:8123/api/websocket")]
    public void WebSocketUri_derives_scheme_and_path(string baseUrl, string expected) =>
        Assert.Equal(expected, new HaConnectionSettings { BaseUrl = baseUrl, Token = "t" }.WebSocketUri.ToString());
}
