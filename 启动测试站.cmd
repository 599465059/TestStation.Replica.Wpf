@echo off
setlocal
cd /d "%~dp0"

set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET_EXE%" set "DOTNET_EXE=dotnet.exe"

echo Preparing Test Station and OpenTAP Host...
echo Dependencies and changed projects will be built before startup.
"%DOTNET_EXE%" run --project "src\TestStation.App\TestStation.App.csproj" --no-launch-profile
if errorlevel 1 (
    echo.
    echo Startup failed. Review the error above.
    pause
)

endlocal
