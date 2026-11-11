$ErrorActionPreference = "Stop"
$adminPath = Join-Path $PSScriptRoot "release-final\Admin.exe"

if (-not (Test-Path $adminPath)) {
    throw "Published Admin.exe was not found at $adminPath"
}

Get-NetFirewallRule |
    Where-Object { $_.DisplayName -ieq "admin.exe" -and $_.Action -eq "Block" } |
    Remove-NetFirewallRule

Get-NetFirewallRule -DisplayName "Remote Admin - Discovery (UDP 38991)" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName "Remote Admin - Client connections (TCP 38992)" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName "Remote Admin - Discovery (UDP 38991)" `
    -Direction Inbound `
    -Action Allow `
    -Protocol UDP `
    -LocalPort 38991 `
    -Program $adminPath `
    -Profile Domain,Private `
    -Enabled True | Out-Null

New-NetFirewallRule `
    -DisplayName "Remote Admin - Client connections (TCP 38992)" `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 38992 `
    -Program $adminPath `
    -Profile Domain,Private `
    -Enabled True | Out-Null

Write-Host "Remote Admin firewall rules repaired for Domain and Private networks."
