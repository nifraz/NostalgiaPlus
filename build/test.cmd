@echo off
REM Compile and run the DSP verification harness against the built plugin.
REM Pure console, deterministic, no desktop required - safe for CI.

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set ROOT=%~dp0..
set OUT=%ROOT%\bin

if not exist "%CSC%" (
  echo ERROR: csc.exe not found at %CSC%
  exit /b 1
)
if not exist "%OUT%\mb_NostalgiaPlus.dll" (
  echo ERROR: build the plugin first - run build\build.cmd
  exit /b 1
)

"%CSC%" -nologo -platform:anycpu -out:"%OUT%\TestHarness.exe" ^
  -r:"%OUT%\mb_NostalgiaPlus.dll" ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  "%ROOT%\build\TestHarness.cs"

if errorlevel 1 (
  echo TEST BUILD FAILED
  exit /b 1
)

"%OUT%\TestHarness.exe"
exit /b %errorlevel%
