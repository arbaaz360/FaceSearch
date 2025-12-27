@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Starting All Services

REM -------- Paths --------
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

echo ========================================
echo   FaceSearch - Starting All Services
echo ========================================
echo.

REM -------- Step 1: Start Docker Infrastructure --------
echo [1/5] Starting Docker infrastructure (MongoDB + Qdrant)...
docker-compose up -d
if errorlevel 1 (
    echo [ERROR] Failed to start Docker containers. Make sure Docker Desktop is running.
    pause
    exit /b 1
)
echo [OK] Docker containers started
timeout /t 3 /nobreak >nul
echo.

REM -------- Step 2: Start Python Embedder --------
echo [2/5] Starting Python Embedder service (3 instances)...
start "FaceSearch-Embedder-1" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port 8090 -ClipDevice dml
start "FaceSearch-Embedder-2" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port 8091 -ClipDevice dml
start "FaceSearch-Embedder-3" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port 8092 -ClipDevice dml
timeout /t 3 /nobreak >nul
echo [CHECK] Waiting for embedder health (http://localhost:8090/_status)...
powershell -NoProfile -Command ^
    "$u='http://localhost:8090/_status'; $deadline=(Get-Date).AddSeconds(240); while((Get-Date) -lt $deadline){ try { $res=Invoke-RestMethod -Uri $u -TimeoutSec 5; Write-Host \"[OK] Embedder online (clip_device=$($res.clip_device); face_device=$($res.face_device))\"; exit 0 } catch { Start-Sleep -Seconds 3 } }; Write-Warning 'Embedder did not respond on port 8090 within 240s'; exit 1"
if errorlevel 1 (
    echo [WARN] Embedder health check failed. See embedder\embedder.log or the embedder window.
) else (
    echo [OK] Embedder responded.
)
echo.

REM -------- Step 3: Start .NET API --------
echo [3/5] Starting .NET API...
start "FaceSearch-API" /D "%SCRIPT_DIR%FaceSearch" cmd /k "title FaceSearch-API && dotnet run"
timeout /t 3 /nobreak >nul
echo [CHECK] Waiting for API health (http://localhost:5240/healthz)...
powershell -NoProfile -Command ^
    "$u='http://localhost:5240/healthz'; $deadline=(Get-Date).AddSeconds(60); while((Get-Date) -lt $deadline){ try { $res=Invoke-RestMethod -Uri $u -TimeoutSec 5; if($res.ok -eq $true){ Write-Host '[OK] API healthy'; exit 0 } } catch { Start-Sleep -Seconds 2 } }; Write-Warning 'API not responding on port 5240 within 60s'; exit 1"
if errorlevel 1 (
    echo [WARN] API health check failed. Check the API window for errors.
) else (
    echo [OK] API responded.
)
echo.

REM -------- Step 4: Start .NET Worker --------
echo [4/5] Starting .NET Worker (Indexer)...
start "FaceSearch-Worker" /D "%SCRIPT_DIR%Workers.Indexer" cmd /k "title FaceSearch-Worker && dotnet run"
timeout /t 2 /nobreak >nul
echo [OK] Worker starting (check Worker window)
echo.

REM -------- Step 5: Start React Frontend --------
echo [5/5] Starting React Frontend...
if not exist "%SCRIPT_DIR%frontend\node_modules" (
    echo [INFO] Installing frontend dependencies (first time setup)...
    cd /d "%SCRIPT_DIR%frontend"
    call npm install
    if errorlevel 1 (
        echo [ERROR] Failed to install frontend dependencies.
        echo         Frontend will be skipped.
        cd /d "%SCRIPT_DIR%"
        goto :skip_frontend
    )
    cd /d "%SCRIPT_DIR%"
    echo [OK] Frontend dependencies installed
)
start "FaceSearch-Frontend" /D "%SCRIPT_DIR%frontend" cmd /k "title FaceSearch-Frontend && npm run dev -- --host --port 3000"
timeout /t 2 /nobreak >nul
echo [CHECK] Waiting for frontend (http://localhost:3000)...
powershell -NoProfile -Command ^
    "$u='http://localhost:3000'; $deadline=(Get-Date).AddSeconds(60); while((Get-Date) -lt $deadline){ try { Invoke-RestMethod -Uri $u -TimeoutSec 5 | Out-Null; Write-Host '[OK] Frontend responding on :3000'; exit 0 } catch { Start-Sleep -Seconds 3 } }; Write-Warning 'Frontend did not respond on port 3000 within 60s'; exit 1"
if errorlevel 1 (
    echo [WARN] Frontend health check failed. Check the Frontend window for errors.
) else (
    echo [OK] Frontend responded.
)
:skip_frontend
echo.

echo ========================================
echo   All services are starting!
echo ========================================
echo.
echo Services:
echo   - MongoDB:     http://localhost:27017
echo   - Qdrant:      http://localhost:6333
echo   - Embedder:    http://localhost:8090
echo   - API:         http://localhost:5240
echo   - Swagger UI:  http://localhost:5240/swagger
echo   - React UI:    http://localhost:3000
echo   - Review UI:   http://localhost:5240/review.html
echo.
echo Check the opened windows for service status.
echo.
echo To stop all services:
echo   1. Close all service windows (Ctrl+C in each)
echo   2. Run: docker-compose down
echo.
pause
