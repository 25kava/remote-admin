using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Shared;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAdmin", "Admin");
Directory.CreateDirectory(dataDir);
var logPath = Path.Combine(dataDir, "Admin.log");
var communicationLog = new CommunicationLog(logPath);
void Log(string message) => communicationLog.Write(message);
if (FirewallSetup.HandleElevatedCommand(args, Environment.ProcessPath!, Log))
    return;
FirewallSetup.EnsureAdmin(Environment.ProcessPath!, Log);
Log("Admin started.");
var instanceId = Guid.NewGuid().ToString("N");
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:38990", "http://0.0.0.0:" + Protocol.ClientPort);
builder.WebHost.UseKestrel();
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(10),
    KeepAliveTimeout = TimeSpan.FromSeconds(10)
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/", () => Results.Content(ReadAsset("index.html"), "text/html"));
app.MapGet("/screen.js", () => Results.Content(ReadAsset("screen.js"), "text/javascript"));

var discoveryTask = Task.Run(() => DiscoveryLoop(app.Lifetime.ApplicationStopping));
new DeviceHub(Log).MapEndpoints(app);

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
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception ex)
        {
            Log($"Discovery responder error: {ex.Message}");
            try { await Task.Delay(1000, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        }
    }
}

static string GetLocalIp() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
static string ReadAsset(string fileName)
{
    var resource = typeof(Program).Assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("wwwroot." + fileName, StringComparison.OrdinalIgnoreCase));
    if (resource is null) return "<h1>Dashboard resource missing</h1>";
    using var stream = typeof(Program).Assembly.GetManifestResourceStream(resource);
    using var reader = new StreamReader(stream!);
    return reader.ReadToEnd();
}
