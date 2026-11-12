# Copilot project guide

## Project purpose

This repository contains a Windows 11 LAN remote-management prototype with exactly two intended runtime executables:

- `release-final\Admin.exe` — administrator-side LAN server and localhost dashboard.
- `release-final\Client.exe` — managed-laptop background client.

The executables are self-contained .NET 10 `win-x64` single-file builds. Git LFS tracks the two binaries so a clone followed by `git lfs pull` materializes them.

## Projects

- `RemoteAdmin.Shared` — shared protocol records and JSON serializer settings.
- `Admin` — ASP.NET Core/Kestrel server, UDP discovery responder, device registry, dashboard, and browser WebSocket relay.
- `Client` — hidden Windows-subsystem background process, UDP discovery client, WebSocket client, startup registration, terminal process handling, statistics collection, and file-operation handlers.

The solution file is `RemoteAdmin.slnx`.

## Network protocol

- UDP discovery request: `REMOTE_ADMIN_DISCOVER_V1`
- UDP discovery port: `38991`
- Client WebSocket port: `38992`
- Admin dashboard: `http://127.0.0.1:38990`
- Client discovery is broadcast-based and does not use hardcoded Admin IP addresses.
- Authentication, pairing, shared secrets, and trust verification were intentionally removed for this prototype. Any reachable LAN client can connect. Discovery uses interface-specific IPv4 broadcasts plus the universal broadcast.

The Admin responds to discovery with its LAN address, WebSocket port, and instance ID. The Client connects to every discovered Admin instance and reconnects after failures. The Admin only keeps active connections in memory; disconnected devices are not retained. Each Client stores notes in `%LOCALAPPDATA%\RemoteAdmin\system-notes.txt`.

## Runtime behavior

`Client.exe`:

- Has `OutputType=WinExe`, so it does not open a console window.
- Relocates itself to `%LOCALAPPDATA%\RemoteAdmin\Client.exe` when started from another directory and starts the relocated copy. The current development build preserves the original executable; the final build should enable removal after handoff.
- Starts remote terminal sessions in the logged-in user's profile directory by default.
- Starts one CMD terminal automatically for each Admin connection; the dashboard only provides terminal input and clearing.
- Restarts the CMD terminal when a device is selected in the dashboard.
- Uses a mutex to prevent duplicate local instances.
- Writes its device ID and logs under `%LOCALAPPDATA%\RemoteAdmin\`.
- Registers or updates `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RemoteAdminClient`.
- Uses only outbound UDP/TCP connections and never requests administrator privileges or configures firewall rules.
- Sends statistics approximately every five seconds.
- Discovery runs continuously while connected. Each Admin reconnects independently; alternate interfaces are connection fallbacks. /31 and /32 adapters are excluded from broadcast calculation, UDP port-unreachable errors do not abandon the response window, and malformed replies are ignored.
- WebSocket connection/greeting time out after ten seconds. Transport keepalive uses a ten-second interval and a ten-second Pong timeout. Session statistics tasks are cancelled and observed on disconnect.
- Communication logging is serialized and cannot break connections when the log file is locked.

`Admin.exe`:

- Opens the localhost dashboard automatically.
- Accepts client WebSocket connections and maintains device status.
- Ensures inbound LAN firewall rules for UDP 38991 and TCP 38992 on first run, requesting UAC approval when needed.
- Serves a dark dashboard with device list and terminal/session controls.
- Writes technical logs under `%LOCALAPPDATA%\RemoteAdmin\Admin\`.
- Uses DeviceHub to keep each socket, send gate and device snapshot in one connection entry. Atomic conditional removal prevents stale cleanup from removing a reconnect. Browser output failures are isolated from the client; browser input resolves the current connection after a reconnect. Disconnect reasons are logged.
- Verifies firewall rules reference the current executable path after Admin.exe is moved.
- Refreshes the dashboard device list every 500 ms so discovered clients appear with minimal UI delay.

## Build and publish

Requires the .NET 10 SDK:

```powershell
dotnet build RemoteAdmin.slnx
dotnet publish Admin\Admin.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None -o release\Admin
dotnet publish Client\Client.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None -o release\Client
Copy-Item release\Admin\Admin.exe release-final\Admin.exe -Force
Copy-Item release\Client\Client.exe release-final\Client.exe -Force
```

After changing either executable, commit the source and updated `release-final` binary. Git LFS must be installed before committing or pulling the binaries:

```powershell
git lfs install
git lfs pull
```

The current plug-and-play requirement is intentional: `Client.exe` is still one self-contained executable and is never elevated. It performs only outbound network operations. `Admin.exe` remains responsible for inbound firewall configuration because it is the LAN listener.

## Current implemented functionality

- LAN Admin discovery and reconnect.
- Active device list, names, status, and IP address.
- Client-hosted system notes editable from the Admin dashboard.
- Localhost dashboard.
- Live CPU and RAM statistics using Windows APIs.
- Disk capacity/free space, Windows version, IP address, and uptime reporting.
- CMD terminal process plumbing with streamed output.
- Basic file upload, download, delete, rename, and execution message handlers in the Client.
- Current-user startup registration.
- Hidden client console window.
- Browser-to-client WebSocket relay.

## Known limitations

This remains a prototype. Primary-display screen capture/streaming is implemented. Basic fullscreen mouse/keyboard control is implemented. Clipboard synchronization, local input blocking and a complete file-transfer dashboard are not finished. The dashboard contains controls for some future session features, but those controls must not be described as fully functional without implementing and testing their corresponding client handlers.

The terminal implementation is basic streamed standard I/O rather than a complete pseudo-terminal. CPU/RAM statistics are operational but should be tested on additional Windows 11 hardware.

Do not add a third runtime executable or an installer unless the project requirements explicitly change. Preserve standard-user execution and do not silently introduce a Windows service or elevated component.

## Development rules

- Keep changes surgical and Windows-focused.
- Prefer existing shared protocol records over duplicated JSON shapes.
- Preserve the two-executable output model.
- Do not commit credentials, local runtime state, `bin/`, `obj/`, or ordinary publish directories.
- Update this file when architecture, protocol ports, executable behavior, or build procedures change.

## Communication tests

Run `dotnet run --project tests/Communication.Tests -c Release` for isolated regression tests of discovery, independent reconnects, fallback addresses, cancellation, logging, concurrent device registration, browser disconnect isolation and silent network failures. Add `-- --live --soak` to discover an existing LAN Admin and maintain a temporary inert test client for 75 seconds. Tests do not start terminals or alter startup registration and are not a third distributed runtime executable.

## Live screen architecture

- `Client/DesktopScreenSharing.cs` manages the primary interactive desktop indicator and shows a 40-DIP red circular Stop button at the top-right. The indicator remains on top, has a stop symbol, hover text and an accessible button name, and does not take focus when shown. Its taskbar entry also makes the stop control discoverable. It is displayed before any screen pixels are captured. It closes on session lock, logoff or disconnect.
- `RemoteAdmin.Shared/ScreenShareSession.cs` owns each viewing session. There is no per-session consent dialog. A visible indicator lease is mandatory, checked before capture and before sending each frame. Local Stop, a hidden/minimized indicator, transport cancellation and capture failure end the session. Never add a path that captures without the visible local indicator.
- Frames use JPEG, quality 65, max 1280x720, max 30 fps, with each send awaited to prevent a backlog. Secure/locked desktops are unavailable. Capture is primary-display only; the Windows cursor is included at the matching scale. Audio is not implemented.
- `screenStart`, `screenStop`, `screenState` and `screenFrame` use shared records. DeviceHub generates the browser's request ID, rewrites start/stop requests and sends screen messages only to that subscribed browser. Viewer closure sends a matching stop; no stream automatically resumes after reconnecting.
- ClientHello and DeviceSnapshot include an optional screen capability flag; old clients default to unsupported. Updating both runtime executables is necessary for screen viewing. Existing non-screen protocol remains compatible.
- Client targets `net10.0-windows10.0.19041.0` with `UseWindowsForms=true`. Both outputs remain self-contained win-x64 single files. Admin embeds both dashboard HTML and `screen.js`.
- Communication tests include screen session lifecycle, unavailable/hidden indicator, stop ownership, browser isolation and old-client handling. Native stop-indicator and desktop JPEG capture were also checked with a local test harness; no screen frames were sent to remote computers in that test.

## Fullscreen remote control

- GPU capture includes the OS cursor. The compatibility path uses `Client/DesktopCursor.cs` to read GetCursorInfo, copy the cursor, apply its scaled hotspot and draw it into the resized desktop. Owned icon/bitmap handles are released on every frame. Hidden cursors and cursors outside the primary display are omitted.
- `RemoteAdmin.Shared/RemoteControlSession.cs` leases input only to the current visible screen request. It sets the local control indicator, serializes input and releases held input on stop, revoked/hidden screen, transport cancellation or missing heartbeats (2.5 seconds; browser sends every 500 ms). There is no target approval prompt for this unattended mode. Do not add a path that injects without the live visible screen lease.
- `Client/WindowsInputSink.cs` uses SendInput with normalized virtual-desktop mouse coordinates and physical keyboard scan codes, tracks only remotely held keys/buttons and releases those on teardown. It checks the interactive desktop and does not elevate or bypass UIPI/UAC. It does not log typed keys or block local input. Tests replace SendInput with an injected fake.
- `controlStart`, `controlStop`, `controlInput`, `controlHeartbeat`, `controlState` and the optional RemoteControlSupported capability use shared records. DeviceHub forces the browser's own request ID and requires an existing subscription/fullscreen declaration before forwarding input. Fullscreen enforcement in the browser is a UI/lifecycle condition, not authentication; the prototype's existing trust model is unchanged.
- Browser control works only in the fullscreen viewport. Its toolbar enables/stops control and exits fullscreen. Escape explicitly stops/exits; blur, hidden tabs, pagehide and fullscreenchange release control. The backend watchdog handles lost stop packets. Cleanup tracks started sessions until stop messages are sent so page reload cancellation cannot strand a screen lease.
- `tests/Communication.Tests` covers the network and session ownership, visible lease, heartbeat, invalid input and cleanup. `tests/WindowsInput.Tests` validates native x64 structure/scan-code/mouse flags and draws a Windows arrow onto a synthetic bitmap without moving, clicking or typing on the real desktop. `node tests/ScreenView.Tests.cjs` checks letterboxing and wheel units.

- Client detects local Admin addresses and does not advertise or accept control for them, preventing same-machine input feedback. Self-view remains available.

## 30 FPS capture

The target is `Protocol.ScreenFramesPerSecond = 30`. ScreenShareSession uses a PeriodicTimer so coarse Windows timer jitter does not accumulate; capture/send remain sequential and missed ticks coalesce. SharingLifetime cancels active capture before disposing its visible indicator, including failure paths. `GpuFrameCapture` uses Windows Graphics Capture/D3D11 with cursor and OS border enabled, at most one latest native frame and one encoded image, and cancels per viewing token. Do not disable the OS capture border or fall back after permission errors. `GdiFrameCapture` reuses a pre-scaled 32-bit DIB, encoder settings and stream for hardware/API compatibility; cursor scaling preserves hotspots. `DesktopFrameCapture` selects the supported path and releases per-view GPU resources. Dashboard FPS is measured from incoming frames rather than a fixed label. Windows SDK/WinRT assemblies are bundled in the same single Client.exe.
