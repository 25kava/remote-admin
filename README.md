# Remote Admin Prototype

Two Windows 11 executables:

- `Admin.exe` - localhost dashboard and LAN server.
- `Client.exe` - background client that discovers Admin instances and reconnects automatically.

## Build

Requires the .NET 10 SDK:

```powershell
dotnet build RemoteAdmin.slnx
dotnet publish Admin\Admin.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o release\Admin
dotnet publish Client\Client.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o release\Client
```

The repository tracks the final executables with Git LFS, so cloning or pulling the repository materializes `release-final\Admin.exe` and `release-final\Client.exe`. Git LFS must be installed and enabled on the machine performing the clone.

This prototype intentionally has no login, pairing, trust verification, or client authentication. Any client that can reach the Admin LAN port can connect. Discovery sends to both interface-specific IPv4 broadcasts and the universal broadcast to work across more LAN configurations.

The Admin only lists currently connected clients; disconnected clients are not retained. Each client stores its system notes in `%LOCALAPPDATA%\RemoteAdmin\system-notes.txt`, and the Admin dashboard can read and update those notes.

The published `Admin.exe` performs its inbound firewall setup automatically on first run and may show one UAC prompt. `Client.exe` only makes outbound UDP/TCP connections, so it does not configure firewall rules, request elevation, or require administrator privileges.

### Client discovery reliability fix

The client remains a single self-contained, plug-and-play executable. Its discovery socket now waits for the complete response window instead of stopping after a fixed 250 ms with no packet. This prevents normal LAN latency, Wi-Fi broadcast delays, and Windows firewall inspection from causing a valid Admin response to be discarded.

The Admin also now protects its connection registry from stale WebSocket cleanup. If a client reconnects while its previous connection is still shutting down, the old connection can no longer remove the replacement connection from the device list. The dashboard polls for device changes every 500 ms so a successfully discovered client is shown almost immediately.

The client does not configure inbound or outbound firewall rules and never starts an elevated process. Only the Admin requires inbound firewall access because it listens for discovery and client WebSocket connections.

When `Client.exe` is started from outside `%LOCALAPPDATA%\RemoteAdmin`, it copies itself to `%LOCALAPPDATA%\RemoteAdmin\Client.exe` and starts that copy. The current development build preserves the original executable; the final build should remove it after handoff.

Remote terminal sessions start in the logged-in user's profile directory by default (`%USERPROFILE%`).
The client starts one CMD session automatically for each Admin connection; the dashboard does not expose start or stop controls.
Selecting a device in the dashboard restarts its CMD session so a previous `exit` command cannot permanently remove the terminal.
