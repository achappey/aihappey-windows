param(
    [Parameter(Mandatory)][ValidateSet('HeaderAuth','AzureAuth')][string]$HostName,
    [Parameter(Mandatory)][string]$PublishedOutput,
    [Parameter(Mandatory)][string]$AppName,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$Publisher,
    [string]$PublisherDisplayName = 'AIHappey',
    [string]$CertificateThumbprint,
    [string]$TimestampUrl,
    [switch]$Development,
    [switch]$Unsigned,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Releases')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Tools.ps1')
if ($Unsigned -and -not $Development) { throw 'Unsigned MSIX is allowed only for development validation.' }
if (-not $Unsigned) { $null = Assert-SigningIdentity $CertificateThumbprint $Publisher -Development:$Development }
$repo = Split-Path $PSScriptRoot -Parent
$hostConfig = (Get-Content (Join-Path $PSScriptRoot 'hosts.json') -Raw | ConvertFrom-Json).$HostName
$version = Get-PackageVersion $ReleaseVersion
$identity = $hostConfig.identity + $(if ($Development) { '.Development' } else { '' })
# Development COM identifiers must not collide with a production install.
$clsid = if ($Development) {
    if ($HostName -eq 'HeaderAuth') { '77589891-60F8-43B7-BFC7-039FA33EE780' } else { '717AE571-BAE9-4999-89E4-15CF9C333424' }
} else { $hostConfig.clsid }
if ($AppName -match '["\\\r\n]' -or $AppName.Length -gt 64) { throw 'AppName is invalid for native integration branding.' }
$native = Join-Path $repo "artifacts\native\$identity"
Invoke-Checked 'cmake' @('-S', (Join-Path $repo 'Integration\ExplorerCommand'), '-B', $native, '-A', 'x64',
    "-DCOMMAND_CLSID=$clsid", "-DHOST_NAME=$HostName", "-DAPP_NAME=$AppName")
Invoke-Checked 'cmake' @('--build', $native, '--config', 'Release')
$stage = Join-Path $repo "artifacts\msix\$identity"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item $stage -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PublishedOutput '*') $stage -Recurse
if (-not $Unsigned) {
    # Sign owned binaries only; preserve vendor signatures on Windows/.NET dependencies.
    foreach ($relative in @('aihappey.exe','aihappey.dll','AIHappey.Desktop.Core.dll',
        'runtimes\ai\AIHappey.Windows.exe','runtimes\ai\AIHappey.Windows.dll',
        'runtimes\agents\AgentHappey.Windows.exe','runtimes\agents\AgentHappey.Windows.dll')) {
        $owned = Join-Path $stage $relative
        if (Test-Path $owned) { Sign-ReleaseFile $owned $CertificateThumbprint $TimestampUrl -Development:$Development }
    }
}
foreach ($file in Get-ChildItem $PublishedOutput -File -Recurse) {
    $relative = $file.FullName.Substring((Resolve-Path $PublishedOutput).Path.Length).TrimStart('\','/')
    # Owned PE signatures intentionally differ from the independently publishable unsigned ZIP.
    if ($relative -in @('aihappey.exe','aihappey.dll','AIHappey.Desktop.Core.dll',
        'runtimes\ai\AIHappey.Windows.exe','runtimes\ai\AIHappey.Windows.dll',
        'runtimes\agents\AgentHappey.Windows.exe','runtimes\agents\AgentHappey.Windows.dll')) { continue }
    if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $stage $relative)).Hash) { throw "Shared payload differs: $relative" }
}
Copy-Item (Join-Path $native 'Release\AIHappey.Explorer.dll') $stage
if (-not $Unsigned) { Sign-ReleaseFile (Join-Path $stage 'AIHappey.Explorer.dll') $CertificateThumbprint $TimestampUrl -Development:$Development }
foreach ($file in @('aihappey.exe','Microsoft.UI.Xaml.dll','coreclr.dll','resources.pri','Assets\AppIcon.ico')) {
    if (-not (Test-Path (Join-Path $stage $file))) { throw "Incomplete package payload: $file" }
}
$values = @{ IDENTITY=$identity; PUBLISHER=$Publisher; VERSION=$version; APPNAME=$AppName; PUBLISHERNAME=$PublisherDisplayName; CLSID=$clsid }
$manifest = Get-Content (Join-Path $PSScriptRoot 'Package.appxmanifest.in') -Raw
foreach ($key in $values.Keys) { $manifest = $manifest.Replace("@$key@", [Security.SecurityElement]::Escape($values[$key])) }
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
$slug = ($AppName.ToLowerInvariant() -replace '[^a-z0-9]+','-').Trim('-')
$suffix = if ($Development) { '-development' } else { '' }
$package = Join-Path $OutputDirectory "$slug-$ReleaseVersion-win-x64$suffix.msix"
if (Test-Path $package) { throw "Refusing to overwrite an existing MSIX version: $package" }
Invoke-Checked (Find-WindowsSdkTool 'makeappx.exe') @('pack', '/d', $stage, '/p', $package, '/o')
if (-not $Unsigned) { Sign-ReleaseFile $package $CertificateThumbprint $TimestampUrl -Development:$Development }
Write-Checksum $package
$metadata = @{ host=$HostName; identity=$identity; publisher=$Publisher; version=$version; clsid=$clsid; development=[bool]$Development;
    signed=(-not $Unsigned); nativeProbe=(Join-Path $native 'Release\ExplorerProbe.exe') }
$metadata | ConvertTo-Json | Set-Content ($package + '.json') -Encoding UTF8
Write-Host "Installer: $package"
