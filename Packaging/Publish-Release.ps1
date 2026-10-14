param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Target, [ValidateSet('Portable','Installer')][string]$Distribution='Portable')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Tools.ps1')
$null = Get-PackageVersion $Version
$extension = if ($Distribution -eq 'Portable') { 'zip' } else { 'msix' }
$files = @(Get-ChildItem $Directory -Filter "*-$Version-win-x64.$extension")
if ($files.Count -eq 0) { throw "No $Distribution artifacts to publish." }
foreach ($file in $files) {
    $expected = (Get-Content ($file.FullName + '.sha256') -Raw).Split(' ')[0]
    if ((Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Artifact checksum mismatch.' }
    if ($Distribution -eq 'Installer') {
        $metadata = Get-Content ($file.FullName + '.json') -Raw | ConvertFrom-Json
        if ($metadata.development -or -not $metadata.signed) { throw 'Cannot publish a development/unsigned MSIX.' }
        Invoke-Checked (Find-WindowsSdkTool 'signtool.exe') @('verify','/pa','/v', $file.FullName)
        $signature = Get-AuthenticodeSignature $file.FullName
        if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -cne $metadata.publisher) { throw 'Installer signature verification failed.' }
    }
}
& gh release view $Version --json targetCommitish,tagName *> $null
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'gh' @('release','create',$Version,'--target',$Target,'--title',"chathappey $Version",'--notes',
        'Self-contained Windows x64 portable ZIP. A signed Windows 11 installer is attached only when installer signing and lifecycle gates pass.')
}
# Existing releases are retryable, but existing assets are immutable: compare rather than overwrite.
$assets = @($files | ForEach-Object {
    $_.FullName; $_.FullName + '.sha256'; if (Test-Path ($_.FullName + '.json')) { $_.FullName + '.json' }
})
foreach ($asset in $assets) {
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
    New-Item $temporary -ItemType Directory | Out-Null
    try {
        & gh release download $Version --pattern ([IO.Path]::GetFileName($asset)) --dir $temporary *> $null
        if ($LASTEXITCODE -eq 0) {
            $existing = Join-Path $temporary ([IO.Path]::GetFileName($asset))
            if ((Get-FileHash $existing).Hash -ne (Get-FileHash $asset).Hash) { throw "Existing release asset differs: $asset" }
        } else { Invoke-Checked 'gh' @('release','upload',$Version,$asset) }
    } finally { Remove-Item $temporary -Recurse -Force }
}
