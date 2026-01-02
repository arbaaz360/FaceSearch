@echo off
setlocal
set SCRIPT_DIR=%~dp0
set FRONTEND_PORT=3000
set FRONTEND_HEALTH_URL=http://localhost:%FRONTEND_PORT%/
set FRONTEND_URL=http://localhost:%FRONTEND_PORT%/fast-search
cd /d "%SCRIPT_DIR%"

echo ========================================
echo   Fast Face Indexer / Search
echo ========================================
echo.

set EMBEDDER_PORTS=8090 8091
echo [1/5] Starting embedders on %EMBEDDER_PORTS% (CUDA)...
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
echo [2/5] Building FastSearch API + FastIndexer [Release]...
dotnet build "%SCRIPT_DIR%FastSearch.FastApi\FastSearch.FastApi.csproj" -c Release
if errorlevel 1 (
    echo [ERROR] Build failed for FastSearch API.
    goto :fail
)
dotnet build "%SCRIPT_DIR%Workers.FastIndexer\Workers.FastIndexer.csproj" -c Release
if errorlevel 1 (
    echo [ERROR] Build failed for FastIndexer.
    goto :fail
)
echo.

echo [3/5] Starting FastSearch API (http://localhost:5251) [Release]...
start "FastSearch-API" /D "%SCRIPT_DIR%FastSearch.FastApi" cmd /k "title FastSearch-API && dotnet run -c Release --no-build --urls http://localhost:5251"
timeout /t 2 /nobreak >nul

REM Start Fast Indexer
echo [4/5] Starting FastIndexer worker [Release]...
start "FastIndexer-Worker" /D "%SCRIPT_DIR%Workers.FastIndexer" cmd /k "title FastIndexer-Worker && dotnet run -c Release --no-build"
timeout /t 2 /nobreak >nul

REM Frontend for fast search
echo [5/5] Starting Frontend (dev) ...
where npm.cmd >nul 2>&1
if errorlevel 1 (
    echo [ERROR] npm.cmd not found. Install Node.js (LTS) to run the frontend.
    goto :fail
)

if not exist "%SCRIPT_DIR%out" mkdir "%SCRIPT_DIR%out"
set FRONTEND_LOG_OUT=%SCRIPT_DIR%out\fast-frontend.out.log
set FRONTEND_LOG_ERR=%SCRIPT_DIR%out\fast-frontend.err.log
del /q "%FRONTEND_LOG_OUT%" >nul 2>&1
del /q "%FRONTEND_LOG_ERR%" >nul 2>&1

REM Pick a free port starting at %FRONTEND_PORT% (avoids Vite strictPort failures)
for /f %%P in ('powershell -NoProfile -Command "$p=%FRONTEND_PORT%; $ports=[System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | ForEach-Object { $_.Port }; while($ports -contains $p){$p++}; Write-Output $p"') do set FRONTEND_PORT=%%P
set FRONTEND_HEALTH_URL=http://localhost:%FRONTEND_PORT%/
set FRONTEND_URL=http://localhost:%FRONTEND_PORT%/fast-search
echo [INFO] Frontend URL: %FRONTEND_URL%

if not exist "%SCRIPT_DIR%frontend\node_modules" (
    echo [INFO] Installing frontend dependencies (first time setup)...
    pushd "%SCRIPT_DIR%frontend"
    call npm install
    if errorlevel 1 (
        echo [ERROR] Failed to install frontend dependencies.
        popd
        goto :fail
    )
    popd
)
echo [INFO] Frontend log: %FRONTEND_LOG_OUT%
echo [INFO] Frontend err: %FRONTEND_LOG_ERR%
powershell -NoProfile -Command "& { $npm=(Get-Command npm.cmd -ErrorAction Stop).Source; $args=@('run','dev','--','--host','--port','%FRONTEND_PORT%','--strictPort'); Start-Process -FilePath $npm -WorkingDirectory '%SCRIPT_DIR%frontend' -ArgumentList $args -RedirectStandardOutput '%FRONTEND_LOG_OUT%' -RedirectStandardError '%FRONTEND_LOG_ERR%' -WindowStyle Normal }"
timeout /t 2 /nobreak >nul
echo [CHECK] Waiting for frontend (%FRONTEND_HEALTH_URL% )...
powershell -NoProfile -Command "& { $u='%FRONTEND_HEALTH_URL%'; $deadline=(Get-Date).AddSeconds(60); while((Get-Date) -lt $deadline){ try { Invoke-RestMethod -Uri $u -TimeoutSec 5 | Out-Null; Write-Host ('[OK] Frontend responding on {0}' -f $u); exit 0 } catch { Start-Sleep -Seconds 3 } }; Write-Warning ('Frontend did not respond at {0} within 60s' -f $u); exit 1 }"
if errorlevel 1 (
    echo [WARN] Frontend health check failed. Check %FRONTEND_LOG_ERR% for errors.
    start "" "%FRONTEND_HEALTH_URL%"
) else (
    start "" "%FRONTEND_URL%"
)

echo [OK] Fast pipeline starting. UI: %FRONTEND_URL%
echo Press any key to close this launcher window.
pause >nul
goto :eof

:fail
echo.
echo [INFO] Keeping window open so you can read the error above.
pause
