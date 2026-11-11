using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteAdmin.Shared;

public static class Protocol
{
    public const int DiscoveryPort = 38991;
    public const int ClientPort = 38992;
    public const string DiscoveryRequest = "REMOTE_ADMIN_DISCOVER_V1";
    public const string Product = "RemoteAdmin";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record DiscoveryAnnouncement(string Host, int Port, string InstanceId);

public sealed record ClientHello(
    string Type,
    string DeviceId,
    string ComputerName,
    string UserName,
    string IpAddress,
    bool ScreenStreamingSupported = false,
    bool RemoteControlSupported = false);

public sealed record ServerHello(string Type, string? Message);
public sealed record ClientNotes(string Text);

public sealed record DeviceSnapshot(
    string DeviceId,
    string ComputerName,
    string DisplayName,
    string Status,
    DateTime LastSeenUtc,
    string? IpAddress,
    SystemStats? Stats,
    bool ScreenStreamingSupported = false,
    bool RemoteControlSupported = false);

public sealed record SystemStats(
    double CpuPercent,
    double MemoryPercent,
    long DiskFreeBytes,
    long DiskTotalBytes,
    string WindowsVersion,
    string IpAddress,
    int? BatteryPercent,
    long UptimeSeconds);

public sealed record RpcRequest(string Type, string RequestId, JsonElement? Payload);
public sealed record RpcResponse(string Type, string RequestId, bool Success, JsonElement? Payload, string? Error);

public sealed record TerminalStart(string Shell, string? WorkingDirectory);
public sealed record TerminalInput(string SessionId, string Data);
public sealed record TerminalStop(string SessionId);
public sealed record TerminalOutput(string SessionId, string Data, bool IsError, bool IsExited, int? ExitCode);

public sealed record FileChunk(string TransferId, string RelativePath, long Offset, long TotalBytes, string DataBase64, bool IsFinal);
public sealed record FileOperation(string Operation, string Path, string? NewName, bool Recursive);
public sealed record ExecuteFile(string Path, string Arguments);

public sealed record InputEvent(string Kind, double X, double Y, int Button, int Delta, string? Key);
public sealed record ClipboardMessage(string Text, string Origin);
public sealed record SessionMode(bool ScreenViewing, bool RemoteControl, bool BlockLocalInput);

public sealed record ScreenStart(string RequestId);
public sealed record ScreenStop(string RequestId);
public sealed record ScreenShareState(string RequestId, string Status, string? Message = null);
public sealed record ScreenFrame(string RequestId, int Width, int Height, string JpegBase64, long Sequence, DateTime CapturedAtUtc);
public sealed record ControlRequest(string RequestId);
public sealed record ControlInput(string RequestId, InputEvent Input);
public sealed record ControlState(string RequestId, string Status, string? Message = null);
