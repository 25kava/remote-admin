using System.Net.WebSockets;
using System.Text.Json;

namespace RemoteAdmin.Shared;

public static class ClientTransport
{
    public static async Task RunAsync(DiscoveryAnnouncement server, ClientHello hello,
        Func<ClientWebSocket, SemaphoreSlim, CancellationToken, Task> session,
        Action<string> log, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        using var sendGate = new SemaphoreSlim(1, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            log($"Connecting to {server.Host}:{server.Port} (Admin {server.InstanceId}).");
            await socket.ConnectAsync(new UriBuilder("ws", server.Host, server.Port, "/client").Uri, handshake.Token);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(hello, Protocol.Json), WebSocketMessageType.Text, true, handshake.Token);
            using var response = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(buffer, handshake.Token);
                if (frame.MessageType != WebSocketMessageType.Text || response.Length + frame.Count > 65536)
                    throw new InvalidDataException("Invalid Admin handshake.");
                response.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            if (JsonSerializer.Deserialize<ServerHello>(response.ToArray(), Protocol.Json)?.Type != "serverHello")
                throw new InvalidDataException("Missing Admin greeting.");
        }
        log($"Connected to Admin {server.InstanceId} through {server.Host}:{server.Port}.");
        try { await session(socket, sendGate, lifetime.Token); }
        finally
        {
            await lifetime.CancelAsync();
            socket.Abort();
            log($"Disconnected from Admin {server.InstanceId} ({server.Host}:{server.Port}).");
        }
    }
}
