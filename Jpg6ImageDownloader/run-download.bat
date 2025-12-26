@echo off
echo Building Jpg6ImageDownloader...
dotnet build Jpg6ImageDownloader.csproj

if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Running Jpg6ImageDownloader...
dotnet run --project Jpg6ImageDownloader.csproj

pause

