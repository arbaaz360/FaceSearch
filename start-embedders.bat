@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Starting Embedder Instances

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
    start "FaceSearch-Embedder-!INSTANCE_NUM!" /D "%SCRIPT_DIR%embedder" cmd /k "title FaceSearch-Embedder-!INSTANCE_NUM! && set PORT=!PORT! && set INSTANCE_NUM=!INSTANCE_NUM! && set CLIP_DEVICE=%CLIP_DEVICE% && call start-instance.bat"
    timeout /t 2 /nobreak >nul
)

echo [CHECK] Waiting for embedder health responses...
set HEALTH_FAIL=0
for /L %%i in (1,1,%INSTANCE_COUNT%) do (
    set /a PORT=%BASE_PORT% + %%i - 1
    set INSTANCE_NUM=%%i
    powershell -NoProfile -Command "$u='http://localhost:!PORT!/_status'; $deadline=(Get-Date).AddSeconds(240); while((Get-Date) -lt $deadline){ try { $res=Invoke-RestMethod -Uri $u -TimeoutSec 5; Write-Host \"[OK] Embedder !INSTANCE_NUM! healthy (clip=$($res.clip_device); face=$($res.face_device))\"; exit 0 } catch { Start-Sleep -Seconds 3 } }; Write-Warning 'Embedder !INSTANCE_NUM! did not respond on port !PORT! within 240s'; exit 1"
    if errorlevel 1 (
        echo [WARN] Embedder !INSTANCE_NUM! health check failed. See embedder\embedder-!INSTANCE_NUM!.log.
        set HEALTH_FAIL=1
    )
)
if "!HEALTH_FAIL!"=="0" (
    echo [OK] All embedder instances responded to /_status.
) else (
    echo [WARN] One or more embedders failed health checks.
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
echo To stop embedders, close the windows or run: stop-embedders.bat
echo.
pause

