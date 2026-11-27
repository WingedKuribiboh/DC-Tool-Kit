@echo off
:: Builds UpdateToolkit.exe (at the toolkit root) using the C# compiler that ships with Windows (no SDK needed).
setlocal
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo csc.exe not found - .NET Framework 4.x required & exit /b 1)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%~dp0..\..\UpdateToolkit.exe" ^
  /win32manifest:"%~dp0app.manifest" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.FileSystem.dll ^
  "%~dp0UpdateToolkit.cs"
if %errorlevel%==0 (echo Built %~dp0..\..\UpdateToolkit.exe) else (echo BUILD FAILED)
