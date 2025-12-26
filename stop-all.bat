@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Stopping All Services

echo ========================================
echo   FaceSearch - Stopping All Services
echo ========================================
echo.

REM -------- Stop .NET processes --------
echo [*] Stopping .NET API and Worker processes...
taskkill /F /IM FaceSearch.exe >NUL 2>&1
taskkill /F /FI "WINDOWTITLE eq FaceSearch-API*" >NUL 2>&1
taskkill /F /FI "WINDOWTITLE eq FaceSearch-Worker*" >NUL 2>&1
echo [OK] .NET processes stopped

REM -------- Stop Python embedder processes --------
echo [*] Stopping Python embedder instances...
taskkill /F /FI "WINDOWTITLE eq FaceSearch-Embedder*" >NUL 2>&1
taskkill /F /IM python.exe /FI "WINDOWTITLE eq FaceSearch-Embedder*" >NUL 2>&1
echo [OK] Embedder processes stopped

REM -------- Stop React frontend --------
echo [*] Stopping React frontend...
taskkill /F /FI "WINDOWTITLE eq FaceSearch-Frontend*" >NUL 2>&1
taskkill /F /IM node.exe /FI "WINDOWTITLE eq FaceSearch-Frontend*" >NUL 2>&1
echo [OK] Frontend stopped

REM -------- Stop Docker (optional) --------
echo.
echo [*] Docker containers are still running.
echo     To stop them, run: docker-compose down
echo.

echo ========================================
echo   All services stopped!
echo ========================================
echo.
pause
