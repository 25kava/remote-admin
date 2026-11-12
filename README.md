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

### Live screen

Update both executables to use the live screen panel. Select a connected device and choose **View screen**. The client shares its primary display as JPEG frames, at up to 30 fps and 1280 x 720 pixels, through the existing WebSocket connection. A slow connection does not build an image backlog.

While sharing, the client shows an always-on-top red circle in the upper-right corner of the primary display. The white stop symbol, hover text and accessible button name identify it as **Stop sharing**. Clicking the circle stops sharing immediately. There is no per-session confirmation dialog. The indicator is shown before capture starts; capture stops if it is hidden or minimized, or if the desktop locks or becomes unavailable. Closing the Admin viewer, changing devices or losing the connection also stops sharing. A new viewing request is required to restart it.

Only the requesting browser receives the images. One viewer can share a client's screen at a time, including across multiple Admin connections. Older clients are listed normally, with a message to update Client.exe before screen viewing is available. The dashboard and its screen-view script are embedded in Admin.exe; no extra runtime files are required.

Client targets `net10.0-windows` with Windows Forms for the visible stop indicator and Windows desktop capture. The published executables remain self-contained single-file Windows x64 programs. Screen-session tests use synthetic pixels and validate indicator gating, local Stop, viewer isolation, cancellation and backpressure without starting a terminal or changing startup registration.

### Target cursor and fullscreen control

The target's visible Windows cursor is composited into each captured frame before resizing, including its current shape and hotspot. Cursors outside the shared primary display or hidden by Windows are not drawn.

For unattended control of your own computers, open **View screen**, choose **Full screen**, then **Enable control** in the fullscreen toolbar. There is no target approval dialog. Mouse movement, left/middle/right clicks, dragging, wheel scrolling and physical keyboard keys are supported. The visible red stop indicator gains a gold ring while control is active; clicking it stops both input and screen sharing.

**Stop control** returns to view-only mode. Escape or **Exit fullscreen**, loss of Admin window focus, a hidden tab, a connection failure or stopping screen sharing releases the keys/buttons held by this session. Control requires a heartbeat every 500 ms and ends after 2.5 seconds without one. It never resumes automatically after interruption. Other browser viewers cannot inject input into the owner's screen session. Image letterboxing is excluded from coordinate mapping, and multiple-monitor virtual desktop origins are handled correctly.

Update both executables for control support; older clients can still be viewed but do not advertise remote control. Windows elevation boundaries, UAC/secure desktop and browser-reserved shortcuts remain unavailable. The target's local input is never blocked. Keyboard mapping uses physical key codes and the target's keyboard layout.

Additional checks (native input is replaced by a fake API during tests):

```powershell
node tests/ScreenView.Tests.cjs
dotnet run --project tests/WindowsInput.Tests -c Release
```

Remote control of the Admin computer itself is disabled to prevent injected input from being forwarded back into its own fullscreen viewer. Viewing that computer remains available.

The stream targets 30 FPS using periodic pacing that compensates for OS timer jitter. Capture/encode/send time counts against each frame interval, and a slow send is awaited before capturing again. The dashboard reports the measured incoming FPS; achievable throughput depends on capture hardware and network performance.

On supported Windows systems the client uses GPU-backed Windows Graphics Capture, with the OS cursor and capture border enabled. It keeps only the latest native frame and reuses the current image when the desktop is unchanged. Capture ends before the red stop indicator closes, even on an encoder error. Unsupported drivers/APIs use a reusable, scaled GDI surface as a compatibility path; access-denied errors do not trigger a bypass. The local animated-desktop benchmark reached approximately 28–30 FPS, versus about 10 FPS with the previous capture path.
