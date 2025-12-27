param(
    [int]$Port = 8090,
    [string]$ClipDevice = "dml",
    [switch]$ForceReinstall
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$VenvPath = Join-Path $ScriptDir ".venv"
$Marker = Join-Path $VenvPath ".deps_ready"
$Constraints = Join-Path $ScriptDir "pip-constraints.txt"
$NumpyPin = "numpy==2.2.6"
$VenvPython = Join-Path $VenvPath "Scripts\python.exe"
$VenvPip = Join-Path $VenvPath "Scripts\pip.exe"

if ($ForceReinstall -and (Test-Path $VenvPath)) {
    Remove-Item $VenvPath -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $VenvPath)) {
    Write-Host "[*] Creating venv..." -ForegroundColor Cyan
    & py -3.11 -m venv $VenvPath
}

# Ensure we use the venv python/pip
$env:VIRTUAL_ENV = $VenvPath
$env:Path = "$($VenvPath)\Scripts;$env:Path"

if (-not (Test-Path $Constraints)) {
    Set-Content -Path $Constraints -Value $NumpyPin -NoNewline
}

if (-not (Test-Path $Marker)) {
    Write-Host "[*] Upgrading pip..." -ForegroundColor Cyan
    & $VenvPython -m pip install --upgrade pip

    Write-Host "[*] Installing base requirements..." -ForegroundColor Cyan
    & $VenvPip install --no-cache-dir -r requirements.txt -c $Constraints

    Write-Host "[*] Ensuring matplotlib..." -ForegroundColor Cyan
    & $VenvPip uninstall -y matplotlib | Out-Null
    & $VenvPip install --no-cache-dir --force-reinstall matplotlib -c $Constraints

    Write-Host "[*] Ensuring onnxruntime-gpu..." -ForegroundColor Cyan
    & $VenvPip uninstall -y onnxruntime onnxruntime-directml | Out-Null
    & $VenvPip install --no-cache-dir --force-reinstall onnxruntime-gpu==1.20.0 -c $Constraints

    Write-Host "[*] Installing torch/torchvision (CPU baseline)..." -ForegroundColor Cyan
    & $VenvPip uninstall -y torch torchvision torch-directml | Out-Null
    if (Test-Path "$VenvPath\Lib\site-packages\torch") {
        Remove-Item "$VenvPath\Lib\site-packages\torch" -Recurse -Force -ErrorAction SilentlyContinue
    }
    & $VenvPip install --no-cache-dir torch==2.4.1 torchvision==0.19.1 --index-url https://download.pytorch.org/whl/cpu

    Write-Host "[*] Ensuring numpy pin..." -ForegroundColor Cyan
    & $VenvPip install --no-cache-dir -c $Constraints $NumpyPin

    Write-Host "[*] Checking installed packages..." -ForegroundColor Cyan
    & $VenvPip check

    Set-Content -Path $Marker -Value "ok" -NoNewline
}

Write-Host "[*] Verifying imports..." -ForegroundColor Cyan
@'
import numpy, torch, onnxruntime, cv2, open_clip, matplotlib
print("numpy", numpy.__version__, "| torch", torch.__version__, "| onnx", onnxruntime.__version__, "| providers", onnxruntime.get_available_providers(), "| matplotlib", matplotlib.__version__)
'@ | & $VenvPython

$env:CLIP_DEVICE = $ClipDevice
$env:PORT = $Port
Write-Host "[*] Starting server on port $Port (CLIP_DEVICE=$ClipDevice)..." -ForegroundColor Green
& $VenvPython main.py
