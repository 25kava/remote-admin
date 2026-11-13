using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RemoteAdmin;
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
var auth = new AdminAuth(Log);
var hub = new DeviceHub(Log, auth);
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(10),
    KeepAliveTimeout = TimeSpan.FromSeconds(10)
});
AdminWeb.UseAuthentication(app, auth);
AdminWeb.MapAssets(app);
AdminWeb.MapAuthentication(app, auth, hub);
hub.MapEndpoints(app);

var discoveryTask = Task.Run(() => DiscoveryLoop(app.Lifetime.ApplicationStopping));
var sessionPruneTask = Task.Run(() => SessionPruneLoop(app.Lifetime.ApplicationStopping));

Console.WriteLine("Admin dashboard: http://127.0.0.1:38990");
_ = Process.Start(new ProcessStartInfo("http://127.0.0.1:38990") { UseShellExecute = true });
await app.RunAsync();

async Task DiscoveryLoop(CancellationToken stop)
{
    using var udp = new UdpClient(Protocol.DiscoveryPort) { EnableBroadcast = true };
    var wasSignedIn = auth.IsAnySignedIn;
    while (!stop.IsCancellationRequested)
    {
        try
        {
            var received = await udp.ReceiveAsync(stop);
            if (Encoding.UTF8.GetString(received.Buffer) == Protocol.DiscoveryRequest)
            {
                var signedIn = auth.IsAnySignedIn;
                if (wasSignedIn && !signedIn)
                {
                    Log("Admin dashboard is no longer signed in; disconnecting updated clients.");
                    hub.DisconnectSignedInClients();
                }
                wasSignedIn = signedIn;
                Log($"Discovery request from {received.RemoteEndPoint.Address} (signed in: {signedIn}).");
                var response = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryAnnouncement(
                    GetLocalIp(), Protocol.ClientPort, instanceId, Protocol.CurrentProtocolVersion, signedIn), Protocol.Json);
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

async Task SessionPruneLoop(CancellationToken stop)
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            if (auth.PruneExpired()) hub.DisconnectSignedInClients();
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception ex) { Log($"Session cleanup error: {ex.Message}"); }
    }
}

static string GetLocalIp() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
