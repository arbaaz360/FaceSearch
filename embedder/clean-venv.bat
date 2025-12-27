@echo off
REM Batch script to clean the Python virtual environment
REM Usage: clean-venv.bat

echo Cleaning Python virtual environment...
if exist ".venv" (
    rmdir /s /q ".venv"
    echo [OK] .venv directory removed
) else (
    echo [INFO] .venv directory does not exist
)

echo Done!


