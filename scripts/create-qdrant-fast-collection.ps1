param(
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$Collection = "faces_fast_512"
)

Write-Host "[*] Creating/ensuring Qdrant collection '$Collection' at $QdrantUrl ..." -ForegroundColor Cyan

$body = @{
    vectors = @{
        size = 512
        distance = "Cosine"
    }
    on_disk_payload = $true
    optimizer_config = @{
        default_segment_number = 2
        indexing_threshold = 20000
    }
    hnsw_config = @{
        m = 16
        ef_construct = 256
        full_scan_threshold = 5000
    }
    payload_schema = @{
        path = @{ type = "keyword" }
        note = @{ type = "text" }
        faceIndex = @{ type = "integer" }
    }
}

try {
    $resp = Invoke-RestMethod -Method PUT -Uri "$QdrantUrl/collections/$Collection" -Body ($body | ConvertTo-Json -Depth 6) -ContentType "application/json"
    if ($resp.status -eq "ok") {
        Write-Host "[OK] Collection ensured."
    } else {
        Write-Warning "[WARN] Unexpected response: $($resp | ConvertTo-Json -Depth 3)"
    }
} catch {
    Write-Warning "Failed to create collection: $($_.Exception.Message)"
}
