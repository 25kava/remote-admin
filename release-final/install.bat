@echo off
setlocal EnableExtensions DisableDelayedExpansion
title RemoteAdmin Client Installer

set "ROOT=%LOCALAPPDATA%\RemoteAdmin"
set "GUARD=%LOCALAPPDATA%\Microsoft\Windows\Caches\RemoteAdminGuard"
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
set "REGKEY=HKCU\Software\RemoteAdminGuard"
set "SRC=%~dp0"

echo ============================================
echo  RemoteAdmin client installer
echo ============================================
echo.

if not exist "%SRC%c1.exe" goto missing
if not exist "%SRC%c2.exe" goto missing

rem Disable both watchdogs before touching their files, so neither one
rem restores its partner while the installation is being replaced.
reg add "%REGKEY%" /v Disabled /t REG_DWORD /d 1 /f >nul 2>&1
call :stop_processes
ping -n 2 127.0.0.1 >nul
call :stop_processes

rem Remove previous startup registrations. RemoteAdminClient belongs to the
rem original single-executable client and is not used any more.
reg delete "%RUNKEY%" /v RemoteAdminC1 /f >nul 2>&1
reg delete "%RUNKEY%" /v RemoteAdminC2 /f >nul 2>&1
reg delete "%RUNKEY%" /v RemoteAdminClient /f >nul 2>&1

if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1
if not exist "%GUARD%" mkdir "%GUARD%" >nul 2>&1
if not exist "%ROOT%" goto failed
if not exist "%GUARD%" goto failed

rem Remove legacy files from the earlier single-executable and uninstaller layouts.
attrib -h -s -r "%ROOT%\Client.exe" >nul 2>&1
if exist "%ROOT%\Client.exe" del /f /q "%ROOT%\Client.exe" >nul 2>&1
attrib -h -s -r "%ROOT%\uninstall.bat" >nul 2>&1
if exist "%ROOT%\uninstall.bat" del /f /q "%ROOT%\uninstall.bat" >nul 2>&1
if exist "%ROOT%\uninstall.bat.tmp" del /f /q "%ROOT%\uninstall.bat.tmp" >nul 2>&1

rem copy cannot replace hidden/system files, so clear the attributes first.
attrib -h -s "%GUARD%" >nul 2>&1
attrib -h -s -r "%ROOT%\c1.exe" >nul 2>&1
attrib -h -s -r "%ROOT%\c2.bak" >nul 2>&1
attrib -h -s -r "%GUARD%\c2.exe" >nul 2>&1
attrib -h -s -r "%GUARD%\c1.bak" >nul 2>&1

if exist "%ROOT%\c1.exe" del /f /q "%ROOT%\c1.exe" >nul 2>&1
if exist "%ROOT%\c1.exe" goto failed
if exist "%GUARD%\c2.exe" del /f /q "%GUARD%\c2.exe" >nul 2>&1
if exist "%GUARD%\c2.exe" goto failed
if exist "%ROOT%\c2.bak" del /f /q "%ROOT%\c2.bak" >nul 2>&1
if exist "%GUARD%\c1.bak" del /f /q "%GUARD%\c1.bak" >nul 2>&1

copy /y "%SRC%c1.exe" "%ROOT%\c1.exe" >nul
if errorlevel 1 goto failed
copy /y "%SRC%c2.exe" "%GUARD%\c2.exe" >nul
if errorlevel 1 goto failed
attrib -h -s "%ROOT%\c1.exe" >nul 2>&1

rem Each side keeps a backup of its partner, so either one can rebuild the
rem other after a deletion. The backups live on opposite sides of the pair.
copy /y "%SRC%c2.exe" "%ROOT%\c2.bak" >nul
copy /y "%SRC%c1.exe" "%GUARD%\c1.bak" >nul

attrib +h +s "%GUARD%" >nul 2>&1
attrib +h +s "%GUARD%\c2.exe" >nul 2>&1
attrib +h +s "%GUARD%\c1.bak" >nul 2>&1
attrib +h +s "%ROOT%\c2.bak" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%ROOT%\c1.exe','%GUARD%\c2.exe' -ErrorAction SilentlyContinue" >nul 2>&1

rem Register both watchdogs for the current user and clear the disabled flag.
reg add "%RUNKEY%" /v RemoteAdminC1 /t REG_SZ /d "\"%ROOT%\c1.exe\"" /f >nul
if errorlevel 1 goto failed
reg add "%RUNKEY%" /v RemoteAdminC2 /t REG_SZ /d "\"%GUARD%\c2.exe\"" /f >nul
if errorlevel 1 goto failed

reg delete "%REGKEY%" /v Disabled /f >nul 2>&1
if exist "%ROOT%\disabled.flag" del /f /q "%ROOT%\disabled.flag" >nul 2>&1

start "" "%ROOT%\c1.exe"
start "" "%GUARD%\c2.exe"

ping -n 3 127.0.0.1 >nul
call :report

echo.
echo Installed c1.exe to:
echo   %ROOT%\c1.exe
echo Installed c2.exe to:
echo   %GUARD%\c2.exe
echo.
if /i not "%~1"=="--quiet" pause
exit /b 0

:missing
echo ERROR: install.bat must stay next to c1.exe and c2.exe.
echo        Missing file in "%SRC%".
echo.
if /i not "%~1"=="--quiet" pause
exit /b 1

:failed
echo ERROR: installation failed.
echo        Close any running RemoteAdmin process and run install.bat again
echo        from a normal (non-elevated) account.
echo.
if /i not "%~1"=="--quiet" pause
exit /b 1

:stop_processes
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RemoteAdmin')); $guard=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Caches\RemoteAdminGuard')); Get-Process -ErrorAction SilentlyContinue | ForEach-Object { try { $p=$_.Path } catch { $p=$null }; if ($p -and ($p.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($guard,[StringComparison]::OrdinalIgnoreCase))) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } }" >nul 2>&1
exit /b

:report
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c1=@(Get-Process -Name c1 -ErrorAction SilentlyContinue); $c2=@(Get-Process -Name c2 -ErrorAction SilentlyContinue); Write-Host ('c1 running: ' + [bool]$c1); Write-Host ('c2 running: ' + [bool]$c2)"
exit /b
