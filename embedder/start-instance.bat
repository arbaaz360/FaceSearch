@echo off
setlocal
title FaceSearch Embedder Instance %INSTANCE_NUM%

REM -------- Paths --------
set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

REM -------- Config --------
if "%PORT%"=="" (
    echo [ERROR] PORT environment variable not set
    pause
    exit /b 1
)
if "%CLIP_DEVICE%"=="" (
    set "CLIP_DEVICE=dml"
)
set "NUMPY_PIN=numpy==2.2.6"
set "CONSTRAINTS_FILE=%SCRIPT_DIR%pip-constraints.txt"
set "MARKER_FILE=%SCRIPT_DIR%.venv\.deps_ready"

REM -------- Force rebuild if requested --------
if "%FORCE_REINSTALL%"=="1" (
    if exist "%MARKER_FILE%" del "%MARKER_FILE%"
)

REM -------- Python venv --------
if not exist ".venv" (
  echo [*] Creating venv...
  py -3.11 -m venv .venv
)
call .venv\Scripts\activate

if not exist "%CONSTRAINTS_FILE%" (
    echo %NUMPY_PIN%>"%CONSTRAINTS_FILE%"
)

REM -------- Install only when marker is missing --------
if not exist "%MARKER_FILE%" (
    echo [*] Upgrading pip...
    python -m pip install --upgrade pip || goto :fail

    echo [*] Installing base requirements (with numpy pin)...
    pip install --no-cache-dir -r requirements.txt -c "%CONSTRAINTS_FILE%" || goto :fail

    echo [*] Ensuring matplotlib is installed cleanly...
    pip uninstall -y matplotlib >NUL 2>&1
    pip install --no-cache-dir --force-reinstall matplotlib -c "%CONSTRAINTS_FILE%" || goto :fail

    REM -------- GPU backends --------
    echo [*] Ensuring onnxruntime-gpu...
    pip uninstall -y onnxruntime onnxruntime-directml >NUL 2>&1
    pip install --no-cache-dir --force-reinstall onnxruntime-gpu==1.20.0 -c "%CONSTRAINTS_FILE%" || goto :fail

    echo [*] Ensuring torch-directml...
    pip uninstall -y torch torchvision torch-directml >NUL 2>&1
    if exist ".venv\Lib\site-packages\torch" (
        rmdir /s /q ".venv\Lib\site-packages\torch" >NUL 2>&1
    )
    pip install --force-reinstall --no-cache-dir -f https://download.pytorch.org/whl/nightly/directml torch-directml -c "%CONSTRAINTS_FILE%" || goto :fail

    echo [*] Ensuring numpy stays at %NUMPY_PIN%...
    pip install --no-cache-dir -c "%CONSTRAINTS_FILE%" %NUMPY_PIN% || goto :fail

    echo [*] Checking installed packages for conflicts...
    pip check || goto :fail

    echo [*] Writing venv readiness marker...
    echo ok>"%MARKER_FILE%"
)

:verify
echo [*] Verifying imports (torch/open_clip/cv2/onnxruntime/matplotlib)...
python -c "import numpy, torch, onnxruntime, cv2, open_clip, matplotlib; print('numpy', numpy.__version__, '| torch', torch.__version__, '| onnx', onnxruntime.__version__, '| providers', onnxruntime.get_available_providers(), '| matplotlib', matplotlib.__version__)" || goto :fail

REM -------- Run server --------
set "LOG_STDOUT=%SCRIPT_DIR%embedder-%INSTANCE_NUM%.log"
set "LOG_STDERR=%SCRIPT_DIR%embedder-%INSTANCE_NUM%.err.log"

echo [*] Starting embedder instance %INSTANCE_NUM% on port %PORT% (CLIP_DEVICE=%CLIP_DEVICE%)...
set CLIP_DEVICE=%CLIP_DEVICE%
python main.py 1>>"%LOG_STDOUT%" 2>>"%LOG_STDERR%"
goto :eof

:fail
echo.
echo [ERROR] Embedder instance setup failed. See messages above.
exit /b 1
