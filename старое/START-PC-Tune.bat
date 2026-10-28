@echo off
rem PC-Tune launcher. Self-elevates and runs Tune.ps1 in Windows PowerShell 5.1
rem (Checkpoint-Computer and Enable-ComputerRestore do not exist in PowerShell 7).

net session >nul 2>&1
if errorlevel 1 goto elevate
goto run

:elevate
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:run
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tune.ps1"
