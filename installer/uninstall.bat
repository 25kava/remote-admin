@echo off
setlocal EnableExtensions DisableDelayedExpansion
title RemoteAdmin uninstaller

rem Removes every per-user trace of the RemoteAdmin client:
rem   %LOCALAPPDATA%\RemoteAdmin                                   (c1.exe, c2.bak, notes, logs, device id, updates)
rem   %LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard    (hidden c2.exe, c1.bak)
rem   HKCU Run values RemoteAdminC1, RemoteAdminC2, RemoteAdminClient
rem   HKCU\Software\RemoteAdminGuard
rem When run elevated, it also deletes the Admin firewall rules.
rem
rem Run as the target user, without elevation, exactly like install.bat.
rem Usage: uninstall.bat [/quiet] [/dry-run]

set "QUIET="
set "DRYRUN="
set "FAIL="
set "ADMIN="

:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="/quiet" set "QUIET=1"
if /i "%~1"=="--quiet" set "QUIET=1"
if /i "%~1"=="/dry-run" set "DRYRUN=1"
if /i "%~1"=="--dry-run" set "DRYRUN=1"
shift
goto :parse_args
:args_done

if not defined LOCALAPPDATA goto :no_localappdata

set "ROOT=%LOCALAPPDATA%\RemoteAdmin"
set "GUARD=%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard"
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "REGKEY=HKCU\Software\RemoteAdminGuard"
set "FW_DISCOVERY=Remote Admin - Discovery (UDP 38991)"
set "FW_CLIENT=Remote Admin - Client connections (TCP 38992)"

rem Deleting the folder this script runs from fails while it is the working
rem directory, so relaunch from a temporary copy when the script lives inside
rem an install location.
for %%I in ("%~dp0.") do set "HERE=%%~fI"
echo "%HERE%" | findstr /i /l /c:"%ROOT%" >nul
if not errorlevel 1 goto :relocate
goto :confirm

:relocate
echo Relaunching the uninstaller from a temporary folder...
copy /y "%~f0" "%TEMP%\ra-uninstall.bat" >nul 2>&1
if errorlevel 1 goto :confirm
call "%TEMP%\ra-uninstall.bat" %*
set "RC=%ERRORLEVEL%"
del /f /q "%TEMP%\ra-uninstall.bat" >nul 2>&1
exit /b %RC%

:confirm
echo ============================================
echo  RemoteAdmin uninstaller
echo ============================================
echo.
echo This permanently deletes:
echo   %ROOT%
echo   %GUARD%
echo   HKCU Run values RemoteAdminC1, RemoteAdminC2, RemoteAdminClient
echo   HKCU\Software\RemoteAdminGuard
echo.
if defined QUIET goto :proceed
choice /c YN /n /m "Remove RemoteAdmin from this account? [Y/N] "
if errorlevel 2 goto :cancelled

:proceed
if defined DRYRUN goto :dry_run

rem Machine-wide firewall rules live in the Admin machine's HKLM policy store,
rem so only an elevated run can remove them.
fltmc >nul 2>&1
if errorlevel 1 goto :disabled
set "ADMIN=1"
:disabled

rem Trip the shared kill switch before touching any file. Both watchdogs check
rem it every three seconds and exit instead of restoring their partner.
if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1
echo disabled>"%ROOT%\disabled.flag"
reg add "%REGKEY%" /v Disabled /t REG_DWORD /d 1 /f >nul 2>&1

rem Kill both watchdogs together, twice: the first pass can still race a
rem supervisor tick that restarts the partner.
call :stop_processes
ping -n 2 127.0.0.1 >nul
call :stop_processes

reg delete "%RUNKEY%" /v RemoteAdminC1 /f >nul 2>&1
reg delete "%RUNKEY%" /v RemoteAdminC2 /f >nul 2>&1
reg delete "%RUNKEY%" /v RemoteAdminClient /f >nul 2>&1
schtasks /delete /tn "RemoteAdminDeploy" /f >nul 2>&1
schtasks /delete /tn "RemoteAdminUpdate" /f >nul 2>&1

set "TRY=0"
:delete_loop
set /a TRY+=1
call :stop_processes
attrib -h -s -r "%ROOT%\*" /s /d >nul 2>&1
attrib -h -s -r "%ROOT%" >nul 2>&1
attrib -h -s -r "%GUARD%\*" /s /d >nul 2>&1
attrib -h -s -r "%GUARD%" >nul 2>&1
if exist "%ROOT%" rd /s /q "%ROOT%" >nul 2>&1
if exist "%GUARD%" rd /s /q "%GUARD%" >nul 2>&1
if not exist "%ROOT%" if not exist "%GUARD%" goto :deleted
if %TRY% GEQ 3 goto :incomplete
ping -n 2 127.0.0.1 >nul
goto :delete_loop

:deleted
reg delete "%REGKEY%" /f >nul 2>&1
goto :firewall

:incomplete
rem Leave the kill switch engaged: a partially removed payload must never
rem resurrect itself on the next supervisor tick.
reg add "%REGKEY%" /v Disabled /t REG_DWORD /d 1 /f >nul 2>&1
set "FAIL=1"
goto :firewall

:firewall
if not defined ADMIN goto :firewall_note
netsh advfirewall firewall delete rule name="%FW_DISCOVERY%" >nul 2>&1
netsh advfirewall firewall delete rule name="%FW_CLIENT%" >nul 2>&1
echo Removed the Admin firewall rules.
goto :verify

:firewall_note
echo NOTE: run this script elevated on the Admin computer to also delete its
echo       firewall rules.
echo.
goto :verify

:verify
if exist "%ROOT%" set "FAIL=1"
if exist "%GUARD%" set "FAIL=1"
reg query "%RUNKEY%" /v RemoteAdminC1 >nul 2>&1
if not errorlevel 1 set "FAIL=1"
reg query "%RUNKEY%" /v RemoteAdminC2 >nul 2>&1
if not errorlevel 1 set "FAIL=1"
reg query "%REGKEY%" >nul 2>&1
if not errorlevel 1 set "FAIL=1"
call :check_processes
if defined FAIL goto :failure
goto :success

:success
echo.
echo RemoteAdmin was removed from this account.
if not defined QUIET pause
exit /b 0

:failure
echo.
echo ERROR: RemoteAdmin was only partially removed.
echo        Stop c1.exe and c2.exe in Task Manager, then run this script again.
echo        The kill switch is still set, so the remaining files stay inactive.
if not defined QUIET pause
exit /b 1

:cancelled
echo.
echo Cancelled; nothing was changed.
if not defined QUIET pause
exit /b 1

:dry_run
echo Dry run: nothing was changed. The commands above would remove the paths
echo and registry values listed.
exit /b 0

:no_localappdata
echo ERROR: LOCALAPPDATA is not defined. Run this script as the target user.
if not defined QUIET pause
exit /b 1

rem ---------------------------------------------------------------------------

:stop_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$roots=@((Join-Path $env:LOCALAPPDATA 'RemoteAdmin'),(Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Caches\RemoteAdminGuard')); $ids=@(); Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $p=$null; try { $p=$_.Path } catch { }; if ($p) { foreach ($r in $roots) { if ($p.StartsWith($r,[StringComparison]::OrdinalIgnoreCase)) { $ids += $_.Id; break } } } }; if ($ids.Count -gt 0) { Stop-Process -Id $ids -Force -ErrorAction SilentlyContinue }" >nul 2>&1
exit /b

:check_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$roots=@((Join-Path $env:LOCALAPPDATA 'RemoteAdmin'),(Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Caches\RemoteAdminGuard')); $found=0; Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $p=$null; try { $p=$_.Path } catch { }; if ($p) { foreach ($r in $roots) { if ($p.StartsWith($r,[StringComparison]::OrdinalIgnoreCase)) { $found=1 } } } }; exit $found" >nul 2>&1
if errorlevel 1 set "FAIL=1"
exit /b
