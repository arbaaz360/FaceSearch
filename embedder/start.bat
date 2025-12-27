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
set NUMPY_PIN=numpy==2.2.6
set CONSTRAINTS_FILE=%SCRIPT_DIR%pip-constraints.txt
set MARKER_FILE=%SCRIPT_DIR%.venv\.deps_ready
set FORCE_REINSTALL=%FORCE_REINSTALL%

REM -------- Python venv --------
if not exist ".venv" (
  echo [*] Creating venv...
  py -3.11 -m venv .venv
)
call .venv\Scripts\activate

if not exist "%CONSTRAINTS_FILE%" (
    echo %NUMPY_PIN%>"%CONSTRAINTS_FILE%"
)

REM -------- Optional fast path --------
if "%FORCE_REINSTALL%"=="" (
    if exist "%MARKER_FILE%" (
        echo [*] Using existing venv (set FORCE_REINSTALL=1 to rebuild)...
        goto :verify
    )
)

echo [*] Upgrading pip...
python -m pip install --upgrade pip
if errorlevel 1 goto :fail

echo [*] Installing base requirements (with numpy pin)...
pip install --no-cache-dir -r requirements.txt -c "%CONSTRAINTS_FILE%"
if errorlevel 1 goto :fail

echo [*] Ensuring matplotlib is installed cleanly...
pip uninstall -y matplotlib >NUL 2>&1
pip install --no-cache-dir --force-reinstall matplotlib -c "%CONSTRAINTS_FILE%"
if errorlevel 1 goto :fail

REM -------- GPU backends --------
REM InsightFace on CUDA (ONNXRuntime GPU)
echo [*] Ensuring onnxruntime-gpu...
pip uninstall -y onnxruntime onnxruntime-directml >NUL 2>&1
pip install --no-cache-dir --force-reinstall onnxruntime-gpu==1.20.0 -c "%CONSTRAINTS_FILE%"
if errorlevel 1 goto :fail

REM CLIP on DirectML (PyTorch DML)
echo [*] Ensuring torch-directml...
REM Force uninstall and reinstall to handle corrupted installations
pip uninstall -y torch torchvision torch-directml >NUL 2>&1
REM Clean up any remaining torch files
if exist ".venv\Lib\site-packages\torch" (
    rmdir /s /q ".venv\Lib\site-packages\torch" >NUL 2>&1
)
REM Install torch-directml with force reinstall
pip install --force-reinstall --no-cache-dir -f https://download.pytorch.org/whl/nightly/directml torch-directml -c "%CONSTRAINTS_FILE%"
if errorlevel 1 goto :fail

REM Re-apply numpy pin to avoid torch pulling 2.4.x
echo [*] Ensuring numpy stays at %NUMPY_PIN%...
pip install --no-cache-dir -c "%CONSTRAINTS_FILE%" %NUMPY_PIN%
if errorlevel 1 goto :fail

echo [*] Checking installed packages for conflicts...
pip check
if errorlevel 1 goto :fail

echo [*] Writing venv readiness marker...
echo ok>"%MARKER_FILE%"

:verify
echo [*] Verifying imports (torch/open_clip/cv2/onnxruntime/matplotlib)...
python -c "import numpy, torch, onnxruntime, cv2, open_clip, matplotlib; print('numpy', numpy.__version__, '| torch', torch.__version__, '| onnx', onnxruntime.__version__, '| providers', onnxruntime.get_available_providers(), '| matplotlib', matplotlib.__version__)" || goto :fail

REM -------- Run server --------
set LOG_STDOUT=%SCRIPT_DIR%embedder.log
set LOG_STDERR=%SCRIPT_DIR%embedder.err.log

echo [*] Starting server on port %PORT% (CLIP_DEVICE=%CLIP_DEVICE%)...
set CLIP_DEVICE=%CLIP_DEVICE%
python main.py 1>>"%LOG_STDOUT%" 2>>"%LOG_STDERR%"

goto :eof

:fail
echo.
echo [ERROR] Embedder environment setup failed. See messages above.
echo         Try deleting .venv and re-running if corruption persists.
exit /b 1
