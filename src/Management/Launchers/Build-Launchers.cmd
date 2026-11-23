@echo off
:: Rebuilds every launcher in Tools\ from launchers.csv (see build-launchers.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-launchers.ps1"
pause
