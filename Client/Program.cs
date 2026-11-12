using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RemoteAdmin.Guard;
using RemoteAdmin.Shared;

var appData = GuardPaths.LocalRoot;
Directory.CreateDirectory(appData);
ClientLog.Path = Path.Combine(appData, "Client.log");

using var instance = SingleInstance.Acquire(GuardPaths.C1MutexName);
if (!instance.Owned) return;

ClientLog.Write("c1 started.");
var deviceIdPath = Path.Combine(appData, "device.id");
var deviceId = File.Exists(deviceIdPath) ? File.ReadAllText(deviceIdPath) : Guid.NewGuid().ToString("N");
File.WriteAllText(deviceIdPath, deviceId);
var notesPath = Path.Combine(appData, "system-notes.txt");
if (!File.Exists(notesPath)) File.WriteAllText(notesPath, "");
var stopping = new CancellationTokenSource();
await using var guard = new GuardSupervisor(GuardRole.C1, ClientLog.Write, stopping);
guard.Start();
await ConnectionSupervisor.RunAsync(
    token => LanDiscovery.FindAsync(ClientLog.Write, token),
    (server, token) => Connect(server, deviceId, notesPath, token),
    ClientLog.Write, stopping.Token);

static Task Connect(DiscoveryAnnouncement server, string deviceId, string notesPath, CancellationToken token)
{
    var canControl = !LanDiscovery.IsLocalAddress(server.Host);
    var hello = new ClientHello("clientHello", deviceId, Environment.MachineName, Environment.UserName, GetLocalIp(), true, canControl);
    return ClientTransport.RunAsync(server, hello,
        (socket, gate, sessionToken) => RunSession(socket, gate, notesPath, canControl, sessionToken), ClientLog.Write, token);
}

static async Task RunSession(ClientWebSocket socket, SemaphoreSlim sendGate, string notesPath, bool canControl, CancellationToken token)
{
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    token = lifetime.Token;
    using var terminal = new TerminalManager(socket, sendGate);
    using var capture = new DesktopFrameCapture(ClientLog.Write);
    await using var screen = new ScreenShareSession(
        capture.CaptureAsync,
        (state, sendToken) => Send(socket, new { type = "screenState", payload = state }, sendGate, sendToken),
        (frame, sendToken) => Send(socket, new { type = "screenFrame", payload = frame }, sendGate, sendToken),
        ClientLog.Write);
    using var localInputBlocker = new WindowsInputBlocker();
    await using var control = new RemoteControlSession(screen, new WindowsInputSink(),
        (state, sendToken) => Send(socket, new { type = "controlState", payload = state }, sendGate, sendToken), ClientLog.Write,
        localInputBlocker: localInputBlocker);
    Task statsTask = Task.CompletedTask;
    try
    {
        await terminal.Start(new("cmd", null), token);
        await Send(socket, new { type = "notesState", payload = new ClientNotes(ReadNotes(notesPath)) }, sendGate, token);
        statsTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Send(socket, new { type = "stats", payload = CollectStats() }, sendGate, token);
                    await Task.Delay(5000, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                ClientLog.Write($"Statistics connection failed: {ex.Message}");
                lifetime.Cancel();
                socket.Abort();
            }
        }, token);
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var message = await Receive<JsonElement>(socket, token);
            if (message.ValueKind == JsonValueKind.Undefined) break;
            await Handle(message, socket, terminal, screen, control, canControl, sendGate, notesPath, token);
        }
    }
    finally
    {
        await lifetime.CancelAsync();
        socket.Abort();
        try { await statsTask; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

static async Task Handle(JsonElement message, ClientWebSocket socket, TerminalManager terminal, ScreenShareSession screen, RemoteControlSession control, bool canControl, SemaphoreSlim sendGate, string notesPath, CancellationToken token)
{
    try
    {
        var type = message.TryGetProperty("type", out var t) ? t.GetString() : "";
        if (type == "screenStart")
        {
            var request = message.GetProperty("payload").Deserialize<ScreenStart>(Protocol.Json)
                ?? throw new ArgumentException("Missing screen request.");
            await screen.StartAsync(request, token);
        }
        else if (type == "screenStop")
        {
            var request = message.GetProperty("payload").Deserialize<ScreenStop>(Protocol.Json)
                ?? throw new ArgumentException("Missing screen request.");
            await control.StopAsync(request.RequestId);
            await screen.StopAsync(request.RequestId, token);
        }
        else if (type == "terminalInput")
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
        else if (type == "controlStart")
        {
            var request = message.GetProperty("payload").Deserialize<ControlRequest>(Protocol.Json)
                ?? throw new ArgumentException("Missing control request.");
            if (canControl) await control.StartAsync(request, token);
            else await Send(socket, new { type = "controlState", payload = new ControlState(request.RequestId, "unavailable", "Remote control of the Admin computer itself is disabled to prevent input feedback.") }, sendGate, token);
        }
        else if (type == "controlStop")
        {
            var request = message.GetProperty("payload").Deserialize<ControlRequest>(Protocol.Json)
                ?? throw new ArgumentException("Missing control request.");
            await control.StopAsync(request.RequestId);
        }
        else if (type == "controlHeartbeat")
        {
            var request = message.GetProperty("payload").Deserialize<ControlRequest>(Protocol.Json);
            if (request is not null) control.Heartbeat(request.RequestId);
        }
        else if (type == "controlInput")
        {
            var request = message.GetProperty("payload").Deserialize<ControlInput>(Protocol.Json);
            if (canControl && request?.Input is not null) control.Apply(request);
        }
        else if (type == "controlBlock")
        {
            var request = message.GetProperty("payload").Deserialize<ControlBlock>(Protocol.Json)
                ?? throw new ArgumentException("Missing control block request.");
            var state = canControl
                ? control.SetBlocked(request)
                : new ControlBlockState(request.RequestId, false, "Remote control of the Admin computer itself is disabled.");
            await Send(socket, new { type = "controlBlockState", payload = state }, sendGate, token);
        }
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
        catch (WebSocketException) { }
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
        catch (WebSocketException) { }
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

static class ClientLog
{
    private static CommunicationLog log = new("Client.log");
    public static string Path { set => log = new CommunicationLog(value); }
    public static void Write(string message) => log.Write(message);
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
