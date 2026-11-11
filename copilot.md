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

The Admin responds to discovery with its LAN address, WebSocket port, and instance ID. The Client connects to every discovered Admin instance and reconnects after failures. Admin persists each device in `%LOCALAPPDATA%\RemoteAdmin\Admin\devices.json`.

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
- Sends statistics approximately every five seconds.

`Admin.exe`:

- Opens the localhost dashboard automatically.
- Accepts client WebSocket connections and maintains device status.
- Serves a dark dashboard with device list and terminal/session controls.
- Writes technical logs under `%LOCALAPPDATA%\RemoteAdmin\Admin\`.

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

## Current implemented functionality

- LAN Admin discovery and reconnect.
- Device registry, names, status, IP address, and persistence.
- Localhost dashboard.
- Live CPU and RAM statistics using Windows APIs.
- Disk capacity/free space, Windows version, IP address, and uptime reporting.
- CMD terminal process plumbing with streamed output.
- Basic file upload, download, delete, rename, and execution message handlers in the Client.
- Current-user startup registration.
- Hidden client console window.
- Browser-to-client WebSocket relay.

## Known limitations

This remains a prototype. Screen capture/streaming, real mouse and keyboard injection, clipboard synchronization, local input blocking, and a complete file-transfer dashboard are not finished. The dashboard contains controls for some future session features, but those controls must not be described as fully functional without implementing and testing their corresponding client handlers.

The terminal implementation is basic streamed standard I/O rather than a complete pseudo-terminal. CPU/RAM statistics are operational but should be tested on additional Windows 11 hardware.

Do not add a third runtime executable or an installer unless the project requirements explicitly change. Preserve standard-user execution and do not silently introduce a Windows service or elevated component.

## Development rules

- Keep changes surgical and Windows-focused.
- Prefer existing shared protocol records over duplicated JSON shapes.
- Preserve the two-executable output model.
- Do not commit credentials, local runtime state, `bin/`, `obj/`, or ordinary publish directories.
- Update this file when architecture, protocol ports, executable behavior, or build procedures change.
