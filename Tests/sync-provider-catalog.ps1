param(
    [string]$Source = (Join-Path $PSScriptRoot '../../aihappey-chat/packages/aihappey-core/src/runtime/providers/catalog'),
    [string]$Destination = (Join-Path $PSScriptRoot '../Core/AIHappey.Desktop.Core/ProviderCatalog'),
    [switch]$RequireSource,
    [switch]$Check,
    [string]$BuildStamp
)
$ErrorActionPreference = 'Stop'

function Read-Catalog([string]$Folder) {
    $indexFile = Join-Path $Folder 'index.json'
    if (-not (Test-Path $indexFile -PathType Leaf)) { throw "Missing provider catalog index: $indexFile" }
    $indexText = Get-Content $indexFile -Raw -Encoding UTF8
    if (-not $indexText.TrimStart().StartsWith('[')) { throw "Provider index must be a JSON array: $indexFile" }
    $index = $indexText | ConvertFrom-Json
    if ($index.Count -eq 0) { throw "Empty provider catalog: $indexFile" }
    $ids = @{}; $files = @{}; $result = @{ 'index.json' = [IO.File]::ReadAllBytes($indexFile) }
    foreach ($entry in $index) {
        if ($entry.id -cnotmatch '^[a-z0-9][a-z0-9_-]*$' -or $ids.ContainsKey($entry.id)) { throw "Invalid or duplicate provider ID: $($entry.id)" }
        if ($entry.file -cnotmatch '^[a-z0-9][a-z0-9_-]*\.json$' -or $entry.file -eq 'index.json' -or $files.ContainsKey($entry.file)) { throw "Invalid or duplicate provider filename: $($entry.file)" }
        $ids[$entry.id] = $true; $files[$entry.file] = $true
        $file = Join-Path $Folder $entry.file
        $provider = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $provider -or $provider -is [array] -or $provider.name -isnot [string] -or [string]::IsNullOrWhiteSpace($provider.name)) { throw "Invalid provider metadata: $file" }
        foreach ($property in @('description', 'category', 'providerCountry')) {
            if ($null -ne $provider.$property -and $provider.$property -isnot [string]) { throw "Invalid provider $property in $file" }
        }
        if ($provider.PSObject.Properties['experimental'] -and $provider.experimental -isnot [bool]) { throw "Invalid experimental flag in $file" }
        foreach ($property in @('icons', 'inferenceRegions')) {
            if ($provider.PSObject.Properties[$property] -and $provider.$property -isnot [array]) { throw "Provider $property must be an array in $file" }
        }
        foreach ($region in $provider.inferenceRegions) { if ($region -isnot [string]) { throw "Invalid inference region in $file" } }
        foreach ($icon in $provider.icons) {
            if ($null -eq $icon -or $icon.src -isnot [string] -or ($null -ne $icon.theme -and $icon.theme -isnot [string])) { throw "Invalid provider icon in $file" }
        }
        if ($provider.PSObject.Properties['urls']) {
            if ($null -eq $provider.urls -or $provider.urls -isnot [PSCustomObject]) { throw "Provider urls must be an object in $file" }
            foreach ($property in @('homepage', 'pricing', 'console', 'docs', 'termsOfService', 'privacyPolicy')) {
                if ($null -ne $provider.urls.$property -and $provider.urls.$property -isnot [string]) { throw "Invalid provider URL $property in $file" }
            }
        }
        $result[$entry.file] = [IO.File]::ReadAllBytes($file)
    }
    return $result
}

# A missing checkout is the only reason to allow an offline development snapshot.
# An existing but incomplete/malformed source is always an error.
if (-not (Test-Path $Source -PathType Container)) {
    if ($RequireSource -or $Check) { throw "Provider source catalog is required: $Source. Restore the chat checkout or set DesktopProviderCatalogSource." }
    $null = Read-Catalog $Destination
    Write-Warning "Chat provider checkout is missing; using validated bundled snapshot: $Destination"
    exit 0
}
$catalog = Read-Catalog $Source
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $fingerprint = (($catalog.Keys | Sort-Object -CaseSensitive | ForEach-Object {
        $_ + ':' + [Convert]::ToBase64String($sha.ComputeHash($catalog[$_]))
    }) -join "`n")
    $fingerprint = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprint)))
} finally { $sha.Dispose() }
# Validate everything before touching the snapshot. Preserve all JSON bytes and fields.
$changed = @($catalog.Keys | Where-Object {
    $file = Join-Path $Destination $_
    -not (Test-Path $file -PathType Leaf) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($file)) -cne [Convert]::ToBase64String($catalog[$_])
})
$obsolete = @(if (Test-Path $Destination) { Get-ChildItem $Destination -File -Filter '*.json' | Where-Object { -not $catalog.ContainsKey($_.Name) } })
if ($Check) {
    if ($changed.Count -or $obsolete.Count) { throw 'Bundled provider catalog differs from chat. Build the desktop app before publishing (publishing without building cannot refresh embedded resources).' }
    if ($BuildStamp -and (-not (Test-Path $BuildStamp -PathType Leaf) -or [IO.File]::ReadAllText($BuildStamp).Trim() -cne $fingerprint)) {
        throw 'The compiled desktop output has a different provider catalog fingerprint. Rebuild before publishing.'
    }
    Write-Output "Provider catalog verified ($($catalog.Count - 1) providers)."
    exit 0
}
$null = New-Item -ItemType Directory -Force -Path $Destination
foreach ($name in $changed) {
    $file = Join-Path $Destination $name
    $temporary = $file + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try { [IO.File]::WriteAllBytes($temporary, $catalog[$name]); Move-Item $temporary $file -Force }
    finally { if (Test-Path $temporary) { Remove-Item $temporary -Force } }
}
foreach ($file in $obsolete) { Remove-Item $file.FullName -Force }
$stamp = Join-Path $Destination 'ProviderCatalog.sha256'
if (-not (Test-Path $stamp) -or [IO.File]::ReadAllText($stamp).Trim() -cne $fingerprint) { [IO.File]::WriteAllText($stamp, $fingerprint) }
Write-Output "Provider catalog synchronized ($($catalog.Count - 1) providers; $($changed.Count) updated, $($obsolete.Count) removed)."
