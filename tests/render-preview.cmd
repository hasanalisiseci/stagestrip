@echo off
rem Compiles and runs the window-free preview; writes PNGs to %TEMP%\stagetest. Opens nothing on screen.
setlocal
set OUT=%TEMP%\stagetest
if not exist "%OUT%" mkdir "%OUT%"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /platform:x64 /target:winexe /out:"%OUT%\RenderPreview.exe" /r:System.Drawing.dll "%~dp0RenderPreview.cs" "%~dp0..\StripRenderer.cs" || exit /b 1
"%OUT%\RenderPreview.exe" "%OUT%"
