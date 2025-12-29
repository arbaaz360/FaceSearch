param(
    [int[]]$EmbedderPorts = @(8090, 8091),
    [string]$ClipDevice = "cuda",
    [int]$EmbedderTimeoutSeconds = 240,
    [int]$ApiTimeoutSeconds = 60,
    [int]$FrontendTimeoutSeconds = 60,
    [switch]$ForceReinstall
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

function Test-PortFree {
    param([int]$Port)
    try {
        $lines = netstat -ano | Select-String ":$Port\s"
        return ($lines.Count -eq 0)
    } catch {
        return $true
    }
}

function Wait-Http {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [int]$TimeoutSec = 60,
        [int]$DelaySec = 3,
        [string]$Name = "service"
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            return Invoke-RestMethod -Uri $Url -TimeoutSec 5
        } catch {
            Start-Sleep -Seconds $DelaySec
        }
    }
    throw "Timeout waiting for $Name at $Url"
}

Write-Host "======================================="
Write-Host "  FaceSearch - Starting All Services"
Write-Host "======================================="
Write-Host ""

# -------- Step 1: Docker infrastructure --------
Write-Host "[1/5] Starting Docker infrastructure (MongoDB + Qdrant)..."
try {
    docker-compose up -d | Out-Host
    Write-Host "[OK] Docker containers started"
} catch {
    Write-Error "[ERROR] Failed to start Docker containers. Make sure Docker Desktop is running."
    exit 1
}
Start-Sleep -Seconds 3
Write-Host ""

# -------- Step 2: Embedders --------
Write-Host "[2/5] Starting Python Embedder services..."
foreach ($p in $EmbedderPorts) {
    Write-Host ("   - Starting embedder on port {0} (CLIP_DEVICE={1})..." -f $p, $ClipDevice)
    $args = @("-NoProfile","-ExecutionPolicy","Bypass","-File","$scriptDir\embedder\start.ps1","-Port",$p,"-ClipDevice",$ClipDevice)
    if ($ForceReinstall) { $args += "-ForceReinstall" }
    Start-Process -FilePath "powershell" -ArgumentList $args -WorkingDirectory "$scriptDir\embedder" -WindowStyle Normal
    Start-Sleep -Seconds 2
}
Write-Host "[CHECK] Waiting for embedders to respond..."
$embedWarnings = @()
$embedSuccess = 0
foreach ($p in $EmbedderPorts) {
    try {
        $res = Wait-Http -Url "http://localhost:$p/_status" -TimeoutSec $EmbedderTimeoutSeconds -DelaySec 3 -Name "embedder $p"
        Write-Host ("[OK] Embedder {0} online (clip_device={1}; face_device={2})" -f $p, $res.clip_device, $res.face_device)
        $embedSuccess++
    } catch {
        Write-Warning ("Embedder health check failed on port {0}: {1}" -f $p, $_.Exception.Message)
        $embedWarnings += $p
    }
}
if ($embedSuccess -eq 0) {
    Write-Error "No embedder instances responded. Check the embedder windows for errors and restart. Aborting."
    exit 1
}
if ($embedWarnings.Count -gt 0) {
    Write-Error ("One or more embedders failed health checks: {0}. Fix and rerun start-all." -f ($embedWarnings -join ", "))
    exit 1
}
Write-Host ""

# -------- Step 3: API --------
if (-not (Test-PortFree 5240)) {
    Write-Error "Port 5240 is already in use. Close any existing FaceSearch API windows or stop the process using that port, then re-run start-all."
    exit 1
}
Write-Host "[3/5] Starting .NET API..."
Start-Process -FilePath "cmd.exe" -ArgumentList "/k","title FaceSearch-API && dotnet run" -WorkingDirectory "$scriptDir\FaceSearch" -WindowStyle Normal
Start-Sleep -Seconds 3
Write-Host "[CHECK] Waiting for API health (http://localhost:5240/healthz)..."
$apiWarning = $false
try {
    $res = Wait-Http -Url "http://localhost:5240/healthz" -TimeoutSec $ApiTimeoutSeconds -DelaySec 2 -Name "API"
    if ($res.ok -eq $true) {
        Write-Host "[OK] API healthy"
    } else {
        Write-Warning "[WARN] API responded but health payload not ok"
        $apiWarning = $true
    }
} catch {
    Write-Warning "[WARN] API health check failed: $($_.Exception.Message)"
    $apiWarning = $true
}
Write-Host ""

# -------- Step 4: Worker --------
Write-Host "[4/5] Starting .NET Worker (Indexer)..."
Start-Process -FilePath "cmd.exe" -ArgumentList "/k","title FaceSearch-Worker && dotnet run" -WorkingDirectory "$scriptDir\Workers.Indexer" -WindowStyle Normal
Start-Sleep -Seconds 2
Write-Host "[OK] Worker starting (check Worker window)"
Write-Host ""

# -------- Step 5: Frontend --------
Write-Host "[5/5] Starting React Frontend..."
$frontendDir = Join-Path $scriptDir "frontend"
if (-not (Test-Path (Join-Path $frontendDir "node_modules"))) {
    Write-Host "[INFO] Installing frontend dependencies (first time setup)..."
    try {
        Push-Location $frontendDir
        npm install | Out-Host
    } catch {
        Pop-Location
        Write-Warning "[WARN] Failed to install frontend dependencies. Frontend will be skipped."
        $frontendWarning = $true
    }
    Pop-Location
    if (-not $frontendWarning) {
        Write-Host "[OK] Frontend dependencies installed"
    }
}
if (-not $frontendWarning) {
    Start-Process -FilePath "cmd.exe" -ArgumentList "/k","title FaceSearch-Frontend && npm run dev -- --host --port 3000" -WorkingDirectory $frontendDir -WindowStyle Normal
    Start-Sleep -Seconds 2
    Write-Host "[CHECK] Waiting for frontend (http://localhost:3000)..."
    try {
        Wait-Http -Url "http://localhost:3000" -TimeoutSec $FrontendTimeoutSeconds -DelaySec 3 -Name "frontend" | Out-Null
        Write-Host "[OK] Frontend responding on :3000"
    } catch {
        Write-Warning "[WARN] Frontend health check failed: $($_.Exception.Message)"
        $frontendWarning = $true
    }
    Write-Host ""
}

Write-Host "======================================="
Write-Host "  All services are starting!"
Write-Host "======================================="
Write-Host ""
Write-Host "Services:"
Write-Host "  - MongoDB:     http://localhost:27017"
Write-Host "  - Qdrant:      http://localhost:6333"
Write-Host "  - Embedders:   " ($EmbedderPorts | ForEach-Object { "http://localhost:$_" } -join ", ")
Write-Host "  - API:         http://localhost:5240"
Write-Host "  - Swagger UI:  http://localhost:5240/swagger"
Write-Host "  - React UI:    http://localhost:3000"
Write-Host "  - Review UI:   http://localhost:5240/review.html"
Write-Host ""

if ($embedWarnings.Count -gt 0 -or $apiWarning -or $frontendWarning) {
    Write-Warning "Some health checks failed. Check the opened windows for details."
}
Write-Host "Close this window after reviewing the logs. To stop all services, close the opened windows and run: docker-compose down"
