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
start "FaceSearch-Embedder" /D "%SCRIPT_DIR%embedder" cmd /k "title FaceSearch-Embedder && call start.bat"
timeout /t 5 /nobreak >nul
echo [OK] Embedder service starting (check embedder window)
echo.

REM -------- Step 3: Start .NET API with WATCH --------
echo [3/5] Starting .NET API (WATCH MODE - auto-restart on changes)...
start "FaceSearch-API" /D "%SCRIPT_DIR%FaceSearch" cmd /k "title FaceSearch-API && dotnet watch run"
timeout /t 3 /nobreak >nul
echo [OK] API starting with watch mode (check API window)
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
start "FaceSearch-Frontend" /D "%SCRIPT_DIR%frontend" cmd /k "title FaceSearch-Frontend && npm run dev"
timeout /t 2 /nobreak >nul
echo [OK] Frontend starting with HMR (check Frontend window)
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

