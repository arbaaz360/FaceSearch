@echo off
setlocal
set SCRIPT_DIR=%~dp0
set FRONTEND_PORT=3000
set FRONTEND_URL=http://localhost:%FRONTEND_PORT%/fast-search
cd /d "%SCRIPT_DIR%"

echo ========================================
echo   Fast Face Indexer / Search
echo ========================================
echo.

set EMBEDDER_PORTS=8090 8091
echo [1/4] Starting embedders on %EMBEDDER_PORTS% (CUDA)...
for %%P in (%EMBEDDER_PORTS%) do (
    start "Fast-Embedder-%%P" /D "%SCRIPT_DIR%embedder" powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%embedder\start.ps1" -Port %%P -ClipDevice cuda
    timeout /t 2 /nobreak >nul
)

echo [CHECK] Waiting for embedders health...
powershell -NoProfile -Command "& { $ports=@(8090,8091); $deadline=(Get-Date).AddSeconds(120); foreach($p in $ports){ $u=\"http://localhost:$p/_status\"; $ok=$false; while((Get-Date) -lt $deadline){ try { $res=Invoke-RestMethod -Uri $u -TimeoutSec 5; Write-Host (\"[OK] Embedder {0} online (clip_device={1}; face_device={2})\" -f $p,$res.clip_device,$res.face_device); $ok=$true; break } catch { Start-Sleep -Seconds 3 } }; if(-not $ok){ Write-Warning (\"Embedder did not respond on port {0} within 120s\" -f $p); exit 1 } }; exit 0 }"
if errorlevel 1 (
    echo [ERROR] Embedder not healthy; aborting.
    goto :fail
)
echo.

REM Start Fast API
echo [2/4] Starting FastSearch API (http://localhost:5251)...
start "FastSearch-API" /D "%SCRIPT_DIR%FastSearch.FastApi" cmd /k "title FastSearch-API && dotnet run --urls http://localhost:5251"
timeout /t 2 /nobreak >nul

REM Start Fast Indexer
echo [3/4] Starting FastIndexer worker...
start "FastIndexer-Worker" /D "%SCRIPT_DIR%Workers.FastIndexer" cmd /k "title FastIndexer-Worker && dotnet run"
timeout /t 2 /nobreak >nul

REM Frontend for fast search
echo [4/4] Starting Frontend (dev) on %FRONTEND_URL% ...
if not exist "%SCRIPT_DIR%frontend\node_modules" (
    echo [INFO] Installing frontend dependencies (first time setup)...
    pushd "%SCRIPT_DIR%frontend"
    call npm install
    popd
)
start "Fast-Frontend" /D "%SCRIPT_DIR%frontend" cmd /k "title Fast-Frontend && npm run dev -- --host --port %FRONTEND_PORT% --strictPort"
timeout /t 2 /nobreak >nul
echo [CHECK] Waiting for frontend (%FRONTEND_URL% )...
powershell -NoProfile -Command "& { $u='%FRONTEND_URL%'; $deadline=(Get-Date).AddSeconds(60); while((Get-Date) -lt $deadline){ try { Invoke-RestMethod -Uri $u -TimeoutSec 5 | Out-Null; Write-Host ('[OK] Frontend responding on {0}' -f $u); exit 0 } catch { Start-Sleep -Seconds 3 } }; Write-Warning ('Frontend did not respond at {0} within 60s' -f $u); exit 1 }"
start "" "%FRONTEND_URL%"

echo [OK] Fast pipeline starting. UI: %FRONTEND_URL%
echo Press any key to close this launcher window.
pause >nul
goto :eof

:fail
echo.
echo [INFO] Keeping window open so you can read the error above.
pause
