@echo off
:: Builds Tools\BitLocker\BitLocker Unlock.exe using the C# compiler that ships with Windows (no SDK needed).
setlocal
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found - .NET Framework 4.x required & exit /b 1)
set "OUT=%~dp0..\..\..\Tools\BitLocker\BitLocker Unlock.exe"
if not exist "%~dp0..\..\..\Tools\BitLocker" mkdir "%~dp0..\..\..\Tools\BitLocker"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%OUT%" ^
  /win32manifest:"%~dp0app.manifest" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "%~dp0BitLockerTool.cs"
if %errorlevel%==0 (echo Built "%OUT%") else (echo BUILD FAILED)
