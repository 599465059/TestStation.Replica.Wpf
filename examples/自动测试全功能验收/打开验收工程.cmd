@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Open-AcceptanceProject.ps1"
if errorlevel 1 pause
endlocal
