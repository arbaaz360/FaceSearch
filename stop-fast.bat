@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Stopping Fast Services

echo ========================================
echo   FaceSearch - Stopping Fast Services
echo ========================================
echo.

REM -------- Stop .NET processes --------
echo [*] Stopping FastSearch API and FastIndexer worker...
taskkill /F /T /FI "WINDOWTITLE eq FastSearch-API*" >NUL 2>&1
taskkill /F /T /FI "WINDOWTITLE eq FastIndexer-Worker*" >NUL 2>&1
echo [OK] Fast .NET processes stopped

REM -------- Stop Python embedder processes --------
echo [*] Stopping Fast embedder instances...
taskkill /F /T /FI "WINDOWTITLE eq Fast-Embedder-*" >NUL 2>&1
taskkill /F /T /IM python.exe /FI "WINDOWTITLE eq Fast-Embedder-*" >NUL 2>&1
echo [OK] Fast embedders stopped

REM -------- Stop React frontend --------
echo [*] Stopping Fast frontend...
taskkill /F /T /FI "WINDOWTITLE eq Fast-Frontend*" >NUL 2>&1
taskkill /F /T /IM node.exe /FI "WINDOWTITLE eq Fast-Frontend*" >NUL 2>&1
echo [OK] Fast frontend stopped

echo.
echo ========================================
echo   Fast services stopped!
echo ========================================
echo.
pause

