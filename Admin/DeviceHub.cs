using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteAdmin.Shared;

public sealed class DeviceHub(Action<string> log)
{
    // The socket, send gate and device snapshot have one shared connection lifetime.
    private readonly ConcurrentDictionary<string, ClientConnection> clients = new();
    private readonly ConcurrentDictionary<Guid, BrowserConnection> browsers = new();

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/devices", () => Results.Json(clients.Values.Select(x => x.Snapshot)
            .OrderBy(x => x.DisplayName), Protocol.Json));
        app.MapPost("/api/devices/{id}/name", async (string id, HttpRequest request) =>
        {
            var body = await JsonSerializer.DeserializeAsync<NameRequest>(request.Body, Protocol.Json);
            if (body is null || !clients.TryGetValue(id, out var client)) return Results.BadRequest();
            client.Update(old => old with { DisplayName = string.IsNullOrWhiteSpace(body.Name) ? old.ComputerName : body.Name });
            return Results.Ok(client.Snapshot);
        });
        app.MapDelete("/api/devices/{id}", (string id) =>
        {
            if (clients.TryRemove(id, out var client)) client.Socket.Abort();
            return Results.Ok();
        });
        app.MapGet("/ws/{id}", RelayBrowser);
        app.Map("/client", AcceptClient);
    }

    private async Task RelayBrowser(string id, HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!clients.ContainsKey(id)) { context.Response.StatusCode = 404; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var browser = new BrowserConnection(id, socket);
        var browserId = Guid.NewGuid();
        browsers[browserId] = browser;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessage(socket, context.RequestAborted);
                if (message is null) break;
                // A browser session can outlive a client reconnect. Resolve the current
                // connection for each message instead of holding a stale socket/gate pair.
                if (clients.TryGetValue(id, out var client))
                    await SendRaw(client.Socket, message, client.SendGate, context.RequestAborted);
            }
        }
        catch (Exception ex) when (IsConnectionError(ex)) { log($"Browser relay for {id} ended: {ex.Message}"); }
        finally { browsers.TryRemove(browserId, out _); }
    }

    private async Task AcceptClient(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        ClientConnection? client = null;
        string? id = null;
        var reason = "peer closed the connection";
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            var data = await ReceiveMessage(socket, handshake.Token);
            var hello = data is null ? null : JsonSerializer.Deserialize<ClientHello>(data, Protocol.Json);
            if (hello is null || hello.Type != "clientHello" || string.IsNullOrWhiteSpace(hello.DeviceId)) return;
            id = hello.DeviceId;
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? hello.IpAddress;
            client = new ClientConnection(socket, new(id, hello.ComputerName, hello.ComputerName,
                "Connected", DateTime.UtcNow, ip, null));
            ClientConnection? previous = null;
            clients.AddOrUpdate(id, client, (_, old) => { previous = old; return client; });
            // Abort does not race a second ReceiveAsync/CloseAsync on the previous socket.
            previous?.Socket.Abort();
            log($"Client connected: {hello.ComputerName} ({id}) from {ip}.");
            await SendRaw(socket, JsonSerializer.SerializeToUtf8Bytes(new ServerHello("serverHello", null), Protocol.Json),
                client.SendGate, handshake.Token);

            while (socket.State == WebSocketState.Open)
            {
                data = await ReceiveMessage(socket, context.RequestAborted);
                if (data is null) break;
                if (!clients.TryGetValue(id, out var current) || !ReferenceEquals(current, client)) break;
                var stats = ReadStats(data);
                client.Update(old => old with { LastSeenUtc = DateTime.UtcNow, Stats = stats ?? old.Stats });
                await Task.WhenAll(browsers.Where(x => x.Value.DeviceId == id)
                    .Select(x => ForwardToBrowser(x.Key, x.Value, data, context.RequestAborted)));
            }
        }
        catch (Exception ex) when (IsConnectionError(ex) || ex is JsonException)
        { reason = $"{ex.GetType().Name}: {ex.Message}"; }
        finally
        {
            if (id is not null && client is not null)
            {
                var removed = ((ICollection<KeyValuePair<string, ClientConnection>>)clients).Remove(new(id, client));
                log($"Client disconnected: {client.Snapshot.ComputerName} ({id}); current={removed}; reason={reason}.");
            }
        }
    }

    private async Task ForwardToBrowser(Guid id, BrowserConnection browser, byte[] data, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await SendRaw(browser.Socket, data, browser.SendGate, timeout.Token);
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            // Closing or refreshing the dashboard must not disconnect the managed PC.
            browsers.TryRemove(id, out _);
            browser.Socket.Abort();
            log($"Browser output for {browser.DeviceId} ended: {ex.Message}");
        }
    }

    private static bool IsConnectionError(Exception ex) =>
        ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException;

    private static SystemStats? ReadStats(byte[] data)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "stats" &&
                json.RootElement.TryGetProperty("payload", out var payload))
                return payload.Deserialize<SystemStats>(Protocol.Json);
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task<byte[]?> ReceiveMessage(WebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    private static async Task SendRaw(WebSocket socket, byte[] data, SemaphoreSlim gate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await socket.SendAsync(data, WebSocketMessageType.Text, true, token); }
        finally { gate.Release(); }
    }

    private sealed class ClientConnection(WebSocket socket, DeviceSnapshot snapshot)
    {
        private readonly object gate = new();
        private DeviceSnapshot snapshot = snapshot;
        public WebSocket Socket { get; } = socket;
        // Do not dispose while an already accepted browser send may still reference it.
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public DeviceSnapshot Snapshot => Volatile.Read(ref snapshot);
        public void Update(Func<DeviceSnapshot, DeviceSnapshot> update)
        { lock (gate) Volatile.Write(ref snapshot, update(snapshot)); }
    }

    private sealed record BrowserConnection(string DeviceId, WebSocket Socket)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
    private sealed record NameRequest(string Name);
}
