@echo off
cd /d "%~dp0"
taskkill /F /IM CyberFanControl.exe >nul 2>&1
dotnet clean -c Debug >nul 2>&1
dotnet build -c Debug
pause
