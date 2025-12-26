# Create Qdrant Collections
$baseUrl = "http://localhost:6333"

$collections = @(
    "clip_512",
    "faces_arcface_512",
    "faces_review_512",
    "album_dominants"
)

$payloadSchema = @{
    albumId = @{ type = "keyword" }
    account = @{ type = "keyword" }
    tags    = @{ type = "keyword"; multi = $true }
}

$bodyObj = @{
    vectors = @{
        size     = 512
        distance = "Cosine"
        on_disk  = $true
    }
    hnsw_config = @{
        m            = 16
        ef_construct = 256
    }
    optimizers_config = @{
        default_segment_number = 2
    }
    payload_schema = $payloadSchema
}

$bodyJson = $bodyObj | ConvertTo-Json -Depth 6

foreach ($collectionName in $collections) {
    $url = "$baseUrl/collections/$collectionName"
    Write-Host "Creating collection: $collectionName..."
    
    try {
        $response = Invoke-RestMethod -Uri $url -Method Put -Body $bodyJson -ContentType "application/json"
        Write-Host "Created: $collectionName" -ForegroundColor Green
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 409) {
            Write-Host "Collection $collectionName already exists" -ForegroundColor Yellow
        }
        else {
            Write-Host "Failed: $collectionName - Status: $statusCode" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host "Verifying collections..."
$listResponse = Invoke-RestMethod -Uri "$baseUrl/collections" -Method Get
$collectionList = $listResponse.result.collections
Write-Host "Found collections: $collectionList" -ForegroundColor Cyan
