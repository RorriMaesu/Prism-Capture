@echo off
setlocal

rem Double-click installer for a real Start Menu app (MSIX).
rem Usage:
rem   InstallPrismCaptureMsix.cmd
rem   InstallPrismCaptureMsix.cmd Release
rem   InstallPrismCaptureMsix.cmd Release -Platform x64 -InstallFfmpeg

set "CONFIG=Debug"
set "EXTRA_ARGS="

if /i "%~1"=="Debug" (
  set "CONFIG=Debug"
  shift
) else if /i "%~1"=="Release" (
  set "CONFIG=Release"
  shift
)

set "EXTRA_ARGS=%*"

set "ROOT=%~dp0"
set "PS1=%ROOT%scripts\InstallPrismCaptureMsix.ps1"

if not exist "%PS1%" (
  echo Missing installer script:
  echo   "%PS1%"
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Configuration "%CONFIG%" -Force %EXTRA_ARGS%
if errorlevel 1 (
  echo.
  echo Install failed.
  pause
  exit /b 1
)

echo.
echo Installed. Open Start and search for "Prism Capture".
pause
exit /b 0
