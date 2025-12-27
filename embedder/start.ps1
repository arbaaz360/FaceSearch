param(
    [int]$Port = 8090,
    [string]$ClipDevice = "cuda", # cuda | dml | cpu
    [switch]$ForceReinstall
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$VenvPath = Join-Path $ScriptDir ".venv"
$Marker = Join-Path $VenvPath ".deps_ready"
$DeviceMarker = Join-Path $VenvPath ".device_mode"
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

if (Test-Path $Marker) {
    $clip = $ClipDevice.ToLowerInvariant()
    if (Test-Path $DeviceMarker) {
        $saved = Get-Content $DeviceMarker -ErrorAction SilentlyContinue
        if ($saved -ne $clip) {
            Write-Host "[*] Detected previous build for '$saved'. Rebuilding for '$clip'..." -ForegroundColor Yellow
            Remove-Item $Marker -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not (Test-Path $Marker)) {
    Write-Host "[*] Upgrading pip..." -ForegroundColor Cyan
    & $VenvPython -m pip install --upgrade pip

    $clip = $ClipDevice.ToLowerInvariant()

    if ($clip -eq "cuda") {
        Write-Host "[*] Installing torch/torchvision (CUDA 12.1)..." -ForegroundColor Cyan
        & $VenvPip install --no-cache-dir torch==2.4.1+cu121 torchvision==0.19.1+cu121 --index-url https://download.pytorch.org/whl/cu121
        Write-Host "[*] Installing ONNX Runtime GPU for InsightFace..." -ForegroundColor Cyan
        & $VenvPip install --no-cache-dir onnxruntime-gpu==1.20.0
    }
    elseif ($clip -eq "dml") {
        Write-Host "[*] Installing torch-directml (GPU via DirectML)..." -ForegroundColor Cyan
        & $VenvPip install --no-cache-dir torch-directml==2.4.0
        Write-Host "[*] Installing ONNX Runtime DirectML for InsightFace..." -ForegroundColor Cyan
        & $VenvPip install --no-cache-dir onnxruntime-directml==1.20.0
    }
    else {
        Write-Host "[*] Installing torch/torchvision (CPU baseline)..." -ForegroundColor Cyan
        & $VenvPip install --no-cache-dir torch==2.4.1 torchvision==0.19.1 --index-url https://download.pytorch.org/whl/cpu
        Write-Host "[*] Installing ONNX Runtime (CPU)..." -ForegroundColor Cyan
        & $VenvPip install --no-cache-dir onnxruntime==1.20.0
    }

    Write-Host "[*] Installing base requirements..." -ForegroundColor Cyan
    & $VenvPip install --no-cache-dir -r requirements.txt -c $Constraints

    Write-Host "[*] Ensuring numpy pin..." -ForegroundColor Cyan
    & $VenvPip install --no-cache-dir -c $Constraints $NumpyPin

    Write-Host "[*] Checking installed packages..." -ForegroundColor Cyan
    & $VenvPip check

    Set-Content -Path $Marker -Value "ok" -NoNewline
    Set-Content -Path $DeviceMarker -Value $clip -NoNewline
}
else {
    # If deps are marked ready but device mode changed, warn the user
    $clip = $ClipDevice.ToLowerInvariant()
    if (Test-Path $DeviceMarker) {
        $saved = Get-Content $DeviceMarker -ErrorAction SilentlyContinue
        if ($saved -ne $clip) {
            Write-Warning "Environment was built for '$saved' but requested '$clip'. Run with -ForceReinstall to rebuild for the new device."
        }
    }
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
