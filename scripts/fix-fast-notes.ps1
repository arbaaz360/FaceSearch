param(
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$Collection = "faces_fast_512",
    [string]$RootPrefix = "X:\Immich\uploads\library\96e7f049-ce60-47a5-9548-a6ebefd14d85",
    [int]$PageSize = 500,
    [switch]$Apply,
    [int]$MaxPoints = 0
)

$ErrorActionPreference = "Stop"

function Get-NoteFromPath {
    param([string]$Path, [string]$Root)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $norm = $Path -replace '/', '\'
    $rel = $norm
    if (-not [string]::IsNullOrWhiteSpace($Root)) {
        $rootNorm = $Root -replace '/', '\'
        if ($norm.ToLower().StartsWith($rootNorm.ToLower())) {
            $rel = $norm.Substring($rootNorm.Length).TrimStart('\')
        }
    }
    $segments = $rel -split '[\\/]+'
    foreach ($s in $segments) {
        if (-not [string]::IsNullOrWhiteSpace($s)) { return $s.Trim() }
    }
    return $null
}

Write-Host "Qdrant: $QdrantUrl" -ForegroundColor Cyan
Write-Host "Collection: $Collection" -ForegroundColor Cyan
Write-Host "Root prefix for note derivation: $RootPrefix" -ForegroundColor Cyan
Write-Host "Page size: $PageSize" -ForegroundColor Cyan
Write-Host "Apply: $Apply" -ForegroundColor Cyan
Write-Host ""

$offset = $null
$checked = 0
$updated = 0
$page = 0

function Scroll-Qdrant {
    param($OffsetToken)
    $body = @{
        limit        = $PageSize
        with_payload = $true
        with_vectors = $false
    }
    if ($OffsetToken) { $body.offset = $OffsetToken }
    $json = $body | ConvertTo-Json -Depth 4
    return Invoke-RestMethod -Method Post -Uri "$QdrantUrl/collections/$Collection/points/scroll" -ContentType "application/json" -Body $json
}

function Set-PayloadBatch {
    param($Items)
    # Qdrant batch update expects operations like { "set_payload": { "points": [...], "payload": {...} } }
    # We need per-point note, so one operation per point.
    $ops = $Items | ForEach-Object {
        @{
            set_payload = @{
                points  = @($_.id)
                payload = @{ note = $_.note }
            }
        }
    }
    $body = @{ operations = $ops }
    $json = $body | ConvertTo-Json -Depth 6
    Invoke-RestMethod -Method Post -Uri "$QdrantUrl/collections/$Collection/points/batch" -ContentType "application/json" -Body $json | Out-Null
}

do {
    $resp = Scroll-Qdrant -OffsetToken $offset
    $points = $resp.result?.points
    $offset = $resp.result?.next_page_offset
    $page++
    if (-not $points) { break }

    $updates = New-Object System.Collections.Generic.List[object]
    foreach ($p in $points) {
        $checked++
        if ($MaxPoints -gt 0 -and $checked -gt $MaxPoints) { break }
        $path = $p.payload.path
        $curNote = $p.payload.note
        $newNote = Get-NoteFromPath -Path $path -Root $RootPrefix
        if (-not [string]::IsNullOrWhiteSpace($newNote) -and $newNote -ne $curNote) {
            $updates.Add([pscustomobject]@{ id = $p.id; note = $newNote })
        }
    }

    if ($updates.Count -gt 0) {
        Write-Host ("Page {0}: {1}/{2} need note update" -f $page, $updates.Count, $points.Count)
        if ($Apply) {
            Set-PayloadBatch -Items $updates
            $updated += $updates.Count
        }
    } else {
        Write-Host ("Page {0}: no changes" -f $page)
    }

    if ($MaxPoints -gt 0 -and $checked -ge $MaxPoints) { break }
}
while ($offset)

Write-Host ""
Write-Host ("Checked: {0}" -f $checked) -ForegroundColor Green
Write-Host ("Updated: {0}" -f $updated) -ForegroundColor Green
if (-not $Apply) {
    Write-Host "No writes performed (run with -Apply to write changes)" -ForegroundColor Yellow
}
