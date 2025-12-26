param(
    [string]$BaseUrl = "http://localhost:6333"
)

# Ensures payload indexes exist for albumId/account/tags on the main collections.
$collections = @(
    "faces_arcface_512",
    "clip_512",
    "faces_review_512",
    "album_dominants"
)

$indexes = @(
    @{ field_name = "albumId"; field_schema = "keyword" },
    @{ field_name = "account"; field_schema = "keyword" },
    # Tags are stored as array; keyword index still works without multi flag on current Qdrant versions
    @{ field_name = "tags"; field_schema = "keyword" }
)

foreach ($collection in $collections) {
    foreach ($idx in $indexes) {
        $body = $idx | ConvertTo-Json -Depth 4
        $url = "$BaseUrl/collections/$collection/index"
        Write-Host "[*] Ensuring index $($idx.field_name) on $collection ..."
        try {
            # Qdrant expects PUT for field index creation; POST returns 404
            Invoke-RestMethod -Uri $url -Method Put -ContentType "application/json" -Body $body | Out-Null
            Write-Host "    OK" -ForegroundColor Green
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -eq 409) {
                Write-Host "    Exists" -ForegroundColor Yellow
            }
            else {
                Write-Host "    Failed ($status)" -ForegroundColor Red
                Write-Host "    Body: $body"
            }
        }
    }
}

Write-Host "[DONE] Payload indexes ensured." -ForegroundColor Cyan
