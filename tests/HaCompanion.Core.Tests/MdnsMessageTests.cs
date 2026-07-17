// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using HaCompanion.Core.Discovery;
using Xunit;

namespace HaCompanion.Core.Tests;

public class MdnsMessageTests
{
    private const string Service = "_home-assistant._tcp.local";

    [Fact]
    public void Query_encodes_the_service_as_a_ptr_question_with_the_qu_bit()
    {
        var q = MdnsMessage.BuildQuery(Service);

        // header: id 0, QDCOUNT 1
        Assert.Equal(0, q[0]);
        Assert.Equal(0, q[1]);
        Assert.Equal(1, q[5]);

        // the labels appear length-prefixed
        Assert.Contains("_home-assistant", Encoding.ASCII.GetString(q));

        // trailer: root(0) + QTYPE PTR(12) + QCLASS IN with QU bit (0x8001)
        Assert.Equal(12, q[^3]);
        Assert.Equal(0x80, q[^2]);
        Assert.Equal(0x01, q[^1]);
    }

    [Fact]
    public void Malformed_datagram_yields_no_results_without_throwing()
    {
        Assert.Empty(MdnsMessage.ParseResponse([1, 2, 3], Service));
        Assert.Empty(MdnsMessage.ParseResponse([], Service));
    }

    [Fact]
    public void Parses_ptr_srv_txt_a_into_a_discovered_instance()
    {
        var datagram = BuildResponse();
        var results = MdnsMessage.ParseResponse(datagram, Service);

        var hit = Assert.Single(results);
        Assert.Equal("Home", hit.Name); // trailing "._home-assistant._tcp.local" stripped
        Assert.Equal("http://homeassistant.local:8123", hit.BaseUrl); // from the TXT base_url
        Assert.Equal(8123, hit.Port);
    }

    [Fact]
    public void Without_a_base_url_txt_the_url_is_built_from_the_a_record()
    {
        var datagram = BuildResponse(includeBaseUrlTxt: false);
        var hit = Assert.Single(MdnsMessage.ParseResponse(datagram, Service));
        Assert.Equal("http://192.168.1.50:8123", hit.BaseUrl); // host resolved via the A record
    }

    // ---- craft a realistic compressed response by hand ----
    private static byte[] BuildResponse(bool includeBaseUrlTxt = true)
    {
        var buf = new List<byte>();
        void U16(int v) { buf.Add((byte)(v >> 8)); buf.Add((byte)(v & 0xFF)); }
        void Name(string s) { foreach (var l in s.Split('.')) { buf.Add((byte)l.Length); buf.AddRange(Encoding.ASCII.GetBytes(l)); } buf.Add(0); }

        // header: 3 answers (PTR, SRV, TXT) + 1 additional (A)
        U16(0); U16(0x8400); U16(0); U16(3); U16(0); U16(1);

        var serviceOffset = buf.Count;
        // --- PTR: service -> instance ---
        Name(Service);
        U16(12); U16(1); U16(0); U16(0); // type PTR, class IN, ttl
        var instanceName = "Home." + Service;
        var rd = new List<byte>();
        foreach (var l in instanceName.Split('.')) { rd.Add((byte)l.Length); rd.AddRange(Encoding.ASCII.GetBytes(l)); }
        rd.Add(0);
        U16(rd.Count); buf.AddRange(rd);

        var instanceOffset = buf.Count;
        // --- SRV for the instance ---
        Name(instanceName);
        U16(33); U16(1); U16(0); U16(0);
        var host = "homeassistant.local";
        var srv = new List<byte> { 0, 0, 0, 0, (byte)(8123 >> 8), (byte)(8123 & 0xFF) }; // prio/weight/port
        foreach (var l in host.Split('.')) { srv.Add((byte)l.Length); srv.AddRange(Encoding.ASCII.GetBytes(l)); }
        srv.Add(0);
        U16(srv.Count); buf.AddRange(srv);
        // remember where the host name was written for the A record's compression pointer
        var hostOffset = instanceOffset + Name_Length(instanceName) + 10 + 6;

        // --- TXT for the instance (compression pointer back to instanceOffset) ---
        buf.Add(0xC0); buf.Add((byte)instanceOffset);
        U16(16); U16(1); U16(0); U16(0);
        var txt = new List<byte>();
        void Txt(string kv) { txt.Add((byte)kv.Length); txt.AddRange(Encoding.UTF8.GetBytes(kv)); }
        Txt("location_name=Home");
        if (includeBaseUrlTxt) Txt("base_url=http://homeassistant.local:8123");
        U16(txt.Count); buf.AddRange(txt);

        // --- additional A record: host -> 192.168.1.50 (compression pointer to hostOffset) ---
        buf.Add(0xC0); buf.Add((byte)hostOffset);
        U16(1); U16(1); U16(0); U16(0);
        U16(4); buf.AddRange(new byte[] { 192, 168, 1, 50 });

        return [.. buf];
    }

    private static int Name_Length(string s)
    {
        var n = 1; // root byte
        foreach (var l in s.Split('.')) n += 1 + l.Length;
        return n;
    }
}
