@echo off
:: Repair-Windows.cmd - standard Windows corruption repair sequence. Run as admin.
net session >nul 2>&1 || (echo Run this as Administrator. & pause & exit /b 1)
echo [1/3] DISM CheckHealth / RestoreHealth
dism /online /cleanup-image /restorehealth
echo.
echo [2/3] System File Checker
sfc /scannow
echo.
echo [3/3] Component store cleanup
dism /online /cleanup-image /startcomponentcleanup
echo.
echo Done. Reboot, then re-run if sfc reported unrepairable files.
pause
