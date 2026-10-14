$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Packaging\Tools.ps1')
function Assert($Condition, [string]$Name) { if (-not $Condition) { throw "FAILED: $Name" }; Write-Host "PASS: $Name" }
Assert ((Get-PackageVersion '261014.0631') -eq '2026.1014.631.0') 'MSIX version mapping'
Assert ([version](Get-PackageVersion '270101.0000') -gt [version](Get-PackageVersion '261231.2359')) 'UTC year boundary ordering'
foreach ($invalid in @('not-a-version','261332.9999','261014.2400')) {
    $rejected=$false; try { Get-PackageVersion $invalid | Out-Null } catch { $rejected=$true }; Assert $rejected 'Malformed versions rejected'
}
$rejected=$false; try { Assert-SigningIdentity '' '' } catch { $rejected=$true }; Assert $rejected 'Production installer signing fails closed'
$repo = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content (Join-Path $repo 'Packaging\Package.appxmanifest.in') -Raw
$hosts = Get-Content (Join-Path $repo 'Packaging\hosts.json') -Raw | ConvertFrom-Json
Assert ($hosts.HeaderAuth.identity -ne $hosts.AzureAuth.identity -and $hosts.HeaderAuth.clsid -ne $hosts.AzureAuth.clsid) 'Hosts have independent stable identities'
Assert ($manifest.Contains('windows.comServer') -and $manifest.Contains('SurrogateServer') -and $manifest.Contains('windows.fileExplorerContextMenus')) 'Primary menu registration owned by MSIX'
Assert ($manifest.Contains('Type="*"') -and -not $manifest.Contains('Directory')) 'Files only registration'
Assert ($manifest.Contains('10.0.22000.0') -and $manifest.Contains('Windows.FullTrustApplication')) 'Windows 11 full-trust package'
$publish = Get-Content (Join-Path $repo 'publish.ps1') -Raw
Assert ($publish.Contains("`$Distribution = 'Portable'") -and -not $publish.Contains('Assert-SigningIdentity')) 'Default portable path does not require signing'
$extension = Get-Content (Join-Path $repo 'Integration\ExplorerCommand\ExplorerCommand.cpp') -Raw
Assert ($extension.Contains('ActivateApplication') -and -not $extension.Contains('CreateProcess') -and -not $extension.Contains('ShellExecute')) 'Package activation without executable guessing'
$delivery = Get-Content (Join-Path $repo 'Core\AIHappey.Desktop.Core\ChatShell.Explorer.cs') -Raw
Assert (-not ($delivery -match 'SendAsync|StreamAsync|PrepareAsync')) 'Explorer admission cannot call submission path'
foreach ($file in @((Get-ChildItem (Join-Path $repo 'Packaging') -Filter *.ps1)) + @((Get-Item (Join-Path $repo 'publish.ps1')), (Get-Item (Join-Path $repo 'Tests\run-installer-tests.ps1')))) {
    $errors=$null; $tokens=$null; [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    Assert (-not $errors) "PowerShell syntax: $($file.Name)"
}
