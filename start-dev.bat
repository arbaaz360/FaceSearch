@echo off
setlocal EnableDelayedExpansion
title FaceSearch - Development Mode (Hot Reload)

REM -------- Paths --------
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

echo ========================================
echo   FaceSearch - Development Mode
echo   (Hot Reload Enabled)
echo ========================================
echo.
echo This will start services with auto-reload:
echo   - Frontend: Hot reload (Vite HMR) - changes appear instantly
echo   - API: Auto-restart on .NET file changes
echo   - Worker: Auto-restart on .NET file changes
echo   - Embedder: Manual restart needed (Python)
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
echo [2/5] Starting Python Embedder service...
start "FaceSearch-Embedder-1" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port 8090 -ClipDevice cuda
start "FaceSearch-Embedder-2" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port 8091 -ClipDevice cuda
start "FaceSearch-Embedder-3" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port 8092 -ClipDevice cuda
timeout /t 5 /nobreak >nul
echo [CHECK] Waiting for embedder health (8090-8092)...
for %%P in (8090 8091 8092) do (
    powershell -NoProfile -Command ^
        "$u='http://localhost:%%P/_status'; $deadline=(Get-Date).AddSeconds(240); while((Get-Date) -lt $deadline){ try { $res=Invoke-RestMethod -Uri $u -TimeoutSec 5; Write-Host \"[OK] Embedder on %%P (clip=$($res.clip_device); face=$($res.face_device))\"; exit 0 } catch { Start-Sleep -Seconds 3 } }; Write-Warning 'Embedder did not respond on port %%P within 240s'; exit 1"
    if errorlevel 1 (
        echo [WARN] Embedder health check failed on port %%P. See embedder logs.
    )
)
echo.

REM -------- Step 3: Start .NET API with WATCH --------
echo [3/5] Starting .NET API (WATCH MODE - auto-restart on changes)...
start "FaceSearch-API" /D "%SCRIPT_DIR%FaceSearch" cmd /k "title FaceSearch-API && dotnet watch run"
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

REM -------- Step 4: Start .NET Worker with WATCH --------
echo [4/5] Starting .NET Worker (WATCH MODE - auto-restart on changes)...
start "FaceSearch-Worker" /D "%SCRIPT_DIR%Workers.Indexer" cmd /k "title FaceSearch-Worker && dotnet watch run"
timeout /t 2 /nobreak >nul
echo [OK] Worker starting with watch mode (check Worker window)
echo.

REM -------- Step 5: Start React Frontend (HMR already enabled) --------
echo [5/5] Starting React Frontend (HMR enabled - instant updates)...
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
echo   Development Mode Active!
echo ========================================
echo.
echo Services:
echo   - MongoDB:     http://localhost:27017
echo   - Qdrant:      http://localhost:6333
echo   - Embedder:    http://localhost:8090
echo   - API:         http://localhost:5240 (WATCH MODE)
echo   - Swagger UI:  http://localhost:5240/swagger
echo   - React UI:    http://localhost:3000 (HMR ENABLED)
echo   - Review UI:   http://localhost:5240/review.html
echo.
echo ========================================
echo   HOT RELOAD INFO:
echo ========================================
echo   Frontend:  Changes appear instantly (no refresh needed)
echo   Backend:   Auto-restarts when you save .cs files
echo   Worker:    Auto-restarts when you save .cs files
echo   Embedder:  Manual restart needed (Ctrl+C and restart)
echo.
echo   TIP: Keep this window open and just save your files!
echo        The services will automatically reload.
echo.
echo To stop all services:
echo   1. Close all service windows (Ctrl+C in each)
echo   2. Run: docker-compose down
echo.
pause

