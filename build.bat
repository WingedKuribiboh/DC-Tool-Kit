@echo off
:: Builds the self-extracting packages into dist\ (see build.ps1). Args are passed through, e.g. build.bat -NoCompile
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
pause
