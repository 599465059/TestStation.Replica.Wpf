@echo off
setlocal
if not exist "%~dp0src\TestStation.App\bin\Debug\net10.0-windows\TestStation.App.exe" (
    echo Build TestStation.slnx before starting this demo.
    exit /b 1
)
start "" "%~dp0src\TestStation.App\bin\Debug\net10.0-windows\TestStation.App.exe" --open "%~dp0artifacts\protocol-demo"
endlocal
