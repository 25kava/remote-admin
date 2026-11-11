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
        var browserId = Guid.NewGuid();
        var browser = new BrowserConnection(id, socket, browserId.ToString("N"));
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
                {
                    using var json = JsonDocument.Parse(message);
                    var type = json.RootElement.TryGetProperty("type", out var kind) ? kind.GetString() : null;
                    if (type == "screenStart")
                    {
                        if (!client.Snapshot.ScreenStreamingSupported)
                        {
                            await SendRaw(socket, JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                type = "screenState", payload = new ScreenShareState(browser.ScreenRequestId,
                                    "unsupported", "Update Client.exe on this computer to enable screen sharing.")
                            }, Protocol.Json), browser.SendGate, context.RequestAborted);
                            continue;
                        }
                        browser.ScreenSubscribed = true;
                        browser.ScreenStarted = true;
                        message = JsonSerializer.SerializeToUtf8Bytes(new
                        { type = "screenStart", payload = new ScreenStart(browser.ScreenRequestId) }, Protocol.Json);
                    }
                    else if (type == "screenStop")
                    {
                        await StopBrowserControl(browser);
                        browser.ScreenSubscribed = false;
                        message = JsonSerializer.SerializeToUtf8Bytes(new
                        { type = "screenStop", payload = new ScreenStop(browser.ScreenRequestId) }, Protocol.Json);
                    }
                    else if (type is "controlStart" or "controlStop" or "controlInput" or "controlHeartbeat")
                    {
                        if (type == "controlStop") browser.ControlRequested = false;
                        else if (!browser.ScreenSubscribed || !client.Snapshot.RemoteControlSupported ||
                            !json.RootElement.TryGetProperty("payload", out var fullPayload) ||
                            !fullPayload.TryGetProperty("fullscreen", out var fullscreen) || fullscreen.ValueKind != JsonValueKind.True)
                        {
                            if (type == "controlStart")
                                await SendRaw(socket, JsonSerializer.SerializeToUtf8Bytes(new
                                { type = "controlState", payload = new ControlState(browser.ScreenRequestId, "unavailable", "Open the live view in fullscreen and update Client.exe to enable control.") },
                                    Protocol.Json), browser.SendGate, context.RequestAborted);
                            continue;
                        }
                        if (type == "controlStart") { browser.ControlRequested = true; browser.ControlStarted = true; }
                        if (type is "controlInput" or "controlHeartbeat" && !browser.ControlRequested) continue;
                        message = type == "controlInput"
                            ? JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                type, payload = new ControlInput(browser.ScreenRequestId,
                                    json.RootElement.GetProperty("payload").GetProperty("input").Deserialize<InputEvent>(Protocol.Json)!)
                            }, Protocol.Json)
                            : JsonSerializer.SerializeToUtf8Bytes(new { type, payload = new ControlRequest(browser.ScreenRequestId) }, Protocol.Json);
                    }
                    await SendRaw(client.Socket, message, client.SendGate, context.RequestAborted);
                    // Keep cleanup pending until the stop was actually sent. A page
                    // reload can abort this request after the subscription flag changes.
                    if (type == "screenStop") browser.ScreenStarted = false;
                    if (type == "controlStop") browser.ControlStarted = false;
                }
            }
        }
        catch (Exception ex) when (IsConnectionError(ex) || ex is JsonException) { log($"Browser relay for {id} ended: {ex.Message}"); }
        finally
        {
            browsers.TryRemove(browserId, out _);
            await StopBrowserControl(browser);
            await StopBrowserScreen(browser);
        }
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
                "Connected", DateTime.UtcNow, ip, null, hello.ScreenStreamingSupported, hello.RemoteControlSupported));
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
                var (screenMessage, screenRequestId) = ReadScreenRoute(data);
                await Task.WhenAll(browsers.Where(x => x.Value.DeviceId == id)
                    .Where(x => !screenMessage || (x.Value.ScreenSubscribed && x.Value.ScreenRequestId == screenRequestId))
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
            await StopBrowserControl(browser);
            await StopBrowserScreen(browser);
            log($"Browser output for {browser.DeviceId} ended: {ex.Message}");
        }
    }

    private async Task StopBrowserScreen(BrowserConnection browser)
    {
        if (!browser.ScreenStarted) return;
        browser.ScreenSubscribed = false;
        if (!clients.TryGetValue(browser.DeviceId, out var client)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendRaw(client.Socket, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "screenStop", payload = new ScreenStop(browser.ScreenRequestId) }, Protocol.Json),
                client.SendGate, timeout.Token);
            browser.ScreenStarted = false;
        }
        catch (Exception ex) when (IsConnectionError(ex)) { log($"Screen stop relay ended: {ex.Message}"); }
    }

    private async Task StopBrowserControl(BrowserConnection browser)
    {
        if (!browser.ControlStarted) return;
        browser.ControlRequested = false;
        if (!clients.TryGetValue(browser.DeviceId, out var client)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendRaw(client.Socket, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "controlStop", payload = new ControlRequest(browser.ScreenRequestId) }, Protocol.Json), client.SendGate, timeout.Token);
            browser.ControlStarted = false;
        }
        catch (Exception ex) when (IsConnectionError(ex)) { log($"Control stop relay ended: {ex.Message}"); }
    }

    private static (bool ScreenMessage, string? RequestId) ReadScreenRoute(byte[] data)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            if (json.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() is "screenFrame" or "screenState" or "controlState")
            {
                var requestId = json.RootElement.TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                return (true, requestId);
            }
        }
        catch (JsonException) { }
        return (false, null);
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

    private sealed record BrowserConnection(string DeviceId, WebSocket Socket, string ScreenRequestId)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public volatile bool ScreenSubscribed;
        public volatile bool ControlRequested;
        public volatile bool ScreenStarted;
        public volatile bool ControlStarted;
    }
    private sealed record NameRequest(string Name);
}
