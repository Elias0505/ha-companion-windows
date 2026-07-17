// SPDX-License-Identifier: AGPL-3.0-only
using System.Buffers.Binary;
using System.Text;

namespace HaCompanion.Core.Discovery;

/// <summary>A Home Assistant instance found via mDNS.</summary>
public sealed record DiscoveredInstance(string Name, string? BaseUrl, string Host, int Port);

/// <summary>
/// Minimal, pure mDNS (DNS) message encoder/parser — just enough to ask for
/// <c>_home-assistant._tcp.local</c> PTR records and read the PTR/SRV/TXT/A answers.
/// No sockets in here; fully covered by the platform-independent tests.
/// </summary>
public static class MdnsMessage
{
    private const ushort TypePtr = 12;
    private const ushort TypeA = 1;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;

    /// <summary>
    /// One-shot PTR question with the QU ("unicast response requested") bit set, so
    /// responders reply directly to our ephemeral port — no multicast group join and
    /// no firewall rule needed on the client.
    /// </summary>
    public static byte[] BuildQuery(string serviceName)
    {
        var buffer = new List<byte>(64)
        {
            0, 0,       // transaction id (0 for mDNS)
            0, 0,       // flags: standard query
            0, 1,       // QDCOUNT = 1
            0, 0, 0, 0, 0, 0, // AN/NS/AR counts
        };
        foreach (var label in serviceName.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);              // root label
        buffer.Add(0); buffer.Add(TypePtr & 0xFF);
        buffer.Add(0x80); buffer.Add(0x01); // class IN with the QU bit
        return [.. buffer];
    }

    /// <summary>
    /// Extract HA instances from one response datagram. Tolerant by design: anything
    /// malformed yields an empty list, never an exception.
    /// </summary>
    public static IReadOnlyList<DiscoveredInstance> ParseResponse(byte[] datagram, string serviceName)
    {
        try
        {
            return ParseCore(datagram, serviceName.TrimEnd('.'));
        }
        catch
        {
            return [];
        }
    }

    private static List<DiscoveredInstance> ParseCore(byte[] d, string service)
    {
        var results = new List<DiscoveredInstance>();
        if (d.Length < 12)
            return results;

        int qd = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        int records = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6))   // answers
                    + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8))   // authority
                    + BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10)); // additional
        var pos = 12;

        // skip questions
        for (var i = 0; i < qd; i++)
        {
            SkipName(d, ref pos);
            pos += 4;
        }

        var instanceNames = new List<string>();
        var srvByName = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txtByName = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var addressByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records && pos < d.Length; i++)
        {
            var name = ReadName(d, ref pos);
            if (pos + 10 > d.Length)
                break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 8));
            pos += 10;
            if (pos + rdLength > d.Length)
                break;
            var rdataPos = pos;
            pos += rdLength;

            switch (type)
            {
                case TypePtr when name.Equals(service, StringComparison.OrdinalIgnoreCase):
                    var instance = ReadName(d, ref rdataPos);
                    if (instance.Length > 0)
                        instanceNames.Add(instance);
                    break;
                case TypeSrv when rdLength >= 7:
                    var port = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(rdataPos + 4));
                    var targetPos = rdataPos + 6;
                    srvByName[name] = (ReadName(d, ref targetPos), port);
                    break;
                case TypeTxt:
                    txtByName[name] = ReadTxt(d, rdataPos, rdLength);
                    break;
                case TypeA when rdLength == 4:
                    addressByHost[name] = $"{d[rdataPos]}.{d[rdataPos + 1]}.{d[rdataPos + 2]}.{d[rdataPos + 3]}";
                    break;
            }
        }

        foreach (var instance in instanceNames)
        {
            srvByName.TryGetValue(instance, out var srv);
            txtByName.TryGetValue(instance, out var txt);

            string? baseUrl = null;
            if (txt is not null)
                foreach (var key in new[] { "base_url", "internal_url", "external_url" })
                    if (txt.TryGetValue(key, out var url) && !string.IsNullOrWhiteSpace(url))
                    {
                        baseUrl = url;
                        break;
                    }

            var host = srv.Target ?? string.Empty;
            if (host.Length > 0 && addressByHost.TryGetValue(host, out var ip))
                host = ip;
            if (baseUrl is null && host.Length > 0 && srv.Port > 0)
                baseUrl = $"http://{host}:{srv.Port}";

            // "Home @ host._home-assistant._tcp.local" -> "Home @ host"
            var display = instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase)
                ? instance[..^(service.Length + 1)]
                : instance;
            results.Add(new DiscoveredInstance(display, baseUrl, host, srv.Port));
        }
        return results;
    }

    private static Dictionary<string, string> ReadTxt(byte[] d, int pos, int length)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var end = pos + length;
        while (pos < end)
        {
            int len = d[pos++];
            if (len == 0 || pos + len > end)
                break;
            var entry = Encoding.UTF8.GetString(d, pos, len);
            pos += len;
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                pairs[entry[..eq]] = entry[(eq + 1)..];
        }
        return pairs;
    }

    /// <summary>Read a (possibly compressed) DNS name and advance <paramref name="pos"/>.</summary>
    private static string ReadName(byte[] d, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var cursor = pos;
        var guard = 0;
        while (cursor < d.Length && guard++ < 64)
        {
            int len = d[cursor];
            if (len == 0)
            {
                cursor++;
                break;
            }
            if ((len & 0xC0) == 0xC0) // compression pointer
            {
                if (cursor + 1 >= d.Length)
                    break;
                var target = ((len & 0x3F) << 8) | d[cursor + 1];
                if (!jumped)
                    pos = cursor + 2;
                jumped = true;
                cursor = target;
                continue;
            }
            if (cursor + 1 + len > d.Length)
                break;
            labels.Add(Encoding.UTF8.GetString(d, cursor + 1, len));
            cursor += 1 + len;
        }
        if (!jumped)
            pos = cursor;
        return string.Join('.', labels);
    }

    private static void SkipName(byte[] d, ref int pos) => ReadName(d, ref pos);
}
