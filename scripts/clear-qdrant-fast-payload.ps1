param(
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$Collection = "faces_fast_512"
)

$ErrorActionPreference = "Stop"

Write-Host "Clearing payload for collection '$Collection' at $QdrantUrl ..." -ForegroundColor Cyan
$body = @{ filter = @{} } | ConvertTo-Json -Depth 3
Invoke-RestMethod -Method Post -Uri "$QdrantUrl/collections/$Collection/points/payload/clear" -ContentType "application/json" -Body $body
Write-Host "[OK] Payload cleared. Vectors and IDs are untouched." -ForegroundColor Green
