// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.WebSockets;
using System.Text;
using HaCompanion.Core.WebSocket;
using Xunit;

namespace HaCompanion.Core.Tests;

public class HaWebSocketReceiveTests
{
    /// <summary>Fake receive: hands out the queued frames one buffer-fill at a time.</summary>
    private static Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> Frames(
        params (byte[] Bytes, bool EndOfMessage)[] frames)
    {
        var queue = new Queue<(byte[], bool)>(frames.Select(f => (f.Bytes, f.EndOfMessage)));
        return (buffer, _) =>
        {
            var (bytes, end) = queue.Dequeue();
            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, end));
        };
    }

    [Fact]
    public async Task Message_split_across_frames_is_reassembled()
    {
        var receive = Frames(
            (Encoding.UTF8.GetBytes("{\"a\""), false),
            (Encoding.UTF8.GetBytes(":1}"), true));

        using var doc = await HaWebSocketClient.ReceiveJsonAsync(receive, CancellationToken.None);

        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task Close_frame_throws_connection_closed()
    {
        Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> receive =
            (_, _) => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        await Assert.ThrowsAsync<WebSocketException>(
            () => HaWebSocketClient.ReceiveJsonAsync(receive, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_message_is_cut_off_instead_of_buffered()
    {
        // Endless 16-KB frames that never signal EndOfMessage.
        var calls = 0;
        var chunk = new byte[16 * 1024];
        Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>> receive = (buffer, _) =>
        {
            calls++;
            chunk.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(chunk.Length, WebSocketMessageType.Text, false));
        };

        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => HaWebSocketClient.ReceiveJsonAsync(receive, CancellationToken.None));

        Assert.Contains("receive limit", ex.Message, StringComparison.Ordinal);
        // The guard must trip right at the limit, not after further buffering.
        Assert.True(calls <= HaWebSocketClient.MaxMessageBytes / chunk.Length + 1,
            $"receive was called {calls} times — the size guard fired too late");
    }
}
