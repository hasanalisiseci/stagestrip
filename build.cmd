@echo off
rem Builds with the C# compiler that ships with Windows; nothing to install.
rem Usage: build.cmd [output.exe]  (default: StageManager.exe next to this file)
set OUTPUT=%~1
if "%OUTPUT%"=="" set OUTPUT=%~dp0StageManager.exe
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /platform:x64 /target:winexe /out:"%OUTPUT%" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0StageManager.cs" "%~dp0StripRenderer.cs" "%~dp0Manager.cs" "%~dp0Strip.cs" "%~dp0Program.cs"
