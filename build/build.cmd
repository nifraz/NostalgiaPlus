@echo off
REM Build Nostalgia+ with the .NET Framework compiler that ships with Windows.
REM No Visual Studio, no SDK, no NuGet packages required.

setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
set ROOT=%~dp0..
set OUT=%ROOT%\bin\mb_NostalgiaPlus.dll

if not exist "%CSC%" (
  echo ERROR: csc.exe not found at %CSC%
  exit /b 1
)

if not exist "%ROOT%\bin" mkdir "%ROOT%\bin"

"%CSC%" -nologo -target:library -platform:anycpu -optimize+ -out:"%OUT%" ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  "%ROOT%\src\*.cs" "%ROOT%\src\Dsp\*.cs" "%ROOT%\src\Audio\*.cs" ^
  "%ROOT%\src\Render\*.cs" "%ROOT%\src\Ui\*.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo Built %OUT%
endlocal
