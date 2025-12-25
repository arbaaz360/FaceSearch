# Test script for niluferq ingestion
Write-Host "=== Testing niluferq Ingestion ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Inspect MongoDB data
Write-Host "[1/5] Inspecting MongoDB data for niluferq..." -ForegroundColor Yellow
$inspect = Invoke-RestMethod -Uri "http://localhost:5240/_diagnostics/embedder/instagram/inspect?username=niluferq" -Method Get
Write-Host "Inspection Results:" -ForegroundColor Green
$inspect | ConvertTo-Json -Depth 10
Write-Host ""

# Step 2: Check current album status
Write-Host "[2/5] Checking if album already exists..." -ForegroundColor Yellow
try {
    $album = Invoke-RestMethod -Uri "http://localhost:5240/api/albums/__niluferq__" -Method Get
    Write-Host "Album already exists: $($album | ConvertTo-Json)" -ForegroundColor Green
} catch {
    Write-Host "Album does not exist yet (this is expected)" -ForegroundColor Yellow
}
Write-Host ""

# Step 3: Check current images
Write-Host "[3/5] Checking current images for niluferq..." -ForegroundColor Yellow
$status = Invoke-RestMethod -Uri "http://localhost:5240/_diagnostics/embedder/album-status/__niluferq__" -Method Get
Write-Host "Current Status:" -ForegroundColor Green
$status | ConvertTo-Json
Write-Host ""

# Step 4: Reset ingestion status if needed
Write-Host "[4/5] Resetting ingestion status (if needed)..." -ForegroundColor Yellow
$resetBody = @{
    followingUsername = "niluferq"
    deleteImages = $true
} | ConvertTo-Json
try {
    $resetResult = Invoke-RestMethod -Uri "http://localhost:5240/api/instagram/reset" -Method Post -Body $resetBody -ContentType "application/json"
    Write-Host "Reset Result: $($resetResult | ConvertTo-Json)" -ForegroundColor Green
} catch {
    Write-Host "Reset failed or not needed: $($_.Exception.Message)" -ForegroundColor Yellow
}
Write-Host ""

# Step 5: Trigger ingestion
Write-Host "[5/5] Triggering Instagram ingestion for niluferq..." -ForegroundColor Yellow
$seedBody = @{
    followingUsername = "niluferq"
    includeVideos = $false
} | ConvertTo-Json
$seedResult = Invoke-RestMethod -Uri "http://localhost:5240/api/instagram/seed" -Method Post -Body $seedBody -ContentType "application/json"
Write-Host "Ingestion Result:" -ForegroundColor Green
$seedResult | ConvertTo-Json -Depth 5
Write-Host ""

Write-Host "=== Test Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Check worker logs to see if images are being processed"
Write-Host "2. Wait for all images to be processed (check album status endpoint)"
Write-Host "3. Verify album appears in /api/albums endpoint"
Write-Host ""

