@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Starting Multiple Embedder Instances

REM -------- Paths --------
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

REM -------- Config --------
REM Number of embedder instances to start (default: 3)
set INSTANCE_COUNT=%1
if "%INSTANCE_COUNT%"=="" set INSTANCE_COUNT=3

REM Base port (first instance will use this, subsequent ones increment)
set BASE_PORT=8090

REM GPU device selection: cuda | dml | cpu
set CLIP_DEVICE=dml

echo ========================================
echo   Starting %INSTANCE_COUNT% Embedder Instances
echo ========================================
echo.
echo Configuration:
echo   - Instances: %INSTANCE_COUNT%
echo   - Base Port: %BASE_PORT%
echo   - CLIP Device: %CLIP_DEVICE%
echo.

REM -------- Start each instance --------
for /L %%i in (1,1,%INSTANCE_COUNT%) do (
    set /a PORT=%BASE_PORT% + %%i - 1
    set INSTANCE_NUM=%%i
    
    echo [*] Starting embedder instance %%i on port !PORT!...
    start "FaceSearch-Embedder-!INSTANCE_NUM!" /D "%SCRIPT_DIR%" cmd /k "title FaceSearch-Embedder-!INSTANCE_NUM! && set PORT=!PORT! && set INSTANCE_NUM=!INSTANCE_NUM! && set CLIP_DEVICE=%CLIP_DEVICE% && call start-instance.bat"
    timeout /t 2 /nobreak >nul
)

echo.
echo ========================================
echo   All embedder instances are starting!
echo ========================================
echo.
echo Instances:
for /L %%i in (1,1,%INSTANCE_COUNT%) do (
    set /a PORT=%BASE_PORT% + %%i - 1
    echo   - Instance %%i: http://localhost:!PORT!
)
echo.
echo Check the opened windows for service status.
echo.
pause

