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

The published directories contain the two runnable single-file executables. The local `release-final` directory is intentionally excluded from source control because the binaries exceed normal GitHub file-size limits.

On first Admin startup, a random password is generated for both `25kava` and `25mawa` and printed once to the Admin console. Set `REMOTE_ADMIN_INITIAL_PASSWORD` before first startup to choose it explicitly.
