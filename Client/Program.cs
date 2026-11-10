using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Shared;

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAdmin");
Directory.CreateDirectory(appData);
var logPath = Path.Combine(appData, "Client.log");
ClientLog.Path = logPath;
ClientLog.Write("Client started.");
var deviceIdPath = Path.Combine(appData, "device.id");
var deviceId = File.Exists(deviceIdPath) ? File.ReadAllText(deviceIdPath) : Guid.NewGuid().ToString("N");
File.WriteAllText(deviceIdPath, deviceId);
var stopping = new CancellationTokenSource();
using var mutex = new Mutex(true, "RemoteAdmin.Client", out var ownsMutex);
if (!ownsMutex) return;
_ = Task.Run(() => StartupRegistration(appData));
while (!stopping.IsCancellationRequested)
{
    try
    {
        ClientLog.Write("Sending discovery broadcast.");
        var servers = await DiscoverServers(stopping.Token);
        ClientLog.Write($"Discovery found {servers.Count} Admin instance(s).");
        await Task.WhenAll(servers.Select(async server =>
        {
            try { await Connect(server, deviceId, stopping.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ClientLog.Write($"Connection to {server.Host}:{server.Port} failed: {ex.Message}"); }
        }));
        await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { ClientLog.Write($"Main loop error: {ex}"); await Task.Delay(3000, stopping.Token); }
}

static async Task<List<DiscoveryAnnouncement>> DiscoverServers(CancellationToken token)
{
    using var udp = new UdpClient { EnableBroadcast = true };
    udp.Client.ReceiveTimeout = 700;
    var request = Encoding.UTF8.GetBytes(Protocol.DiscoveryRequest);
    await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, Protocol.DiscoveryPort));
    var result = new List<DiscoveryAnnouncement>();
    var until = DateTime.UtcNow.AddMilliseconds(1200);
    while (DateTime.UtcNow < until)
    {
        try
        {
            var receiveTask = udp.ReceiveAsync(token).AsTask();
            if (await Task.WhenAny(receiveTask, Task.Delay(250, token)) != receiveTask) break;
            var packet = await receiveTask;
            ClientLog.Write($"Received discovery response from {packet.RemoteEndPoint}.");
            var server = JsonSerializer.Deserialize<DiscoveryAnnouncement>(packet.Buffer, Protocol.Json);
            if (server is not null && result.All(x => x.InstanceId != server.InstanceId))
                result.Add(server with { Host = packet.RemoteEndPoint.Address.ToString() });
        }
        catch (SocketException ex) { ClientLog.Write($"Discovery socket error: {ex.Message}"); break; }
        catch (OperationCanceledException) { break; }
    }
    return result;
}

static async Task Connect(DiscoveryAnnouncement server, string deviceId, CancellationToken token)
{
    using var socket = new ClientWebSocket();
    ClientLog.Write($"Connecting to {server.Host}:{server.Port}.");
    await socket.ConnectAsync(new Uri($"ws://{server.Host}:{server.Port}/client"), token);
    var hello = new ClientHello("clientHello", deviceId, Environment.MachineName, Environment.UserName, GetLocalIp());
    await Send(socket, hello, token);
    var response = await Receive<ServerHello>(socket, token);
    if (response is null || !response.Accepted) return;
    ClientLog.Write($"Connected to Admin {server.InstanceId}.");
    using var terminal = new TerminalManager();
    var statsTask = Task.Run(async () => { while (socket.State == WebSocketState.Open && !token.IsCancellationRequested) { await Send(socket, new { type = "stats", payload = CollectStats() }, token); await Task.Delay(5000, token); } }, token);
    var inputBlocker = new InputBlocker();
    try
    {
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var message = await Receive<JsonElement>(socket, token);
            if (message.ValueKind == JsonValueKind.Undefined) break;
            await Handle(message, socket, terminal, inputBlocker, token);
        }
    }
    finally { inputBlocker.Dispose(); }
}

static async Task Handle(JsonElement message, ClientWebSocket socket, TerminalManager terminal, InputBlocker blocker, CancellationToken token)
{
    var type = message.TryGetProperty("type", out var t) ? t.GetString() : "";
    if (type == "terminalInput")
    {
        var data = message.GetProperty("payload").GetProperty("data").GetString() ?? "";
        await terminal.Write(data);
    }
    else if (type == "terminalStart") await terminal.Start(message.GetProperty("payload").Deserialize<TerminalStart>(Protocol.Json) ?? new("powershell", null), socket, token);
    else if (type == "terminalStop") terminal.Stop();
    else if (type == "controlStart") blocker.Enable();
    else if (type == "controlStop") blocker.Disable();
    else if (type == "controlInput") InputInjector.Apply(message.GetProperty("payload").Deserialize<InputEvent>(Protocol.Json)!);
    else if (type == "fileUpload")
    {
        var payload = message.GetProperty("payload");
        var path = payload.GetProperty("path").GetString() ?? "";
        var data = Convert.FromBase64String(payload.GetProperty("dataBase64").GetString() ?? "");
        await File.WriteAllBytesAsync(path, data, token);
        await Send(socket, new { type = "fileResult", payload = new { operation = "upload", path, success = true, message = "Uploaded" } }, token);
    }
    else if (type == "fileDownload")
    {
        var path = message.GetProperty("payload").GetProperty("path").GetString() ?? "";
        var data = await File.ReadAllBytesAsync(path, token);
        await Send(socket, new { type = "fileDownload", payload = new { path, dataBase64 = Convert.ToBase64String(data) } }, token);
    }
    else if (type == "fileDelete")
    {
        var path = message.GetProperty("payload").GetProperty("path").GetString() ?? "";
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
        await Send(socket, new { type = "fileResult", payload = new { operation = "delete", path, success = true, message = "Deleted" } }, token);
    }
    else if (type == "fileRename")
    {
        var payload = message.GetProperty("payload");
        var path = payload.GetProperty("path").GetString() ?? "";
        var newPath = payload.GetProperty("newPath").GetString() ?? "";
        if (Directory.Exists(path)) Directory.Move(path, newPath); else File.Move(path, newPath);
        await Send(socket, new { type = "fileResult", payload = new { operation = "rename", path = newPath, success = true, message = "Renamed" } }, token);
    }
    else if (type == "executeFile")
    {
        var payload = message.GetProperty("payload");
        var path = payload.GetProperty("path").GetString() ?? "";
        var arguments = payload.TryGetProperty("arguments", out var args) ? args.GetString() ?? "" : "";
        var psi = new ProcessStartInfo(path, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(psi);
        if (process is null) throw new InvalidOperationException("Unable to start file.");
        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        await Send(socket, new { type = "fileResult", payload = new { operation = "execute", path, success = process.ExitCode == 0, message = output + error, exitCode = process.ExitCode } }, token);
    }
}

static SystemStats CollectStats()
{
    var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
    var ip = GetLocalIp();
    var memory = NativeStats.GetMemory();
    return new SystemStats(NativeStats.GetCpuPercent(), memory.UsedPercent, drive.AvailableFreeSpace, drive.TotalSize, Environment.OSVersion.VersionString, ip, GetBattery(), (long)(Environment.TickCount64 / 1000));
}
static int? GetBattery() => null;
static string GetLocalIp() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
static async Task Send<T>(ClientWebSocket socket, T value, CancellationToken token) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json), WebSocketMessageType.Text, true, token);
static async Task<T?> Receive<T>(ClientWebSocket socket, CancellationToken token) { var buffer = new byte[256 * 1024]; var result = await socket.ReceiveAsync(buffer, token); return result.MessageType == WebSocketMessageType.Close ? default : JsonSerializer.Deserialize<T>(buffer.AsSpan(0, result.Count), Protocol.Json); }
static async Task StartupRegistration(string root)
{
    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
    key?.SetValue("RemoteAdminClient", Environment.ProcessPath);
    await Task.CompletedTask;
}

sealed class TerminalManager : IDisposable
{
    private Process? process;
    public async Task Start(TerminalStart start, ClientWebSocket socket, CancellationToken token)
    {
        Stop();
        var shell = string.Equals(start.Shell, "cmd", StringComparison.OrdinalIgnoreCase) ? "cmd.exe" : "powershell.exe";
        process = new Process { StartInfo = new ProcessStartInfo(shell) { WorkingDirectory = string.IsNullOrWhiteSpace(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.Start();
        _ = Pump(process.StandardOutput, socket, false, token);
        _ = Pump(process.StandardError, socket, true, token);
        await Task.CompletedTask;
    }
    public async Task Write(string data) { if (process is { HasExited: false }) await process.StandardInput.WriteAsync(data); }
    public void Stop() { try { if (process is { HasExited: false }) process.Kill(true); } catch { } process?.Dispose(); process = null; }
    private static async Task Pump(StreamReader reader, ClientWebSocket socket, bool error, CancellationToken token) { var buffer = new char[2048]; while (!token.IsCancellationRequested) { var count = await reader.ReadAsync(buffer); if (count == 0) break; var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "terminalOutput", payload = new TerminalOutput("", new string(buffer, 0, count), error, false, null) }, Protocol.Json); await socket.SendAsync(payload, WebSocketMessageType.Text, true, token); } }
    public void Dispose() => Stop();
}

sealed class InputBlocker : IDisposable
{
    private bool enabled;
    public void Enable() { enabled = true; }
    public void Disable() { enabled = false; }
    public void Dispose() => Disable();
}

static class InputInjector
{
    public static void Apply(InputEvent e) { }
}

static class ClientLog
{
    public static string Path { get; set; } = "Client.log";
    public static void Write(string message) => File.AppendAllText(Path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
}

static class NativeStats
{
    private static long previousIdle;
    private static long previousKernel;
    private static long previousUser;
    private static bool hasCpuSample;

    public static double GetCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleTicks = ToInt64(idle);
        var kernelTicks = ToInt64(kernel);
        var userTicks = ToInt64(user);
        if (!hasCpuSample)
        {
            previousIdle = idleTicks;
            previousKernel = kernelTicks;
            previousUser = userTicks;
            hasCpuSample = true;
            return 0;
        }
        var idleDelta = idleTicks - previousIdle;
        var totalDelta = (kernelTicks - previousKernel) + (userTicks - previousUser);
        previousIdle = idleTicks;
        previousKernel = kernelTicks;
        previousUser = userTicks;
        return totalDelta <= 0 ? 0 : Math.Clamp((1d - (double)idleDelta / totalDelta) * 100d, 0, 100);
    }

    public static (double UsedPercent, long TotalBytes, long AvailableBytes) GetMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0, 0);
        var used = status.TotalPhysicalMemory - status.AvailablePhysicalMemory;
        return (status.TotalPhysicalMemory == 0 ? 0 : (double)used / status.TotalPhysicalMemory * 100, (long)status.TotalPhysicalMemory, (long)status.AvailablePhysicalMemory);
    }

    private static long ToInt64(FileTime value) => ((long)value.High << 32) | value.Low;

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
