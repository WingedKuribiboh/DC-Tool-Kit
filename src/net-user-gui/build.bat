@echo off
:: Builds Tools\NetUserManager.exe using the C# compiler that ships with Windows (no SDK needed).
setlocal
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found - .NET Framework 4.x required & exit /b 1)
set "OUT=%~dp0..\..\Tools\NetUserManager.exe"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%OUT%" ^
  /win32manifest:"%~dp0app.manifest" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  "%~dp0NetUserManager.cs"
if %errorlevel%==0 (echo Built "%OUT%") else (echo BUILD FAILED)
