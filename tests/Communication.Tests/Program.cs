using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RemoteAdmin.Shared;

var results = new List<string>();
async Task Test(string name, Func<Task> test)
{
    var timer = Stopwatch.StartNew();
    await test();
    var line = $"PASS {name} ({timer.Elapsed.TotalSeconds:F2}s)";
    results.Add(line);
    Console.WriteLine(line);
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Until(Func<Task<bool>> check, string message, int milliseconds = 6000)
{
    var timer = Stopwatch.StartNew();
    while (timer.ElapsedMilliseconds < milliseconds)
    {
        if (await check()) return;
        await Task.Delay(25);
    }
    throw new Exception(message);
}
var logs = new ConcurrentQueue<string>();
void Log(string message) => logs.Enqueue(message);

await Test("broadcast calculation excludes /31, /32 and link-local adapters", () =>
{
    Assert(LanDiscovery.BroadcastAddress(IPAddress.Parse("100.64.1.199"), IPAddress.Parse("255.255.255.255")) is null, "/32 must not send to itself");
    Assert(LanDiscovery.BroadcastAddress(IPAddress.Parse("10.1.1.2"), IPAddress.Parse("255.255.255.254")) is null, "/31 is point to point");
    Assert(LanDiscovery.BroadcastAddress(IPAddress.Parse("192.168.49.140"), IPAddress.Parse("255.255.252.0"))?.ToString() == "192.168.51.255", "incorrect /22 broadcast");
    Assert(LanDiscovery.BroadcastAddress(IPAddress.Parse("169.254.1.2"), IPAddress.Parse("255.255.0.0")) is null, "link local must be excluded");
    return Task.CompletedTask;
});

await Test("UDP discovery survives a closed port, malformed packets and delayed replies", async () =>
{
    using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var endpoint = (IPEndPoint)responder.Client.LocalEndPoint!;
    int closedPort;
    using (var closed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        closedPort = ((IPEndPoint)closed.Client.LocalEndPoint!).Port;
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var reply = Task.Run(async () =>
    {
        var request = await responder.ReceiveAsync(limit.Token);
        await responder.SendAsync(Encoding.UTF8.GetBytes("not JSON"), request.RemoteEndPoint, limit.Token);
        await Task.Delay(550, limit.Token);
        foreach (var port in new[] { 38992, 38992, 38993 })
            await responder.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new DiscoveryAnnouncement("ignored", port, "test-admin"), Protocol.Json), request.RemoteEndPoint, limit.Token);
    });
    var timer = Stopwatch.StartNew();
    var found = await LanDiscovery.FindAsync(Log, limit.Token,
        [new(IPAddress.Loopback, closedPort), endpoint], TimeSpan.FromMilliseconds(950));
    await reply;
    Assert(found.Count == 2, $"Expected two unique endpoints; got {found.Count}");
    Assert(timer.ElapsedMilliseconds >= 900, "Response window was abandoned early");
    Assert(found.All(x => x.Host == "127.0.0.1"), "Must use packet source address");
});

await Test("logging tolerates concurrent writers and an exclusively locked file", async () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "concurrent.log");
    File.Delete(path);
    var logger = new CommunicationLog(path);
    await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
    { for (var j = 0; j < 100; j++) logger.Write($"{i}:{j}"); })));
    Assert(File.ReadAllLines(path).Length == 2000, "Concurrent log entries lost");
    using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    logger.Write("A sharing violation must not escape into the connection loop");
});

await Test("discovery and independent reconnect continue while another Admin remains connected", async () =>
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var aCount = 0;
    var bCount = 0;
    var scans = 0;
    var a = new DiscoveryAnnouncement("127.0.0.1", 1, "a");
    var b = new DiscoveryAnnouncement("127.0.0.1", 2, "b");
    var disconnectB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run = ConnectionSupervisor.RunAsync(_ =>
    {
        var count = Interlocked.Increment(ref scans);
        return Task.FromResult(count < 3 ? new List<DiscoveryAnnouncement> { a } : new List<DiscoveryAnnouncement> { a, b });
    }, async (server, token) =>
    {
        if (server.InstanceId == "a") { Interlocked.Increment(ref aCount); await Task.Delay(Timeout.Infinite, token); }
        else if (Interlocked.Increment(ref bCount) == 1) await disconnectB.Task.WaitAsync(token);
        else await Task.Delay(Timeout.Infinite, token);
    }, Log, stop.Token, TimeSpan.FromMilliseconds(40));
    await Until(() => Task.FromResult(bCount == 1), "New Admin was not found while A stayed connected");
    disconnectB.SetResult();
    await Until(() => Task.FromResult(bCount >= 2), "B did not reconnect independently");
    Assert(aCount == 1 && scans >= 4, "Duplicate A connection or paused discovery");
    stop.Cancel();
    await run;
});

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
new DeviceHub(Log).MapEndpoints(app);
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var portNumber = new Uri(address).Port;
using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
Task<DeviceSnapshot[]?> Devices() => http.GetFromJsonAsync<DeviceSnapshot[]>("/api/devices", Protocol.Json);

await Test("real WebSocket handshake, stats and same-ID replacement remain consistent", async () =>
{
    var peers = new List<ProbeClient>();
    try
    {
        for (var i = 0; i < 50; i++)
        {
            var peer = new ProbeClient(portNumber, "replacement", $"generation-{i}", Log);
            peers.Add(peer);
            await peer.Ready;
            await peer.Send(new { type = "stats", payload = ProbeClient.Stats });
            await Until(async () => (await Devices()) is [{ ComputerName: var name, Stats: not null }] && name == $"generation-{i}", "Replacement disappeared or stats were lost");
        }
        await Task.Delay(250);
        Assert((await Devices()) is [{ ComputerName: "generation-49" }], "Old cleanup removed the replacement");
    }
    finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    await Until(async () => (await Devices())!.Length == 0, "Disconnected peer retained");
});

await Test("dashboard refresh/abort cannot tear down the client connection", async () =>
{
    await using var peer = new ProbeClient(portNumber, "browser-test", "browser-test", Log);
    await peer.Ready;
    for (var i = 0; i < 100; i++)
    {
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri($"ws://127.0.0.1:{portNumber}/ws/browser-test"), CancellationToken.None);
        var sending = peer.Send(new { type = "stats", payload = ProbeClient.Stats });
        browser.Abort();
        await sending;
    }
    await Task.Delay(250);
    Assert(!peer.Completion.IsCompleted, "Closing the dashboard disconnected the client");
    Assert((await Devices())!.Length == 1, "Client disappeared after dashboard closed");
});
await Until(async () => (await Devices())!.Length == 0, "Browser test leaked a device");

await Test("an existing browser relays to the new connection after replacement", async () =>
{
    await using var first = new ProbeClient(portNumber, "relay", "old", Log);
    await first.Ready;
    using var browser = new ClientWebSocket();
    await browser.ConnectAsync(new Uri($"ws://127.0.0.1:{portNumber}/ws/relay"), CancellationToken.None);
    await using var next = new ProbeClient(portNumber, "relay", "new", Log);
    await next.Ready;
    await browser.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"transportProbe\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var relayed = await next.Messages.Reader.ReadAsync(limit.Token);
    Assert(relayed.Contains("transportProbe"), "Browser sent to a stale client socket");
    await next.Send(new { type = "stats", payload = ProbeClient.Stats });
    var buffer = new byte[4096];
    var frame = await browser.ReceiveAsync(buffer, limit.Token);
    Assert(Encoding.UTF8.GetString(buffer, 0, frame.Count).Contains("stats"), "Client output did not reach existing browser");
});
await Until(async () => (await Devices())!.Length == 0, "Relay test leaked a device");

await Test("parallel registration and removal do not delete unrelated clients", async () =>
{
    var peers = Enumerable.Range(0, 30).Select(i => new ProbeClient(portNumber, $"parallel-{i}", $"parallel-{i}", Log)).ToArray();
    try
    {
        await Task.WhenAll(peers.Select(x => x.Ready));
        Assert((await Devices())!.Length == 30, "Lost a parallel registration");
        await Task.WhenAll(peers.Take(15).Select(x => x.DisposeAsync().AsTask()));
        await Until(async () => (await Devices())!.Length == 15, "Wrong count after partial disconnect");
        await Task.WhenAll(peers.Skip(15).Select(x => x.Send(new { type = "stats", payload = ProbeClient.Stats })));
        await Until(async () => (await Devices())!.All(x => x.Stats is not null), "Surviving stats stream failed");
    }
    finally { foreach (var peer in peers) await peer.DisposeAsync(); }
    await Until(async () => (await Devices())!.Length == 0, "Parallel test leaked a device");
});

await Test("an unreachable interface falls back to another endpoint of the same Admin", async () =>
{
    int closedPort;
    using (var closed = new TcpListener(IPAddress.Loopback, 0))
    {
        closed.Start();
        closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
    }
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run = ConnectionSupervisor.RunAsync(_ => Task.FromResult(new List<DiscoveryAnnouncement>
    { new("127.0.0.1", closedPort, "fallback"), new("127.0.0.1", portNumber, "fallback") }),
        (server, token) => ClientTransport.RunAsync(server, new("clientHello", "fallback", "fallback", "test", "127.0.0.1"),
            async (socket, gate, sessionToken) =>
            {
                connected.TrySetResult();
                await Task.Delay(Timeout.Infinite, sessionToken);
            }, Log, token), Log, stop.Token, TimeSpan.FromMilliseconds(50));
    await connected.Task.WaitAsync(TimeSpan.FromSeconds(6));
    Assert((await Devices()) is [{ ComputerName: "fallback" }], "Fallback endpoint did not register");
    stop.Cancel();
    await run;
    await Until(async () => (await Devices())!.Length == 0, "Fallback connection leaked");
});

await Test("discovery cancellation completes without a dangling receive", async () =>
{
    using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var timer = Stopwatch.StartNew();
    try
    {
        await LanDiscovery.FindAsync(Log, stop.Token, [(IPEndPoint)responder.Client.LocalEndPoint!], TimeSpan.FromSeconds(5));
        throw new Exception("Discovery ignored cancellation");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    Assert(timer.ElapsedMilliseconds < 1000, "Cancellation waited for the response window");
});

await Test("WebSocket keepalive detects a silent network blackhole", async () =>
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var blackhole = false;
    var proxy = Task.Run(async () =>
    {
        using var incoming = await listener.AcceptTcpClientAsync(stop.Token);
        using var outgoing = new TcpClient();
        await outgoing.ConnectAsync(IPAddress.Loopback, portNumber, stop.Token);
        async Task Pump(Stream source, Stream destination)
        {
            var buffer = new byte[8192];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var count = await source.ReadAsync(buffer, stop.Token);
                    if (count == 0) break;
                    if (!Volatile.Read(ref blackhole)) await destination.WriteAsync(buffer.AsMemory(0, count), stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
        await Task.WhenAll(Pump(incoming.GetStream(), outgoing.GetStream()), Pump(outgoing.GetStream(), incoming.GetStream()));
    });
    await using var peer = new ProbeClient(proxyPort, "blackhole", "blackhole", Log);
    await peer.Ready;
    Volatile.Write(ref blackhole, true);
    await peer.Completion.WaitAsync(TimeSpan.FromSeconds(28));
    await Until(async () => (await Devices())!.Length == 0, "Server did not remove a silent dead connection", 4000);
    stop.Cancel();
    await proxy;
});

await ScreenSharingTests.RunAsync(Test, portNumber, Devices, Log);
await RemoteControlTests.RunAsync(Test, Log);

if (args.Contains("--live"))
{
    await Test("live LAN discovery and handshake against existing Admin", async () =>
    {
        List<DiscoveryAnnouncement> found = [];
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(args.Contains("--soak") ? 120 : 30));
        for (var i = 0; i < 5 && found.Count == 0; i++)
        {
            found = await LanDiscovery.FindAsync(Console.WriteLine, limit.Token);
            if (found.Count == 0) await Task.Delay(1500, limit.Token);
        }
        Assert(found.Count > 0, "No active Admin replied on this LAN");
        Exception? lastError = null;
        foreach (var server in found)
        {
            try
            {
                await ClientTransport.RunAsync(server,
                    new("clientHello", "communication-test-" + Guid.NewGuid().ToString("N"), "Communication test (temporary)", "test", "127.0.0.1"),
                    async (socket, gate, token) =>
                    {
                        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var receiver = Task.Run(async () =>
                        {
                            var buffer = new byte[65536];
                            try
                            {
                                while (!session.IsCancellationRequested)
                                {
                                    var frame = await socket.ReceiveAsync(buffer, session.Token);
                                    if (frame.MessageType == WebSocketMessageType.Close) throw new Exception("Live Admin closed the test session");
                                }
                            }
                            catch (OperationCanceledException) when (session.IsCancellationRequested) { }
                        });
                        try
                        {
                            var duration = args.Contains("--soak") ? TimeSpan.FromSeconds(75) : TimeSpan.FromMilliseconds(500);
                            var timer = Stopwatch.StartNew();
                            while (timer.Elapsed < duration)
                            {
                                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "stats", payload = ProbeClient.Stats }, Protocol.Json), WebSocketMessageType.Text, true, token);
                                await Task.Delay(500, token);
                                if (receiver.IsCompleted) await receiver;
                            }
                        }
                        finally { session.Cancel(); await receiver; }
                    }, Console.WriteLine, limit.Token);
                return;
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw new Exception("All live Admin endpoints failed", lastError);
    });
}
await app.StopAsync();
File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "results.txt"), results);
File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "test-network.log"), logs);
Console.WriteLine($"ALL {results.Count} TESTS PASSED");

sealed class ProbeClient : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket? socket;
    private SemaphoreSlim? gate;
    private bool disposed;
    public Task Ready => ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
    public Task Completion { get; }
    public Channel<string> Messages { get; } = Channel.CreateUnbounded<string>();
    public static SystemStats Stats => new(5, 25, 100, 200, "communication test", "127.0.0.1", null, 60);
    public ProbeClient(int port, string id, string name, Action<string> log, bool screenSupported = false, bool controlSupported = false)
    {
        Completion = Run();
        async Task Run()
        {
            try
            {
                await ClientTransport.RunAsync(new("127.0.0.1", port, "test"), new("clientHello", id, name, "test", "127.0.0.1", screenSupported, controlSupported),
                    async (ws, sendGate, token) =>
                    {
                        socket = ws;
                        gate = sendGate;
                        ready.TrySetResult();
                        var buffer = new byte[64 * 1024];
                        while (ws.State == WebSocketState.Open)
                        {
                            var frame = await ws.ReceiveAsync(buffer, token);
                            if (frame.MessageType == WebSocketMessageType.Close) break;
                            await Messages.Writer.WriteAsync(Encoding.UTF8.GetString(buffer, 0, frame.Count), token);
                        }
                    }, log, stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            { ready.TrySetException(ex); }
        }
    }
    public async Task Send(object value)
    {
        await Ready;
        await gate!.WaitAsync(stop.Token);
        try { await socket!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json), WebSocketMessageType.Text, true, stop.Token); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        socket?.Abort();
        await Completion;
        stop.Dispose();
    }
}
