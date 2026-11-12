# Copilot project guide

## Project purpose

This repository contains a Windows 11 LAN remote-management prototype with three runtime payloads:

- `release-final\Admin.exe` — administrator-side LAN server and localhost dashboard.
- `release-final\c1.exe` — full managed-laptop background client.
- `release-final\c2.exe` — tiny two-way watchdog whose only job is to keep `c1.exe` alive.

The payloads are self-contained .NET 10 `win-x64` single-file builds. Git LFS tracks the three binaries so a clone followed by `git lfs pull` materializes them. Distribution uses `install.bat`; there is no longer a `Client.exe`.

## Projects

- `RemoteAdmin.Shared` — shared protocol records and JSON serializer settings.
- `RemoteAdmin.Guard` — shared installation paths, single-instance mutexes and the two-way watchdog supervisor used by both client payloads.
- `Admin` — ASP.NET Core/Kestrel server, UDP discovery responder, device registry, dashboard, and browser WebSocket relay.
- `Client` — `c1.exe`, the hidden Windows-subsystem client with UDP discovery, WebSocket client, terminal handling, statistics collection and file-operation handlers.
- `Watchdog` — `c2.exe`, a trimmed Windows-subsystem process with no networking and no admin features. It only restores or restarts `c1.exe`.
- `installer` — `install.bat` (the only installer) and `uninstall.bat` (removal only).

The solution file is `RemoteAdmin.slnx`.

## Network protocol

- UDP discovery request: `REMOTE_ADMIN_DISCOVER_V1`
- UDP discovery port: `38991`
- Client WebSocket port: `38992`
- Admin dashboard: `http://127.0.0.1:38990`
- Client discovery is broadcast-based and does not use hardcoded Admin IP addresses.
- Authentication, pairing, shared secrets, and trust verification were intentionally removed for this prototype. Any reachable LAN client can connect. Discovery uses interface-specific IPv4 broadcasts plus the universal broadcast.

The Admin responds to discovery with its LAN address, WebSocket port, and instance ID. Each client connects to every discovered Admin instance and reconnects after failures. The Admin only keeps active connections in memory; disconnected devices are not retained. Each client stores notes in `%LOCALAPPDATA%\RemoteAdmin\system-notes.txt`.

## Runtime behavior

`c1.exe`:

- Has `OutputType=WinExe`, so it does not open a console window.
- Is installed at `%LOCALAPPDATA%\RemoteAdmin\c1.exe` by `install.bat`; it no longer relocates itself.
- Runs the C1 side of `GuardSupervisor`: it watches `c2.exe`, restores it from `%LOCALAPPDATA%\RemoteAdmin\c2.bak` when the executable is missing, restarts it when the `RemoteAdmin.C2` mutex is absent, and keeps both per-user startup values present.
- Starts remote terminal sessions in the logged-in user's profile directory by default.
- Starts one CMD terminal automatically for each Admin connection; the dashboard only provides terminal input and clearing.
- Restarts the CMD terminal when a device is selected in the dashboard.
- Uses the `RemoteAdmin.C1` mutex to prevent duplicate local instances.
- Writes its device ID and logs under `%LOCALAPPDATA%\RemoteAdmin\`.
- Ensures `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RemoteAdminC1` and `RemoteAdminC2` exist and removes the legacy `RemoteAdminClient` value.
- Uses only outbound UDP/TCP connections and never requests administrator privileges or configures firewall rules.
- Sends statistics approximately every five seconds.
- Discovery runs continuously while connected. Each Admin reconnects independently; alternate interfaces are connection fallbacks. /31 and /32 adapters are excluded from broadcast calculation, UDP port-unreachable errors do not abandon the response window, and malformed replies are ignored.
- WebSocket connection/greeting time out after ten seconds. Transport keepalive uses a ten-second interval and a ten-second Pong timeout. Session statistics tasks are cancelled and observed on disconnect.
- Communication logging is serialized and cannot break connections when the log file is locked.

`c2.exe`:

- Has `OutputType=WinExe`, no console, no UI and no network code.
- Is installed at `%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard\c2.exe`; the folder and executable carry Hidden and System attributes.
- Uses the `RemoteAdmin.C2` mutex to prevent duplicate local instances.
- Runs the C2 side of `GuardSupervisor`: it watches `c1.exe`, restores it from `%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard\c1.bak`, restarts it when `RemoteAdmin.C1` is absent, and keeps both startup values present.
- Is published with `PublishTrimmed=true` and is roughly one tenth the size of `c1.exe`.
- Exits without restoring anything when the shared disabled flag or `HKCU\Software\RemoteAdminGuard\Disabled` is set by the installer.

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

Requires the .NET 10 SDK. `build.ps1` performs the build, publish and packaging steps:

```powershell
.\build.ps1
# Or reuse the existing Admin.exe:
.\build.ps1 -SkipAdmin
```

The equivalent manual commands are:

```powershell
dotnet build RemoteAdmin.slnx
dotnet publish Admin\Admin.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None -o release\Admin
dotnet publish Client\Client.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None -o release\c1
dotnet publish Watchdog\Watchdog.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None -o release\c2
Copy-Item release\Admin\Admin.exe release-final\Admin.exe -Force
Copy-Item release\c1\c1.exe release-final\c1.exe -Force
Copy-Item release\c2\c2.exe release-final\c2.exe -Force
Copy-Item installer\install.bat release-final\ -Force
```

After changing any payload or installer script, commit the source and the updated `release-final` files. Git LFS must be installed before committing or pulling the binaries:

```powershell
git lfs install
git lfs pull
```

The plug-and-play requirement is intentional: `c1.exe` and `c2.exe` are self-contained executables and are never elevated. `c1.exe` performs only outbound network operations; `c2.exe` performs no network operations. `Admin.exe` remains responsible for inbound firewall configuration because it is the LAN listener. The installer runs as the target user and requires no administrator rights.

## Watchdog layout and recovery

- Visible side: `%LOCALAPPDATA%\RemoteAdmin\c1.exe` and the hidden/system `c2.bak`.
- Hidden side: `%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard\c2.exe` and the hidden/system `c1.bak`.
- Each side keeps the other side's backup on its own side, so deleting one payload does not delete its recovery source.
- Liveness is detected through the named mutexes `RemoteAdmin.C1` and `RemoteAdmin.C2`; no process enumeration or elevation is needed.
- Every three seconds each supervisor: exits when disabled, ensures both directories and startup entries, repairs its partner's missing executable from the backup, refreshes its partner's backup, and starts a partner whose mutex is absent.
- `install.bat` sets the disabled flag first, stops old `Client.exe`/`c1.exe`/`c2.exe` processes, clears hidden attributes, replaces both payloads and backups, writes both startup values, clears the flag and starts both processes. It also deletes the legacy `Client.exe`.
- `uninstall.bat` is the supported removal path. It sets the shared disabled flag first so neither supervisor can restore its partner, stops both payloads in one pass (retrying twice to beat a supervisor tick), drops both `Run` values and the legacy `RemoteAdminClient` value, clears hidden/system attributes, deletes both install folders, and removes `HKCU\Software\RemoteAdminGuard`. Elevated runs also delete the two Admin firewall rules. If a payload file cannot be deleted it leaves the disabled flag set and exits non-zero instead of allowing a partial install to resurrect itself. `/quiet` skips the prompt; `/dry-run` prints the targets.
- Manual removal remains possible: stop both processes, remove both startup values, then delete `%LOCALAPPDATA%\RemoteAdmin`, `%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard` and `HKCU\Software\RemoteAdminGuard`. The installer deletes a legacy `uninstall.bat` left inside the install folder by earlier builds.

## Current implemented functionality

- LAN Admin discovery and reconnect.
- Active device list, names, status, and IP address.
- Client-hosted system notes editable from the Admin dashboard.
- Localhost dashboard.
- Live CPU and RAM statistics using Windows APIs.
- Disk capacity/free space, Windows version, IP address, and uptime reporting.
- CMD terminal process plumbing with streamed output.
- Basic file upload, download, delete, rename, and execution message handlers in `c1.exe`.
- Two-way c1/c2 watchdog with backup-based recovery and per-user startup registration.
- Complete per-user uninstaller that stops both watchdogs at once and removes payloads, backups, startup values and the guard registry key.
- Hidden client console window and hidden watchdog folder.
- Browser-to-client WebSocket relay.

## Known limitations

This remains a prototype. Primary-display screen capture/streaming is implemented. Basic fullscreen mouse/keyboard control and target-side input blocking are implemented. Clipboard synchronization and a complete file-transfer dashboard are not finished. The dashboard contains controls for some future session features, but those controls must not be described as fully functional without implementing and testing their corresponding client handlers.

The terminal implementation is basic streamed standard I/O rather than a complete pseudo-terminal. CPU/RAM statistics are operational but should be tested on additional Windows 11 hardware.

Stopping both client payloads at the same time leaves neither watchdog running until the next logon. The per-user startup entries restore the pair after logoff or reboot.

Preserve the Admin + `c1.exe` + `c2.exe` payload model and the shared layout in `RemoteAdmin.Guard`. `install.bat` is the only installer and `uninstall.bat` only removes; do not reintroduce a standalone `Client.exe`. Preserve standard-user execution and do not silently introduce a Windows service or elevated component.

## Development rules

- Keep changes surgical and Windows-focused.
- Prefer existing shared protocol records over duplicated JSON shapes.
- Preserve the Admin + c1 + c2 payload model and keep the two watchdogs symmetrical.
- Keep `install.bat` as the only installer. `uninstall.bat` may only remove; it must never install, repair or restore anything.
- Do not commit credentials, local runtime state, `bin/`, `obj/`, or ordinary publish directories.
- Update this file when architecture, protocol ports, executable behavior, or build procedures change.

## Communication tests

Run `dotnet run --project tests/Communication.Tests -c Release` for isolated regression tests of discovery, independent reconnects, fallback addresses, cancellation, logging, concurrent device registration, browser disconnect isolation and silent network failures. Add `-- --live --soak` to discover an existing LAN Admin and maintain a temporary inert test client for 75 seconds. Tests do not start terminals or alter startup registration and are not part of the distributed payload set.

## Live screen architecture

- `Client/DesktopAccess.cs` provides the interactive-desktop check used by capture and input. There is no target-side sharing window, indicator or stop button; screen sharing and remote control are silent on the target.
- `RemoteAdmin.Shared/ScreenShareSession.cs` owns each viewing session. There is no per-session consent dialog and no visible target UI. The Admin viewer owns the lifetime. Stopping the viewer, replacing the screen request, transport cancellation and capture failure end the session. Do not add a target-side indicator or stop button.
- Frames use JPEG, quality 65, max 1280x720, max 30 fps, with each send awaited to prevent a backlog. Secure/locked desktops are unavailable. Capture is primary-display only; the Windows cursor is included at the matching scale. Audio is not implemented.
- `screenStart`, `screenStop`, `screenState` and `screenFrame` use shared records. DeviceHub generates the browser's request ID, rewrites start/stop requests and sends screen messages only to that subscribed browser. Viewer closure sends a matching stop; no stream automatically resumes after reconnecting.
- ClientHello and DeviceSnapshot include an optional screen capability flag; old clients default to unsupported. Updating `Admin.exe` and `c1.exe` is necessary for screen viewing. Existing non-screen protocol remains compatible.
- `c1.exe` targets `net10.0-windows10.0.22000.0`. All three payloads remain self-contained win-x64 single files. Admin embeds both dashboard HTML and `screen.js`.
- Communication tests include screen session lifecycle, cancellation, viewer isolation, pacing/backpressure and old-client handling. Desktop JPEG capture was also checked with a local test harness; no screen frames were sent to remote computers in that test.

## Fullscreen remote control

- GPU capture includes the OS cursor. The compatibility path uses `Client/DesktopCursor.cs` to read GetCursorInfo, copy the cursor, apply its scaled hotspot and draw it into the resized desktop. Owned icon/bitmap handles are released on every frame. Hidden cursors and cursors outside the primary display are omitted.
- `RemoteAdmin.Shared/RemoteControlSession.cs` leases input only to the current screen request. It serializes input and releases held input on stop, revoked screen, transport cancellation or missing heartbeats (2.5 seconds; browser sends every 500 ms). It can also block the target's physical mouse and keyboard through `ILocalInputBlocker`; the block is bound to the active control request and released on any control teardown. There is no target approval prompt and no target-side control indicator. Do not add a path that injects without the live screen lease.
- `Client/WindowsInputSink.cs` uses SendInput with normalized virtual-desktop mouse coordinates and physical keyboard scan codes, tracks only remotely held keys/buttons and releases those on teardown. It checks the interactive desktop and does not elevate or bypass UIPI/UAC. It does not log typed keys.
- `Client/WindowsInputBlocker.cs` runs WH_KEYBOARD_LL and WH_MOUSE_LL hooks on a dedicated message-loop thread. It swallows only non-injected events, so remote SendInput continues to work. It cannot intercept Ctrl+Alt+Del or secure-desktop input, does not elevate, and unblocks on control teardown. Tests use a fake blocker plus flag-filter assertions.
- `controlStart`, `controlStop`, `controlInput`, `controlHeartbeat`, `controlBlock`, `controlState`, `controlBlockState` and the optional RemoteControlSupported capability use shared records. DeviceHub forces the browser's own request ID and requires an existing subscription/fullscreen declaration and active control before forwarding block or input messages. Fullscreen enforcement in the browser is a UI/lifecycle condition, not authentication; the prototype's existing trust model is unchanged.
- Browser control works only in the fullscreen viewport. Its toolbar enables/stops control, exposes the **Block inputs** toggle only while control is active, and exits fullscreen. Escape explicitly stops/exits; blur, hidden tabs, pagehide and fullscreenchange release control. The backend watchdog handles lost stop packets. Cleanup tracks started sessions until stop messages are sent so page reload cancellation cannot strand a screen lease.
- `tests/Communication.Tests` covers the network and session ownership, heartbeat, input blocking lifecycle, invalid input and cleanup. `tests/WindowsInput.Tests` validates native x64 structure/scan-code/mouse flags, the injected-event filter and draws a Windows arrow onto a synthetic bitmap without moving, clicking or typing on the real desktop. `node tests/ScreenView.Tests.cjs` checks letterboxing and wheel units.

- `c1.exe` detects local Admin addresses and does not advertise or accept control for them, preventing same-machine input feedback. Self-view remains available.

## 30 FPS capture

The target is `Protocol.ScreenFramesPerSecond = 30`. ScreenShareSession uses a PeriodicTimer so coarse Windows timer jitter does not accumulate; capture/send remain sequential and missed ticks coalesce. SharingLifetime cancels active capture when the session ends, including failure paths. `GpuFrameCapture` uses Windows Graphics Capture/D3D11 with the cursor enabled and the OS capture border disabled (`IsBorderRequired = false`) so the target never shows a yellow outline. It keeps at most one latest native frame and one encoded image, and cancels per viewing token. Do not re-enable the capture border. `GdiFrameCapture` reuses a pre-scaled 32-bit DIB, encoder settings and stream for hardware/API compatibility; cursor scaling preserves hotspots. `DesktopFrameCapture` selects the supported path and releases per-view GPU resources. Dashboard FPS is measured from incoming frames rather than a fixed label. Windows SDK/WinRT assemblies are bundled in the same single c1.exe.
