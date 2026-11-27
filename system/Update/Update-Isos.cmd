@echo off
:: Update-Isos.cmd - downloads the newest ISOs (see update_isos.py).
::   Update-Isos.cmd            update everything that is out of date
::   Update-Isos.cmd --list     only show status
::   Update-Isos.cmd ubuntu     only that entry
:: Uses an installed Python if there is one; otherwise fetches a portable
:: Python (about 11 MB, from python.org) into system\Update\python\ once.
setlocal
set "HERE=%~dp0"
set "PY="

for %%P in (py.exe python.exe) do if not defined PY (
    where %%P >nul 2>&1 && (%%P -c "import sys; sys.exit(0 if sys.version_info>=(3,8) else 1)" >nul 2>&1 && set "PY=%%P")
)
if not defined PY if exist "%HERE%python\python.exe" set "PY=%HERE%python\python.exe"

if not defined PY (
    echo No Python found. Downloading portable Python to "%HERE%python" ...
    set "ARCH=amd64"
    if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "ARCH=arm64"
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol='Tls12'; $z=Join-Path $env:TEMP 'toolkit-python.zip'; Invoke-WebRequest -UseBasicParsing ('https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-'+$env:ARCH+'.zip') -OutFile $z; Expand-Archive -Force $z '%HERE%python'; Remove-Item $z"
    if errorlevel 1 (echo Could not download Python. Install it from python.org and re-run. & pause & exit /b 1)
    set "PY=%HERE%python\python.exe"
)

"%PY%" -I "%HERE%update_isos.py" %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
