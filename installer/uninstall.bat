@echo off
setlocal EnableExtensions DisableDelayedExpansion
title RemoteAdmin Client Uninstaller

set "ROOT=%LOCALAPPDATA%\RemoteAdmin"
set "GUARD=%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard"
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "REGKEY=HKCU\Software\RemoteAdminGuard"

echo ============================================
echo  RemoteAdmin client uninstaller
echo ============================================
echo.

rem 1. Disable both watchdogs first, otherwise they restore each other.
reg add "%REGKEY%" /v Disabled /t REG_DWORD /d 1 /f >nul 2>&1
if exist "%ROOT%" (> "%ROOT%\disabled.flag" echo disabled)

rem 2. Remove startup entries so nothing returns after logoff or reboot.
reg delete "%RUNKEY%" /v RemoteAdminC1 /f >nul 2>&1
reg delete "%RUNKEY%" /v RemoteAdminC2 /f >nul 2>&1
reg delete "%RUNKEY%" /v RemoteAdminClient /f >nul 2>&1

rem 3. Stop both processes and wait until neither is left.
set /a tries=0
:stop_loop
call :stop_processes
ping -n 2 127.0.0.1 >nul
call :any_running
if not errorlevel 1 goto stopped
set /a tries+=1
if %tries% lss 15 goto stop_loop
echo Warning: a RemoteAdmin process is still running; continuing removal.

:stopped
rem 4. Remove binaries, backups, client state and the hidden partner folder.
attrib -h -s -r "%ROOT%\c1.exe" >nul 2>&1
attrib -h -s -r "%ROOT%\c2.bak" >nul 2>&1
attrib -h -s -r "%ROOT%\Client.exe" >nul 2>&1
attrib -h -s -r "%ROOT%\disabled.flag" >nul 2>&1
if exist "%ROOT%\c1.exe" del /f /q "%ROOT%\c1.exe" >nul 2>&1
if exist "%ROOT%\c2.bak" del /f /q "%ROOT%\c2.bak" >nul 2>&1
if exist "%ROOT%\Client.exe" del /f /q "%ROOT%\Client.exe" >nul 2>&1
if exist "%ROOT%\Client.log" del /f /q "%ROOT%\Client.log" >nul 2>&1
if exist "%ROOT%\device.id" del /f /q "%ROOT%\device.id" >nul 2>&1
if exist "%ROOT%\system-notes.txt" del /f /q "%ROOT%\system-notes.txt" >nul 2>&1
if exist "%ROOT%\disabled.flag" del /f /q "%ROOT%\disabled.flag" >nul 2>&1
if exist "%ROOT%\c2.bak" echo Warning: could not remove "%ROOT%\c2.bak".

call :stop_processes
attrib -h -s -r "%GUARD%\*" /s /d >nul 2>&1
attrib -h -s -r "%GUARD%" >nul 2>&1
rmdir /s /q "%GUARD%" >nul 2>&1
if exist "%GUARD%" echo Warning: could not fully remove "%GUARD%".

rem Remove the disable marker only after both payload binaries are gone.
reg delete "%REGKEY%" /f >nul 2>&1

echo.
echo RemoteAdmin client removed.
echo Leftover files and this script are deleted in a few seconds.
echo.
if /i not "%~1"=="--quiet" pause

rem 5. Delete what this running script cannot delete itself.
call :stop_processes
set "CLEAN=%TEMP%\remoteadmin_cleanup_%RANDOM%%RANDOM%.cmd"
> "%CLEAN%" echo @echo off
>>"%CLEAN%" echo ping -n 4 127.0.0.1 ^>nul
>>"%CLEAN%" echo attrib -h -s -r "%ROOT%\c2.bak" ^>nul 2^>^&1
>>"%CLEAN%" echo del /f /q "%ROOT%\c2.bak" ^>nul 2^>^&1
>>"%CLEAN%" echo del /f /q "%ROOT%\uninstall.bat" ^>nul 2^>^&1
>>"%CLEAN%" echo rmdir /s /q "%GUARD%" ^>nul 2^>^&1
>>"%CLEAN%" echo rmdir "%ROOT%" ^>nul 2^>^&1
>>"%CLEAN%" echo del /f /q "%%~f0"
start "" /min "%CLEAN%"
exit /b 0

:stop_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RemoteAdmin')); $guard=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Caches\RemoteAdminGuard')); Get-Process -ErrorAction SilentlyContinue | ForEach-Object { try { $p=$_.Path } catch { $p=$null }; if ($p -and ($p.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($guard,[StringComparison]::OrdinalIgnoreCase))) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }" >nul 2>&1
exit /b

:any_running
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RemoteAdmin')); $guard=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Caches\RemoteAdminGuard')); $found=@(Get-Process -ErrorAction SilentlyContinue | ForEach-Object { try { $p=$_.Path } catch { $p=$null }; if ($p -and ($p.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($guard,[StringComparison]::OrdinalIgnoreCase))) { $p } }); if ($found.Count -gt 0) { exit 1 } else { exit 0 }" >nul 2>&1
exit /b
