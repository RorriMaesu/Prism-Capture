@echo off
setlocal enableextensions enabledelayedexpansion

rem Double-click to build (if needed) and launch the WinUI app.
rem Usage:
rem   RunPrismCapture.cmd            (builds+launches Debug)
rem   RunPrismCapture.cmd Release    (builds+launches Release)

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\ScreenRecorder.App\ScreenRecorder.App.csproj"
set "CONFIG=Debug"
if not "%~1"=="" set "CONFIG=%~1"

if not exist "%PROJECT%" (
  echo Could not find project:
  echo   "%PROJECT%"
  goto error
)

rem Pick a reasonable default platform for the current machine.
set "PLATFORM=x86"
if /i "%PROCESSOR_ARCHITECTURE%"=="AMD64" set "PLATFORM=x64"
if /i "%PROCESSOR_ARCHITEW6432%"=="AMD64" set "PLATFORM=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "PLATFORM=ARM64"

set "RIDARCH=x86"
if /i "%PLATFORM%"=="x64" set "RIDARCH=x64"
if /i "%PLATFORM%"=="ARM64" set "RIDARCH=arm64"

set "TFM=net8.0-windows10.0.19041.0"
set "EXE=%ROOT%src\ScreenRecorder.App\bin\%PLATFORM%\%CONFIG%\%TFM%\win-%RIDARCH%\PrismCapture.exe"

if exist "%EXE%" goto run

echo Building %PLATFORM% %CONFIG%...
dotnet build "%PROJECT%" -c "%CONFIG%" -p:Platform=%PLATFORM% || goto error

if not exist "%EXE%" (
  echo.
  echo Build completed but the exe was not found at:
  echo   "%EXE%"
  echo.
  echo If you changed the target framework, configuration, or packaging output,
  echo update this script accordingly.
  goto error
)

:run
echo Launching:
echo   "%EXE%"
start "Prism Capture" "%EXE%"
exit /b 0

:error
echo.
echo Failed to build or launch Prism Capture.
echo.
pause
exit /b 1
