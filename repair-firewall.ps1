$ErrorActionPreference = "Stop"
$adminPath = Join-Path $PSScriptRoot "release-final\Admin.exe"

if (-not (Test-Path $adminPath)) {
    throw "Published Admin.exe was not found at $adminPath"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Requesting administrator permission..."
    $args = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $args -Wait -PassThru
    if ($elevated.ExitCode -ne 0) {
        throw "The elevated firewall repair failed or was cancelled. Exit code: $($elevated.ExitCode)"
    }
    exit 0
}

try {
    Get-NetFirewallRule |
        Where-Object { $_.DisplayName -ieq "admin.exe" -and $_.Action -eq "Block" } |
        Remove-NetFirewallRule -ErrorAction Stop

    Get-NetFirewallRule -DisplayName "Remote Admin - Discovery (UDP 38991)" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction Stop
    Get-NetFirewallRule -DisplayName "Remote Admin - Client connections (TCP 38992)" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction Stop

    New-NetFirewallRule `
        -DisplayName "Remote Admin - Discovery (UDP 38991)" `
        -Direction Inbound `
        -Action Allow `
        -Protocol UDP `
        -LocalPort 38991 `
        -Program $adminPath `
        -Profile Domain,Private `
        -Enabled True `
        -ErrorAction Stop | Out-Null

    New-NetFirewallRule `
        -DisplayName "Remote Admin - Client connections (TCP 38992)" `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort 38992 `
        -Program $adminPath `
        -Profile Domain,Private `
        -Enabled True `
        -ErrorAction Stop | Out-Null

    $expected = Get-NetFirewallRule -DisplayName "Remote Admin - Discovery (UDP 38991)", "Remote Admin - Client connections (TCP 38992)" -ErrorAction Stop
    if (@($expected).Count -ne 2 -or @($expected | Where-Object { $_.Action -ne "Allow" -or $_.Enabled -ne "True" }).Count -ne 0) {
        throw "Firewall rules were not created in the expected enabled/allow state."
    }

    Write-Host "Remote Admin firewall rules repaired for Domain and Private networks."
}
catch {
    Write-Error "Firewall repair failed: $($_.Exception.Message)"
    exit 1
}
