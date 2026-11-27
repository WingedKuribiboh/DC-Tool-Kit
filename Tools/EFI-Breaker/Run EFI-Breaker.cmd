@echo off
rem Double-click to launch EFI Breaker. Works in Hiren's BootCD PE and in normal Windows.
where powershell.exe >NUL 2>&1 || (echo PowerShell was not found in this environment. & pause & exit /b 1)
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0EFI-Breaker.ps1"
if errorlevel 1 pause
