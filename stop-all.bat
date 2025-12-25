@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Stopping All Services

echo ========================================
echo   FaceSearch - Stopping All Services
echo ========================================
echo.

echo [1/2] Stopping Docker containers...
docker-compose down
if errorlevel 1 (
    echo [WARNING] Failed to stop Docker containers. They may already be stopped.
) else (
    echo [OK] Docker containers stopped
)
echo.

echo [2/2] Stopping service windows...
echo.
echo Please close the following windows manually:
echo   - FaceSearch-Embedder
echo   - FaceSearch-API
echo   - FaceSearch-Worker
echo.
echo Or press Ctrl+C in each window to stop the services.
echo.
pause

