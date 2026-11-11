using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Shared;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAdmin", "Admin");
Directory.CreateDirectory(dataDir);
var devices = new ConcurrentDictionary<string, DeviceSnapshot>();
var logPath = Path.Combine(dataDir, "Admin.log");
void Log(string message) => File.AppendAllText(logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
Log("Admin started.");
var connections = new ConcurrentDictionary<string, WebSocket>();
var clientSendGates = new ConcurrentDictionary<string, SemaphoreSlim>();
var browserConnections = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, WebSocket>>();
var instanceId = Guid.NewGuid().ToString("N");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:38990", "http://0.0.0.0:" + Protocol.ClientPort);
builder.WebHost.UseKestrel();
var app = builder.Build();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/", () => Results.Content(ReadDashboard(), "text/html"));

var discoveryTask = Task.Run(() => DiscoveryLoop(app.Lifetime.ApplicationStopping));
var registryTask = Task.Run(() => RegistryHeartbeat(app.Lifetime.ApplicationStopping));

app.MapGet("/api/devices", () => Results.Json(devices.Values.OrderBy(x => x.DisplayName), Protocol.Json));
app.MapPost("/api/devices/{id}/name", async (string id, HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<NameRequest>(request.Body, Protocol.Json);
    if (body is null || !devices.TryGetValue(id, out var old)) return Results.BadRequest();
    devices[id] = old with { DisplayName = string.IsNullOrWhiteSpace(body.Name) ? old.ComputerName : body.Name };
    return Results.Ok(devices[id]);
});
app.MapDelete("/api/devices/{id}", (string id) =>
{
    devices.TryRemove(id, out _);
    if (connections.TryRemove(id, out var socket)) _ = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Removed", CancellationToken.None);
    return Results.Ok();
});
app.MapGet("/ws/{id}", async (string id, HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!connections.TryGetValue(id, out var socket)) { context.Response.StatusCode = 404; return; }
    using var browser = await context.WebSockets.AcceptWebSocketAsync();
    var browserId = Guid.NewGuid();
    var peers = browserConnections.GetOrAdd(id, _ => new ConcurrentDictionary<Guid, WebSocket>());
    peers[browserId] = browser;
    try
    {
        while (browser.State == WebSocketState.Open && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessage(browser, context.RequestAborted);
            if (message is null) break;
            if (clientSendGates.TryGetValue(id, out var sendGate))
                await SendRaw(socket, message, sendGate, context.RequestAborted);
        }
    }
    finally
    {
        peers.TryRemove(browserId, out _);
        if (peers.IsEmpty) browserConnections.TryRemove(id, out _);
    }
});
app.Map("/client", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var clientSocket = await context.WebSockets.AcceptWebSocketAsync();
    var hello = await ReceiveJson<ClientHello>(clientSocket, context.RequestAborted);
    if (hello is null) return;
    Log($"Client connected: {hello.ComputerName} ({hello.DeviceId}) from {hello.IpAddress}.");
    var snapshot = new DeviceSnapshot(hello.DeviceId, hello.ComputerName, hello.ComputerName, "Connected", DateTime.UtcNow, hello.IpAddress, null);
    devices[hello.DeviceId] = snapshot;
    connections[hello.DeviceId] = clientSocket;
    var sendGate = clientSendGates.GetOrAdd(hello.DeviceId, _ => new SemaphoreSlim(1, 1));
    await SendJson(clientSocket, new ServerHello("serverHello", null), sendGate, context.RequestAborted);
    try
    {
        while (clientSocket.State == WebSocketState.Open)
        {
            var data = await ReceiveMessage(clientSocket, context.RequestAborted);
            if (data is null) break;
            var json = Encoding.UTF8.GetString(data);
            if (TryGetStats(json, out var stats))
                devices[hello.DeviceId] = devices[hello.DeviceId] with { Stats = stats, LastSeenUtc = DateTime.UtcNow };
            if (browserConnections.TryGetValue(hello.DeviceId, out var browsers))
                foreach (var browser in browsers.Values.Where(x => x.State == WebSocketState.Open))
                    await browser.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        connections.TryRemove(hello.DeviceId, out _);
        if (clientSendGates.TryRemove(hello.DeviceId, out var gate)) gate.Dispose();
        Log($"Client disconnected: {hello.ComputerName} ({hello.DeviceId}).");
        devices.TryRemove(hello.DeviceId, out _);
    }
});

Console.WriteLine("Admin dashboard: http://127.0.0.1:38990");
_ = Process.Start(new ProcessStartInfo("http://127.0.0.1:38990") { UseShellExecute = true });
await app.RunAsync();

async Task DiscoveryLoop(CancellationToken stop)
{
    using var udp = new UdpClient(Protocol.DiscoveryPort) { EnableBroadcast = true };
    while (!stop.IsCancellationRequested)
    {
        try
        {
            var received = await udp.ReceiveAsync(stop);
            if (Encoding.UTF8.GetString(received.Buffer) == Protocol.DiscoveryRequest)
            {
                Log($"Discovery request from {received.RemoteEndPoint.Address}.");
                var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryAnnouncement(GetLocalIp(), Protocol.ClientPort, instanceId), Protocol.Json);
                await udp.SendAsync(response, response.Length, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch { await Task.Delay(1000, stop); }
    }
}

async Task RegistryHeartbeat(CancellationToken stop)
{
    while (!stop.IsCancellationRequested)
    {
        foreach (var pair in devices)
            if (pair.Value.Status == "Connected" && !connections.ContainsKey(pair.Key))
                devices.TryRemove(pair.Key, out _);
        await Task.Delay(TimeSpan.FromSeconds(5), stop);
    }
}

static string GetLocalIp() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
static bool TryGetStats(string json, out SystemStats? stats) { stats = null; try { var envelope = JsonSerializer.Deserialize<JsonElement>(json, Protocol.Json); if (envelope.GetProperty("type").GetString() == "stats") { stats = envelope.GetProperty("payload").Deserialize<SystemStats>(Protocol.Json); return stats is not null; } } catch { } return false; }
static async Task<T?> ReceiveJson<T>(WebSocket socket, CancellationToken cancellationToken)
{
    var data = await ReceiveMessage(socket, cancellationToken);
    return data is null ? default : JsonSerializer.Deserialize<T>(data, Protocol.Json);
}
static async Task<byte[]?> ReceiveMessage(WebSocket socket, CancellationToken cancellationToken)
{
    using var message = new MemoryStream();
    var buffer = new byte[64 * 1024];
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        if (result.MessageType != WebSocketMessageType.Text) continue;
        message.Write(buffer, 0, result.Count);
        if (result.EndOfMessage) return message.ToArray();
    }
}
static async Task SendJson<T>(WebSocket socket, T value, SemaphoreSlim sendGate, CancellationToken cancellationToken)
    => await SendRaw(socket, JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json), sendGate, cancellationToken);
static async Task SendRaw(WebSocket socket, byte[] data, SemaphoreSlim sendGate, CancellationToken cancellationToken)
{
    await sendGate.WaitAsync(cancellationToken);
    try { await socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken); }
    finally { sendGate.Release(); }
}
static string ReadDashboard()
{
    var resource = typeof(Program).Assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("wwwroot.index.html", StringComparison.OrdinalIgnoreCase));
    if (resource is null) return "<h1>Dashboard resource missing</h1>";
    using var stream = typeof(Program).Assembly.GetManifestResourceStream(resource);
    using var reader = new StreamReader(stream!);
    return reader.ReadToEnd();
}

record NameRequest(string Name);
