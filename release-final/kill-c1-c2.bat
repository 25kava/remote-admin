@echo off
setlocal EnableExtensions DisableDelayedExpansion
title RemoteAdmin stop c1 and c2

rem Stop both watchdogs with one Stop-Process call. Killing them together
rem prevents either process from restoring the other.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$names=@('c1','c2'); $p=@(Get-Process -Name $names -ErrorAction SilentlyContinue); if($p.Count -eq 0){ Write-Host 'c1.exe and c2.exe are not running.'; exit 0 }; $ids=@($p.Id); Stop-Process -Id $ids -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 150; $left=@(Get-Process -Name $names -ErrorAction SilentlyContinue); if($left.Count -gt 0){ Stop-Process -Id @($left.Id) -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 150; $left=@(Get-Process -Name $names -ErrorAction SilentlyContinue) }; if($left.Count -eq 0){ Write-Host 'c1.exe and c2.exe stopped.'; exit 0 }; $still=$left | ForEach-Object { $_.ProcessName + '(' + $_.Id + ')' }; Write-Host ('Could not stop: ' + ($still -join ', ')); exit 1"
if errorlevel 1 goto failed
exit /b 0

:failed
echo Failed to stop all RemoteAdmin processes.
exit /b 1
