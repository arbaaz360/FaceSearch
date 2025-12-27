# PowerShell script to clean the Python virtual environment
# Usage: .\clean-venv.ps1

Write-Host "Cleaning Python virtual environment..."
if (Test-Path .venv) {
    Remove-Item -Recurse -Force .venv
    Write-Host "[OK] .venv directory removed"
} else {
    Write-Host "[INFO] .venv directory does not exist"
}

Write-Host "Done!"


