@echo off
echo Building Jpg6Importer...
dotnet build

if %ERRORLEVEL% NEQ 0 (
    echo Build failed!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Running Jpg6Importer...
dotnet run --project Jpg6Importer.csproj

pause

