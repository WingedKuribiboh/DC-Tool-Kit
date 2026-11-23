@echo off
:: Reset-WindowsUpdate.cmd - clears the Windows Update cache when updates are stuck. Run as admin.
net session >nul 2>&1 || (echo Run this as Administrator. & pause & exit /b 1)
net stop wuauserv
net stop bits
net stop cryptsvc
if exist %SystemRoot%\SoftwareDistribution ren %SystemRoot%\SoftwareDistribution SoftwareDistribution.old
if exist %SystemRoot%\System32\catroot2 ren %SystemRoot%\System32\catroot2 catroot2.old
net start cryptsvc
net start bits
net start wuauserv
echo Done. Reboot and check for updates. The .old folders can be deleted once updates work.
pause
