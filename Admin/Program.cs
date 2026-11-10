using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Shared;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAdmin", "Admin");
Directory.CreateDirectory(dataDir);
var registryPath = Path.Combine(dataDir, "devices.json");
var devices = LoadRegistry(registryPath);
var connections = new ConcurrentDictionary<string, WebSocket>();
var browserConnections = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, WebSocket>>();
var instanceId = Guid.NewGuid().ToString("N");
var serverSecretPath = Path.Combine(dataDir, "server.secret");
if (!File.Exists(serverSecretPath)) File.WriteAllText(serverSecretPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
var serverSecret = File.ReadAllText(serverSecretPath);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:38990", "http://0.0.0.0:" + Protocol.ClientPort);
builder.WebHost.UseKestrel();
var app = builder.Build();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/", () => Results.Content(ReadDashboard(), "text/html"));

var auth = new AuthStore(Path.Combine(dataDir, "credentials.json"), serverSecret);
var discoveryTask = Task.Run(() => DiscoveryLoop(app.Lifetime.ApplicationStopping));
var registryTask = Task.Run(() => RegistryHeartbeat(app.Lifetime.ApplicationStopping));

app.MapGet("/api/devices", () => Results.Json(devices.Values.OrderBy(x => x.DisplayName), Protocol.Json));
app.MapPost("/api/login", async (HttpRequest request) =>
{
    var login = await JsonSerializer.DeserializeAsync<LoginRequest>(request.Body, Protocol.Json);
    return login is not null && auth.Verify(login.UserName, login.Password)
        ? Results.Ok(new { ok = true })
        : Results.Unauthorized();
});
app.MapPost("/api/devices/{id}/name", async (string id, HttpRequest request) =>
{
    var body = await JsonSerializer.DeserializeAsync<NameRequest>(request.Body, Protocol.Json);
    if (body is null || !devices.TryGetValue(id, out var old)) return Results.BadRequest();
    devices[id] = old with { DisplayName = string.IsNullOrWhiteSpace(body.Name) ? old.ComputerName : body.Name };
    SaveRegistry(registryPath, devices);
    return Results.Ok(devices[id]);
});
app.MapDelete("/api/devices/{id}", (string id) =>
{
    devices.TryRemove(id, out _);
    if (connections.TryRemove(id, out var socket)) _ = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Removed", CancellationToken.None);
    SaveRegistry(registryPath, devices);
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
        var buffer = new byte[128 * 1024];
        while (browser.State == WebSocketState.Open && socket.State == WebSocketState.Open)
        {
            var result = await browser.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
            await socket.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Text, result.EndOfMessage, context.RequestAborted);
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
    if (hello is null || !auth.VerifySecret(hello.Secret))
    {
        await SendJson(clientSocket, new ServerHello("serverHello", false, "Authentication failed"), context.RequestAborted);
        return;
    }
    var snapshot = devices.GetOrAdd(hello.DeviceId, _ => new DeviceSnapshot(hello.DeviceId, hello.ComputerName, hello.ComputerName, "Connected", DateTime.UtcNow, hello.IpAddress, null));
    devices[hello.DeviceId] = snapshot with { ComputerName = hello.ComputerName, Status = "Connected", LastSeenUtc = DateTime.UtcNow, IpAddress = hello.IpAddress };
    SaveRegistry(registryPath, devices);
    connections[hello.DeviceId] = clientSocket;
    await SendJson(clientSocket, new ServerHello("serverHello", true, null), context.RequestAborted);
    try
    {
        var buffer = new byte[256 * 1024];
        while (clientSocket.State == WebSocketState.Open)
        {
            var result = await clientSocket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (TryGetStats(json, out var stats))
                devices[hello.DeviceId] = devices[hello.DeviceId] with { Stats = stats, LastSeenUtc = DateTime.UtcNow };
            if (browserConnections.TryGetValue(hello.DeviceId, out var browsers))
                foreach (var browser in browsers.Values.Where(x => x.State == WebSocketState.Open))
                    await browser.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        connections.TryRemove(hello.DeviceId, out _);
        if (devices.TryGetValue(hello.DeviceId, out var current)) devices[hello.DeviceId] = current with { Status = "Disconnected", LastSeenUtc = DateTime.UtcNow };
        SaveRegistry(registryPath, devices);
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
                var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryAnnouncement(GetLocalIp(), Protocol.ClientPort, instanceId, serverSecret), Protocol.Json);
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
                devices[pair.Key] = pair.Value with { Status = "Disconnected" };
        SaveRegistry(registryPath, devices);
        await Task.Delay(TimeSpan.FromSeconds(5), stop);
    }
}

static string GetLocalIp() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
static ConcurrentDictionary<string, DeviceSnapshot> LoadRegistry(string path) => File.Exists(path)
    ? JsonSerializer.Deserialize<ConcurrentDictionary<string, DeviceSnapshot>>(File.ReadAllText(path), Protocol.Json) ?? new()
    : new();
static void SaveRegistry(string path, ConcurrentDictionary<string, DeviceSnapshot> value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Protocol.Json));
static bool TryGetStats(string json, out SystemStats? stats) { stats = null; try { var envelope = JsonSerializer.Deserialize<JsonElement>(json, Protocol.Json); if (envelope.GetProperty("type").GetString() == "stats") { stats = envelope.GetProperty("payload").Deserialize<SystemStats>(Protocol.Json); return stats is not null; } } catch { } return false; }
static async Task<T?> ReceiveJson<T>(WebSocket socket, CancellationToken cancellationToken) { var buffer = new byte[64 * 1024]; var result = await socket.ReceiveAsync(buffer, cancellationToken); return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, result.Count), Protocol.Json); }
static Task SendJson<T>(WebSocket socket, T value, CancellationToken cancellationToken) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json), WebSocketMessageType.Text, true, cancellationToken);
static string ReadDashboard()
{
    var resource = typeof(Program).Assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("wwwroot.index.html", StringComparison.OrdinalIgnoreCase));
    if (resource is null) return "<h1>Dashboard resource missing</h1>";
    using var stream = typeof(Program).Assembly.GetManifestResourceStream(resource);
    using var reader = new StreamReader(stream!);
    return reader.ReadToEnd();
}

record LoginRequest(string UserName, string Password);
record NameRequest(string Name);

sealed class AuthStore
{
    private readonly string path;
    private readonly Dictionary<string, string> hashes;
    private readonly string serverSecret;
    public AuthStore(string path, string serverSecret) { this.path = path; this.serverSecret = serverSecret; hashes = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); if (hashes.Count == 0) { var initial = Environment.GetEnvironmentVariable("REMOTE_ADMIN_INITIAL_PASSWORD") ?? RandomNumberGenerator.GetHexString(16); hashes["25kava"] = Hash(initial); hashes["25mawa"] = Hash(initial); File.WriteAllText(path, JsonSerializer.Serialize(hashes)); Console.WriteLine($"Initial admin password: {initial}"); } }
    public bool Verify(string user, string password) => hashes.TryGetValue(user, out var hash) && VerifyHash(password, hash);
    public bool VerifySecret(string secret) => secret == serverSecret;
    private static string Hash(string value) { var salt = RandomNumberGenerator.GetBytes(16); var hash = Rfc2898DeriveBytes.Pbkdf2(value, salt, 120_000, HashAlgorithmName.SHA256, 32); return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash); }
    private static bool VerifyHash(string value, string encoded) { var parts = encoded.Split('.'); if (parts.Length != 2) return false; var actual = Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromBase64String(parts[0]), 120_000, HashAlgorithmName.SHA256, 32); return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(parts[1])); }
}
