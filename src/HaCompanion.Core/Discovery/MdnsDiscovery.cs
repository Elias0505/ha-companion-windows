// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HaCompanion.Core.Discovery;

/// <summary>
/// Best-effort discovery of Home Assistant instances on the local network.
/// Sends one mDNS PTR query for <c>_home-assistant._tcp.local</c> from an ephemeral
/// port (QU bit set → unicast replies pass the stateful firewall without a rule or a
/// multicast group join) and collects answers for the given window. Networks that
/// filter mDNS simply yield an empty result — never an error.
/// </summary>
public static class MdnsDiscovery
{
    public const string HaServiceName = "_home-assistant._tcp.local";

    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<DiscoveredInstance>> DiscoverAsync(
        TimeSpan timeout, ILogger? logger = null, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredInstance>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var udp = new UdpClient(0); // ephemeral port; replies come back unicast
            var query = MdnsMessage.BuildQuery(HaServiceName);
            await udp.SendAsync(query, MdnsEndpoint, ct).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);
            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // window elapsed
                }
                foreach (var instance in MdnsMessage.ParseResponse(result.Buffer, HaServiceName))
                    found[instance.Name] = instance;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "mDNS discovery failed (network may filter multicast)");
        }
        return [.. found.Values];
    }
}
