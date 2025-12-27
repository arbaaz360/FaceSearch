@echo off
setlocal
REM Thin wrapper to run the PowerShell orchestrator
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-all.ps1" %*
