@echo off
setlocal EnableDelayedExpansion
title FaceSearch Embedder

REM -------- Paths --------
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

REM -------- Config --------
set PORT=8090
REM Choose CLIP backend: dml | cuda | cpu
set CLIP_DEVICE=dml

REM -------- Python venv --------
if not exist ".venv" (
  echo [*] Creating venv...
  py -3.11 -m venv .venv
)
call .venv\Scripts\activate

echo [*] Upgrading pip...
python -m pip install --upgrade pip

echo [*] Installing base requirements...
pip install -r requirements.txt

REM -------- GPU backends --------
REM InsightFace on CUDA (ONNXRuntime GPU)
echo [*] Ensuring onnxruntime-gpu...
pip uninstall -y onnxruntime onnxruntime-directml >NUL 2>&1
pip install onnxruntime-gpu==1.20.0

REM CLIP on DirectML (PyTorch DML)
echo [*] Ensuring torch-directml...
pip uninstall -y torch torchvision >NUL 2>&1
pip install -f https://download.pytorch.org/whl/nightly/directml torch-directml

REM -------- Run server --------
set LOG_STDOUT=%SCRIPT_DIR%embedder.log
set LOG_STDERR=%SCRIPT_DIR%embedder.err.log

echo [*] Starting server on port %PORT% (CLIP_DEVICE=%CLIP_DEVICE%)...
set CLIP_DEVICE=%CLIP_DEVICE%
python main.py 1>>"%LOG_STDOUT%" 2>>"%LOG_STDERR%"
