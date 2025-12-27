@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Starting Services (API, Worker, Frontend)

REM -------- Paths --------
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

echo ========================================
echo   FaceSearch - Starting Services
echo ========================================
echo.
echo Note: This script starts Docker, API, Worker, and Frontend.
echo       Embedders are NOT started by this script.
echo       Run start-embedders.bat separately if needed.
echo.

REM -------- Step 1: Start Docker Infrastructure --------
echo [1/4] Starting Docker infrastructure (MongoDB + Qdrant)...
docker-compose up -d
if errorlevel 1 (
    echo [ERROR] Failed to start Docker containers. Make sure Docker Desktop is running.
    pause
    exit /b 1
)
echo [OK] Docker containers started
timeout /t 3 /nobreak >nul
echo.

REM -------- Step 2: Start .NET API --------
echo [2/4] Starting .NET API...
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

REM -------- Step 3: Start .NET Worker --------
echo [3/4] Starting .NET Worker (Indexer)...
start "FaceSearch-Worker" /D "%SCRIPT_DIR%Workers.Indexer" cmd /k "title FaceSearch-Worker && dotnet run"
timeout /t 2 /nobreak >nul
echo [OK] Worker starting (check Worker window)
echo.

REM -------- Step 4: Start React Frontend --------
echo [4/4] Starting React Frontend...
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
echo   Services are starting!
echo ========================================
echo.
echo Services:
echo   - MongoDB:     http://localhost:27017
echo   - Qdrant:      http://localhost:6333
echo   - API:         http://localhost:5240
echo   - Swagger UI:  http://localhost:5240/swagger
echo   - React UI:    http://localhost:3000
echo   - Review UI:   http://localhost:5240/review.html
echo.
echo NOTE: Embedder instances are NOT started by this script.
echo       Run start-embedders.bat to start embedders (ports 8090, 8091, 8092).
echo.
echo To stop all services:
echo   1. Close all service windows (Ctrl+C in each)
echo   2. Run: docker-compose down
echo   3. Run: stop-embedders.bat (if embedders are running)
echo.
pause

