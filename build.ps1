<#
.SYNOPSIS
Builds and packages the RemoteAdmin runtime payloads.

.DESCRIPTION
Publishes Admin.exe, c1.exe and c2.exe as self-contained win-x64 single-file
executables, then copies them and the installer scripts into release-final.

.PARAMETER SkipAdmin
Reuse the existing release-final\Admin.exe instead of republishing it.

.EXAMPLE
.\build.ps1
.EXAMPLE
.\build.ps1 -SkipAdmin
#>
param(
    [switch]$SkipAdmin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root
try {
    $candidates = @(
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    )

    $dotnet = $null
    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks
        if ($sdks -match '(?m)^10\.') {
            $dotnet = $candidate
            break
        }
    }

    if (-not $dotnet) {
        throw 'The .NET 10 SDK was not found. Install it and try again.'
    }

    Write-Host "Using $dotnet"
    & $dotnet build RemoteAdmin.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

    $common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '/p:PublishSingleFile=true', '/p:IncludeNativeLibrariesForSelfExtract=true',
        '/p:DebugType=None', '--nologo')

    if (-not $SkipAdmin) {
        & $dotnet publish Admin\Admin.csproj @common -o release\Admin
        if ($LASTEXITCODE -ne 0) { throw 'Admin publish failed.' }
        Copy-Item release\Admin\Admin.exe release-final\Admin.exe -Force
    }

    & $dotnet publish Client\Client.csproj @common -o release\c1
    if ($LASTEXITCODE -ne 0) { throw 'c1 publish failed.' }

    & $dotnet publish Watchdog\Watchdog.csproj @common -o release\c2
    if ($LASTEXITCODE -ne 0) { throw 'c2 publish failed.' }

    Copy-Item release\c1\c1.exe release-final\c1.exe -Force
    Copy-Item release\c2\c2.exe release-final\c2.exe -Force
    Copy-Item installer\install.bat release-final\install.bat -Force
    if (Test-Path release-final\uninstall.bat) { Remove-Item release-final\uninstall.bat -Force }

    Write-Host ''
    Write-Host 'Packaged:'
    Get-ChildItem release-final | Select-Object Name, Length | Format-Table -AutoSize
}
finally {
    Pop-Location
}
