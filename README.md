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

If Windows Firewall previously blocked Admin, run this from the repository folder:

```powershell
.\repair-firewall.ps1
```

The script requests administrator permission through UAC automatically. Approve that prompt. It removes old blocking `Admin.exe` rules and creates allow rules for Admin inbound UDP `38991` discovery and TCP `38992` client connections, plus Client outbound UDP `38991` discovery and TCP `38992` connections, on Domain, Private, and Public networks. Run it on both PCs.

The published `Admin.exe` and `Client.exe` also perform this firewall setup automatically on first run. Windows may show one UAC prompt; approve it once. `Client.exe` can then run by itself without requiring `Admin.exe` to be started first.

When `Client.exe` is started from outside `%LOCALAPPDATA%\RemoteAdmin`, it copies itself to `%LOCALAPPDATA%\RemoteAdmin\Client.exe` and starts that copy. The current development build preserves the original executable; the final build should remove it after handoff.

Remote terminal sessions start in the logged-in user's profile directory by default (`%USERPROFILE%`).
The client starts one CMD session automatically for each Admin connection; the dashboard does not expose start or stop controls.
Selecting a device in the dashboard restarts its CMD session so a previous `exit` command cannot permanently remove the terminal.
