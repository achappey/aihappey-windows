$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $repo 'Core/AIHappey.Desktop.Core/Assets/FishAudio'
$manifest = Get-Content (Join-Path $assets 'manifest.json') -Raw | ConvertFrom-Json
function Get-NormalizedHash([string]$Path) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($Path).Replace("`r`n", "`n"))
        return [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    } finally { $hash.Dispose() }
}
if ((Get-NormalizedHash (Join-Path $assets 'runtime.js')) -ne $manifest.bundle) {
    throw 'Fish Audio transport bundle hash mismatch. Rebuild it with the shared chat transport bundle builder.'
}
$source = Join-Path $repo '../aihappey-chat/packages/aihappey-core/src/features/realtime'
foreach ($entry in $manifest.sources.PSObject.Properties) {
    $file = Join-Path $source $entry.Name
    if ((Test-Path $file) -and (Get-NormalizedHash $file) -ne $entry.Value) {
        throw ('Fish Audio shared source changed: ' + $entry.Name + '. Run node scripts/build-fishaudio-desktop.cjs in aihappey-chat before building Windows.')
    }
}
Write-Output ('Verified bundled Fish Audio transport SDK ' + $manifest.sdk)
