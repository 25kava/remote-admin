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

### Communication reliability

Discovery runs continuously, including while connected to other Admin instances. Each Admin has an independent connection task, so one healthy connection cannot block discovery or reconnecting to another Admin. Alternate addresses of the same Admin are retained as connection fallbacks.

IPv4 broadcast calculation excludes /31, /32, tunnel, loopback and link-local interfaces. Discovery waits for its full response window, ignores malformed replies, and does not abandon valid LAN replies because another destination produces a UDP port-unreachable error. Discovery receives are cancelled and disposed at the end of each window.

WebSocket connection and greeting have a ten-second timeout. Both updated peers use ten-second keepalive intervals and a ten-second Pong timeout to detect silent network failures. The Admin stores each socket and device snapshot as a single entry and removes it only if that exact connection is still current. Closing or refreshing the dashboard cannot tear down the client connection. Existing dashboard sockets resolve the current client connection after a reconnect. Transport logs are serialized, cannot interrupt connections on file-sharing errors, and include disconnect reasons.

Admin firewall checks verify the executable path as well as the rule name, so moving Admin.exe cannot silently reuse a rule for an older path. Ports and protocol records remain unchanged; updated programs interoperate with the previous release.

Communication regression tests run without starting a remote shell or changing startup registration:

```powershell
dotnet run --project tests/Communication.Tests -c Release
# Optional: discover an existing LAN Admin and maintain a temporary test session.
dotnet run --project tests/Communication.Tests -c Release -- --live --soak
```

The client does not configure inbound or outbound firewall rules and never starts an elevated process. Only the Admin requires inbound firewall access because it listens for discovery and client WebSocket connections.

When `Client.exe` is started from outside `%LOCALAPPDATA%\RemoteAdmin`, it copies itself to `%LOCALAPPDATA%\RemoteAdmin\Client.exe` and starts that copy. The current development build preserves the original executable; the final build should remove it after handoff.

Remote terminal sessions start in the logged-in user's profile directory by default (`%USERPROFILE%`).
The client starts one CMD session automatically for each Admin connection; the dashboard does not expose start or stop controls.
Selecting a device in the dashboard restarts its CMD session so a previous `exit` command cannot permanently remove the terminal.
