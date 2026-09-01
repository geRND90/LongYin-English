@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ==========================================================
echo LongYin English Core v0.1.8-test42 BUILD
echo TEST38 stable base + TEST42 latest 831 unresolved canonical consistency wave
echo ==========================================================
echo.

where dotnet >nul 2>nul
if not errorlevel 1 (
  set "DOTNET_CMD=dotnet"
  goto :build
)

set "DOTNET_CMD=%~dp0_buildtools\dotnet\dotnet.exe"
if exist "%DOTNET_CMD%" goto :build

echo .NET SDK not found. Downloading a local .NET 6 SDK...
if not exist "%~dp0_buildtools" mkdir "%~dp0_buildtools"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $p='%~dp0_buildtools\dotnet-install.ps1'; Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -UseBasicParsing -OutFile $p; & $p -Channel 6.0 -InstallDir '%~dp0_buildtools\dotnet' -NoPath"
if errorlevel 1 goto :failed

:build
"%DOTNET_CMD%" build ".\LongYinEnglish\LongYinEnglish.csproj" -c Release
if errorlevel 1 goto :failed

if exist "%~dp0READY" rmdir /s /q "%~dp0READY"
mkdir "%~dp0READY\Mods"
copy /y ".\LongYinEnglish\bin\Release\net6.0\LongYinEnglish.dll" "%~dp0READY\Mods\LongYinEnglish.dll" >nul
xcopy /e /i /y ".\UserData" "%~dp0READY\UserData\" >nul
xcopy /e /i /y ".\Mods\ModsOfLong" "%~dp0READY\Mods\ModsOfLong\" >nul

echo.
echo READY: %~dp0READY
echo Copy the CONTENTS of READY to the game root.
echo.
echo This package contains ONLY LongYinEnglish and its translation data.
echo It does NOT build or include BuildingActionsNative or LongYinUIStabilizer.
echo.
pause
exit /b 0

:failed
echo.
echo BUILD FAILED. Send me the text shown above.
pause
exit /b 1
