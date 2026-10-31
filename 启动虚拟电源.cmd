@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET_EXE%" set "DOTNET_EXE=dotnet.exe"
"%DOTNET_EXE%" run --project "tools\TestStation.VirtualPowerSupply\TestStation.VirtualPowerSupply.csproj" --no-launch-profile -- %*
if errorlevel 1 pause
endlocal
