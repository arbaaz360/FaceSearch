@echo off
echo ========================================
echo   FaceSearch - Stopping Embedder Instances
echo ========================================
echo.

echo [*] Stopping Python Embedder instances...
taskkill /IM python.exe /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq FaceSearch-Embedder-*" /F >nul 2>&1
echo [OK] Python Embedder instances stopped
echo.

echo ========================================
echo   Embedder instances stopped!
echo ========================================
echo.
pause

