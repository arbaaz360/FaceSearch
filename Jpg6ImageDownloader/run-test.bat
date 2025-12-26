@echo off
echo Building TestDownloader...
dotnet build TestDownloader.csproj

if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Running TestDownloader with sample URLs...
dotnet run --project TestDownloader.csproj

pause

