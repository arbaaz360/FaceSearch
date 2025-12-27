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

if (-not (Test-Path $VenvPath)) {
    Write-Host "[*] Creating venv..." -ForegroundColor Cyan
    & py -3.11 -m venv $VenvPath
}

& (Join-Path $VenvPath "Scripts\activate.bat") | Out-Null

if (-not (Test-Path $Constraints)) {
    Set-Content -Path $Constraints -Value $NumpyPin -NoNewline
}

if ($ForceReinstall) {
    if (Test-Path $Marker) { Remove-Item $Marker -Force }
}

if (-not (Test-Path $Marker)) {
    Write-Host "[*] Upgrading pip..." -ForegroundColor Cyan
    python -m pip install --upgrade pip

    Write-Host "[*] Installing base requirements..." -ForegroundColor Cyan
    pip install --no-cache-dir -r requirements.txt -c $Constraints

    Write-Host "[*] Ensuring matplotlib..." -ForegroundColor Cyan
    pip uninstall -y matplotlib | Out-Null
    pip install --no-cache-dir --force-reinstall matplotlib -c $Constraints

    Write-Host "[*] Ensuring onnxruntime-gpu..." -ForegroundColor Cyan
    pip uninstall -y onnxruntime onnxruntime-directml | Out-Null
    pip install --no-cache-dir --force-reinstall onnxruntime-gpu==1.20.0 -c $Constraints

    Write-Host "[*] Ensuring torch-directml..." -ForegroundColor Cyan
    pip uninstall -y torch torchvision torch-directml | Out-Null
    if (Test-Path "$VenvPath\Lib\site-packages\torch") {
        Remove-Item "$VenvPath\Lib\site-packages\torch" -Recurse -Force -ErrorAction SilentlyContinue
    }
    pip install --force-reinstall --no-cache-dir -f https://download.pytorch.org/whl/nightly/directml torch-directml -c $Constraints

    Write-Host "[*] Ensuring numpy pin..." -ForegroundColor Cyan
    pip install --no-cache-dir -c $Constraints $NumpyPin

    Write-Host "[*] Checking installed packages..." -ForegroundColor Cyan
    pip check

    Set-Content -Path $Marker -Value "ok" -NoNewline
}

Write-Host "[*] Verifying imports..." -ForegroundColor Cyan
python - <<'PY'
import numpy, torch, onnxruntime, cv2, open_clip, matplotlib
print("numpy", numpy.__version__, "| torch", torch.__version__, "| onnx", onnxruntime.__version__, "| providers", onnxruntime.get_available_providers(), "| matplotlib", matplotlib.__version__)
PY

$env:CLIP_DEVICE = $ClipDevice
$env:PORT = $Port
Write-Host "[*] Starting server on port $Port (CLIP_DEVICE=$ClipDevice)..." -ForegroundColor Green
python main.py
