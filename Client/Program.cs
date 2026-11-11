using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Shared;

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteAdmin");
Directory.CreateDirectory(appData);
var logPath = Path.Combine(appData, "Client.log");
ClientLog.Path = logPath;
var removeOriginalAfterRelocation = false;
var relocation = RelocationInfo.Parse(args);
if (relocation is not null)
    WaitForParentExit(relocation.ParentProcessId);

using var mutex = new Mutex(false, "RemoteAdmin.Client");
var ownsMutex = false;
try
{
    ownsMutex = mutex.WaitOne(0);
}
catch (AbandonedMutexException)
{
    ownsMutex = true;
}

if (!ownsMutex) return;

if (relocation is not null)
{
    if (removeOriginalAfterRelocation)
    {
        var originalRemoved = TryDeleteOriginal(relocation.SourcePath);
        if (!originalRemoved)
            ClientLog.Write($"Unable to remove the original client executable: {relocation.SourcePath}");
    }
}
else if (!IsUnderDirectory(Environment.ProcessPath, appData))
{
    var targetPath = Path.Combine(appData, "Client.exe");
    File.Copy(Environment.ProcessPath!, targetPath, true);
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = targetPath,
        Arguments = $"--relocated-from \"{Environment.ProcessPath}\" --parent-pid {Environment.ProcessId}",
        UseShellExecute = false,
        CreateNoWindow = true
    });

    if (child is null)
        throw new InvalidOperationException("Unable to start the relocated client.");

    mutex.ReleaseMutex();
    return;
}

ClientLog.Write("Client started.");
var deviceIdPath = Path.Combine(appData, "device.id");
var deviceId = File.Exists(deviceIdPath) ? File.ReadAllText(deviceIdPath) : Guid.NewGuid().ToString("N");
File.WriteAllText(deviceIdPath, deviceId);
var notesPath = Path.Combine(appData, "system-notes.txt");
if (!File.Exists(notesPath)) File.WriteAllText(notesPath, "");
var stopping = new CancellationTokenSource();
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
            try { await Connect(server, deviceId, notesPath, stopping.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ClientLog.Write($"Connection to {server.Host}:{server.Port} failed: {ex.Message}"); }
        }));
        await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { ClientLog.Write($"Main loop error: {ex}"); await Task.Delay(3000, stopping.Token); }
}

static bool IsUnderDirectory(string? filePath, string directory)
{
    if (string.IsNullOrWhiteSpace(filePath)) return false;
    var file = Path.GetFullPath(filePath);
    var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return file.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}

static void WaitForParentExit(int processId)
{
    try
    {
        using var parent = Process.GetProcessById(processId);
        parent.WaitForExit(TimeSpan.FromSeconds(10));
    }
    catch (ArgumentException) { }
    catch (InvalidOperationException) { }
}

static bool TryDeleteOriginal(string path)
{
    try
    {
        if (!File.Exists(path)) return true;
        File.Delete(path);
        return !File.Exists(path);
    }
    catch
    {
        return false;
    }
}

static async Task<List<DiscoveryAnnouncement>> DiscoverServers(CancellationToken token)
{
    using var udp = new UdpClient { EnableBroadcast = true };
    udp.Client.ReceiveTimeout = 700;
    var request = Encoding.UTF8.GetBytes(Protocol.DiscoveryRequest);
    var broadcasts = GetBroadcastAddresses().Append(IPAddress.Broadcast).Distinct().ToList();
    foreach (var broadcast in broadcasts)
    {
        try
        {
            await udp.SendAsync(request, request.Length, new IPEndPoint(broadcast, Protocol.DiscoveryPort));
            ClientLog.Write($"Discovery broadcast sent to {broadcast}.");
        }
        catch (SocketException ex)
        {
            ClientLog.Write($"Discovery broadcast to {broadcast} failed: {ex.Message}");
        }
    }
    var result = new List<DiscoveryAnnouncement>();
    var until = DateTime.UtcNow.AddMilliseconds(1200);
    while (DateTime.UtcNow < until)
    {
        try
        {
            var receiveTask = udp.ReceiveAsync(token).AsTask();
            var remaining = until - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (await Task.WhenAny(receiveTask, Task.Delay(remaining, token)) != receiveTask) break;
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

static async Task Connect(DiscoveryAnnouncement server, string deviceId, string notesPath, CancellationToken token)
{
    using var socket = new ClientWebSocket();
    using var sendGate = new SemaphoreSlim(1, 1);
    ClientLog.Write($"Connecting to {server.Host}:{server.Port}.");
    await socket.ConnectAsync(new Uri($"ws://{server.Host}:{server.Port}/client"), token);
    var hello = new ClientHello("clientHello", deviceId, Environment.MachineName, Environment.UserName, GetLocalIp());
    await Send(socket, hello, sendGate, token);
    var response = await Receive<ServerHello>(socket, token);
    if (response is null) return;
    ClientLog.Write($"Connected to Admin {server.InstanceId}.");
    using var terminal = new TerminalManager(socket, sendGate);
    await terminal.Start(new("cmd", null), token);
    await Send(socket, new { type = "notesState", payload = new ClientNotes(ReadNotes(notesPath)) }, sendGate, token);
    var statsTask = Task.Run(async () => { while (socket.State == WebSocketState.Open && !token.IsCancellationRequested) { await Send(socket, new { type = "stats", payload = CollectStats() }, sendGate, token); await Task.Delay(5000, token); } }, token);
    var inputBlocker = new InputBlocker();
    try
    {
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var message = await Receive<JsonElement>(socket, token);
            if (message.ValueKind == JsonValueKind.Undefined) break;
            await Handle(message, socket, terminal, inputBlocker, sendGate, notesPath, token);
        }
    }
    finally { inputBlocker.Dispose(); }
}

static async Task Handle(JsonElement message, ClientWebSocket socket, TerminalManager terminal, InputBlocker blocker, SemaphoreSlim sendGate, string notesPath, CancellationToken token)
{
    try
    {
        var type = message.TryGetProperty("type", out var t) ? t.GetString() : "";
        if (type == "terminalInput")
        {
            var data = message.GetProperty("payload").GetProperty("data").GetString() ?? "";
            await terminal.Write(data);
        }
        else if (type == "terminalRestart")
            await terminal.Start(new("cmd", null), token);
        else if (type == "notesGet")
            await Send(socket, new { type = "notesState", payload = new ClientNotes(ReadNotes(notesPath)) }, sendGate, token);
        else if (type == "notesSet")
        {
            var text = message.GetProperty("payload").GetProperty("text").GetString() ?? "";
            WriteNotes(notesPath, text);
            await Send(socket, new { type = "notesState", payload = new ClientNotes(text) }, sendGate, token);
        }
        else if (type == "controlStart") blocker.Enable();
        else if (type == "controlStop") blocker.Disable();
        else if (type == "controlInput") InputInjector.Apply(message.GetProperty("payload").Deserialize<InputEvent>(Protocol.Json)!);
        else if (type == "fileUpload")
        {
            var payload = message.GetProperty("payload");
            var path = RequirePath(payload, "path");
            var data = Convert.FromBase64String(payload.GetProperty("dataBase64").GetString() ?? "");
            await File.WriteAllBytesAsync(path, data, token);
            await Send(socket, new { type = "fileResult", payload = new { operation = "upload", path, success = true, message = "Uploaded" } }, sendGate, token);
        }
        else if (type == "fileDownload")
        {
            var path = RequirePath(message.GetProperty("payload"), "path");
            var data = await File.ReadAllBytesAsync(path, token);
            await Send(socket, new { type = "fileDownload", payload = new { path, dataBase64 = Convert.ToBase64String(data) } }, sendGate, token);
        }
        else if (type == "fileDelete")
        {
            var path = RequirePath(message.GetProperty("payload"), "path");
            if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
            await Send(socket, new { type = "fileResult", payload = new { operation = "delete", path, success = true, message = "Deleted" } }, sendGate, token);
        }
        else if (type == "fileRename")
        {
            var payload = message.GetProperty("payload");
            var path = RequirePath(payload, "path");
            var newPath = RequirePath(payload, "newPath");
            if (Directory.Exists(path)) Directory.Move(path, newPath); else File.Move(path, newPath);
            await Send(socket, new { type = "fileResult", payload = new { operation = "rename", path = newPath, success = true, message = "Renamed" } }, sendGate, token);
        }
        else if (type == "executeFile")
        {
            var payload = message.GetProperty("payload");
            var path = RequirePath(payload, "path");
            var arguments = payload.TryGetProperty("arguments", out var args) ? args.GetString() ?? "" : "";
            var psi = new ProcessStartInfo(path, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start file.");
            var output = await process.StandardOutput.ReadToEndAsync(token);
            var error = await process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await Send(socket, new { type = "fileResult", payload = new { operation = "execute", path, success = process.ExitCode == 0, message = output + error, exitCode = process.ExitCode } }, sendGate, token);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException or JsonException)
    {
        ClientLog.Write($"Request '{message.GetProperty("type").GetString()}' failed: {ex.Message}");
        await Send(socket, new { type = "requestError", error = ex.Message }, sendGate, token);
    }
}

static string RequirePath(JsonElement payload, string property)
{
    var path = payload.TryGetProperty(property, out var value) ? value.GetString() : null;
    if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"Missing path '{property}'.");
    return Path.GetFullPath(path);
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
static string ReadNotes(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
static void WriteNotes(string path, string text) => File.WriteAllText(path, text);
static IEnumerable<IPAddress> GetBroadcastAddresses()
{
    foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (network.OperationalStatus != OperationalStatus.Up ||
            network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            continue;

        foreach (var address in network.GetIPProperties().UnicastAddresses)
        {
            if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                continue;

            var ip = BitConverter.ToUInt32(address.Address.GetAddressBytes().Reverse().ToArray());
            var mask = BitConverter.ToUInt32(address.IPv4Mask.GetAddressBytes().Reverse().ToArray());
            var broadcast = (ip & mask) | ~mask;
            yield return new IPAddress(BitConverter.GetBytes(broadcast).Reverse().ToArray());
        }
    }
}
static async Task Send<T>(ClientWebSocket socket, T value, SemaphoreSlim sendGate, CancellationToken token)
{
    var data = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
    await sendGate.WaitAsync(token);
    try { await socket.SendAsync(data, WebSocketMessageType.Text, true, token); }
    finally { sendGate.Release(); }
}

static async Task<T?> Receive<T>(ClientWebSocket socket, CancellationToken token)
{
    var data = await ReceiveMessage(socket, token);
    return data is null ? default : JsonSerializer.Deserialize<T>(data, Protocol.Json);
}

static async Task<byte[]?> ReceiveMessage(WebSocket socket, CancellationToken token)
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
static async Task StartupRegistration(string root)
{
    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
    key?.SetValue("RemoteAdminClient", Environment.ProcessPath);
    await Task.CompletedTask;
}

sealed class TerminalManager : IDisposable
{
    private Process? process;
    private readonly ClientWebSocket socket;
    private readonly SemaphoreSlim sendGate;
    private string? sessionId;

    public TerminalManager(ClientWebSocket socket, SemaphoreSlim sendGate)
    {
        this.socket = socket;
        this.sendGate = sendGate;
    }

    public async Task Start(TerminalStart start, CancellationToken token)
    {
        Stop();
        const string shell = "cmd.exe";
        const string shellArguments = "/Q /K \"title RemoteAdmin\"";
        var workingDirectory = string.IsNullOrWhiteSpace(start.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(start.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        process = new Process { StartInfo = new ProcessStartInfo(shell, shellArguments) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        if (!process.Start()) throw new InvalidOperationException("Unable to start terminal process.");
        sessionId = Guid.NewGuid().ToString("N");
        await Send(new { type = "terminalStarted", payload = new { sessionId, shell, workingDirectory } }, token);
        _ = Pump(process.StandardOutput, false, token);
        _ = Pump(process.StandardError, true, token);
        _ = MonitorExit(process, token);
    }
    public async Task Write(string data)
    {
        if (process is not { HasExited: false }) throw new InvalidOperationException("No terminal session is running.");
        await process.StandardInput.WriteAsync(data);
        await process.StandardInput.FlushAsync();
    }
    public void Stop()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
        process?.Dispose();
        process = null;
        sessionId = null;
    }
    private async Task Pump(StreamReader reader, bool error, CancellationToken token)
    {
        var buffer = new char[2048];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer, token);
                if (count == 0) break;
                await Send(new { type = "terminalOutput", payload = new TerminalOutput(sessionId ?? "", new string(buffer, 0, count), error, false, null) }, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task MonitorExit(Process terminalProcess, CancellationToken token)
    {
        try
        {
            await terminalProcess.WaitForExitAsync(token);
            await Send(new { type = "terminalOutput", payload = new TerminalOutput(sessionId ?? "", "", false, true, terminalProcess.ExitCode) }, token);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }
    private Task Send<T>(T value, CancellationToken token) => ProgramSend(value, token);
    private async Task ProgramSend<T>(T value, CancellationToken token) => await SendMessage(socket, value, sendGate, token);
    public void Dispose() => Stop();

    private static async Task SendMessage<T>(ClientWebSocket socket, T value, SemaphoreSlim sendGate, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
        await sendGate.WaitAsync(token);
        try { await socket.SendAsync(data, WebSocketMessageType.Text, true, token); }
        finally { sendGate.Release(); }
    }
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

sealed record RelocationInfo(string SourcePath, int ParentProcessId)
{
    public static RelocationInfo? Parse(string[] args)
    {
        if (args.Length != 4 ||
            !string.Equals(args[0], "--relocated-from", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[2], "--parent-pid", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(args[3], out var parentProcessId) ||
            string.IsNullOrWhiteSpace(args[1]))
            return null;

        return new RelocationInfo(args[1], parentProcessId);
    }
}
