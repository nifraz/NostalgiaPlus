@echo off
REM Reproduces MusicBee's own plugin load sequence: type lookup by name,
REM Initialise against a zeroed API block, panel creation from a background
REM thread, paint, shutdown.
REM
REM Creates WinForms windows offscreen, so unlike test.cmd this needs a desktop
REM session. In CI it is allowed to fail rather than block the build.

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set ROOT=%~dp0..
set OUT=%ROOT%\bin

if not exist "%OUT%\mb_NostalgiaPlus.dll" (
  echo ERROR: build the plugin first - run build\build.cmd
  exit /b 1
)

"%CSC%" -nologo -target:exe -platform:anycpu -out:"%OUT%\Verify.exe" ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  "%ROOT%\build\Verify.cs"

if errorlevel 1 (
  echo VERIFY BUILD FAILED
  exit /b 1
)

"%OUT%\Verify.exe" "%OUT%\mb_NostalgiaPlus.dll" "%OUT%"
exit /b %errorlevel%
