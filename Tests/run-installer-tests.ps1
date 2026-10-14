param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Probe,
    [Parameter(Mandatory)][string]$UpgradePackage,
    [switch]$DisposableEnvironment
)
$ErrorActionPreference='Stop'
if (-not $DisposableEnvironment -or $env:AIHAPPEY_DISPOSABLE_WINDOWS11 -ne '1' -or [Environment]::OSVersion.Version.Build -lt 22000 -or
    -not [Environment]::UserInteractive) { throw 'Installer tests require an explicitly marked disposable, interactive Windows 11 VM.' }
. (Join-Path $PSScriptRoot '..\Packaging\Tools.ps1')
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShellTestInput {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
}
'@
function Assert($Condition,[string]$Name) { if(-not $Condition){throw "FAILED: $Name"}; Write-Host "PASS: $Name" }
$metadata=Get-Content ($Package+'.json') -Raw | ConvertFrom-Json
Assert (-not (Get-AppxPackage -Name $metadata.identity)) 'Refuse to replace an existing installation'
$root=Join-Path ([IO.Path]::GetTempPath()) ('AIHappey-installer-'+[guid]::NewGuid().ToString('N'))
New-Item $root -ItemType Directory | Out-Null
$first=Join-Path $root 'first file.txt'; $second=Join-Path $root 'second-file.txt'
[IO.File]::WriteAllText($first,'first'); [IO.File]::WriteAllText($second,'second')
$installed=$null; $processes=@()
function AppProcesses { @(Get-Process aihappey -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$($installed.InstallLocation)\*" }) }
function CloseApp {
    foreach($process in (AppProcesses)) { $null=$process.CloseMainWindow(); if(-not $process.WaitForExit(30000)){throw 'Application did not shut down gracefully'} }
}
function InvokeSelection {
    $before=@(Get-ChildItem $inbox -Filter *.status -ErrorAction SilentlyContinue | ForEach-Object Name)
    Invoke-Checked $Probe @("{$($metadata.clsid)}",$first,$second)
    for($attempt=0;$attempt -lt 150;$attempt++) {
        $status=Get-ChildItem $inbox -Filter *.status -ErrorAction SilentlyContinue | Where-Object {$_.Name -notin $before} | Select-Object -First 1
        if($status){ Assert ((Get-Content $status.FullName -Raw | ConvertFrom-Json).outcome -eq 'attached') 'Registered native COM selection reached draft'; return }
        Start-Sleep -Seconds 1
    }
    throw 'Installed activation failed to acknowledge file delivery'
}
function CheckPrimaryMenu {
    Start-Process explorer.exe -ArgumentList ('/select,"'+$first+'"')
    $element=$null
    for($attempt=0;$attempt -lt 30;$attempt++) {
        $items=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
        $element=$items | Where-Object { $_.Current.Name -in @('first file.txt','first file') } | Select-Object -First 1
        if($element){break}; Start-Sleep -Milliseconds 500
    }
    Assert ($null -ne $element) 'Explorer test file visible'
    $element.SetFocus(); $point=$element.GetClickablePoint()
    $null=[ShellTestInput]::SetCursorPos([int]$point.X,[int]$point.Y)
    [ShellTestInput]::mouse_event(0x8,0,0,0,[UIntPtr]::Zero); [ShellTestInput]::mouse_event(0x10,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
    $items=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem))
    $entry=$items | Where-Object { $_.Current.Name -match '^Attach to |^Toevoegen aan ' } | Select-Object -First 1
    Assert ($null -ne $entry) 'Command visible in primary menu without opening Show more options'
    ([Windows.Automation.InvokePattern]$entry.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
try {
    Add-AppxPackage -Path $Package
    $installed=Get-AppxPackage -Name $metadata.identity
    Assert ($installed.Version -eq $metadata.version) 'Fresh installation registered expected version'
    $state=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages\$($installed.PackageFamilyName)\LocalState"
    $inbox=Join-Path $state 'ShellRequests'
    $manifest=Get-AppxPackageManifest $installed
    Assert ($manifest.OuterXml.Contains('windows.fileExplorerContextMenus') -and $manifest.OuterXml.Contains($metadata.clsid)) 'Package owns menu and COM registration'
    InvokeSelection; $processes=AppProcesses
    Assert ($processes.Count -eq 1) 'Cold activation starts one installed instance'
    InvokeSelection
    Assert ((AppProcesses).Count -eq 1 -and (AppProcesses)[0].Id -eq $processes[0].Id) 'Warm activation reuses instance'
    CheckPrimaryMenu
    $sentinel=Join-Path $state 'upgrade-preserved.txt'; [IO.File]::WriteAllText($sentinel,'preserve')
    $active=(AppProcesses)[0]
    Add-AppxPackage -Path $UpgradePackage -DeferRegistrationWhenPackagesAreInUse
    Assert (-not $active.HasExited) 'Running application upgrade is deferred without force-closing a chat'
    CloseApp
    Add-AppxPackage -Path $UpgradePackage
    Assert (Test-Path $sentinel) 'Repeated install and deferred upgrade preserve state'
    $installed=Get-AppxPackage -Name $metadata.identity
    Assert ([version]$installed.Version -gt [version]$metadata.version -and (Test-Path $sentinel)) 'Upgrade preserves package-local state'
    $rejected=$false; try { Add-AppxPackage -Path $Package } catch { $rejected=$true }
    Assert $rejected 'Downgrade rejected'
    $corrupt=Join-Path $root 'corrupt.msix'; [IO.File]::WriteAllText($corrupt,'not a package')
    $version=$installed.Version; $rejected=$false; try { Add-AppxPackage -Path $corrupt } catch { $rejected=$true }
    Assert ($rejected -and (Get-AppxPackage -Name $metadata.identity).Version -eq $version -and (Test-Path $sentinel)) 'Failed deployment leaves previous version and data usable'
    InvokeSelection
    Remove-AppxPackage -Package $installed.PackageFullName
    Assert ((AppProcesses).Count -eq 0) 'Uninstall closes the installed application'
    Assert (-not (Get-AppxPackage -Name $metadata.identity) -and (Test-Path $first) -and (Test-Path $second)) 'Uninstall unregisters package and preserves selected source files'
    # A COM activation check must fail after package registration removal.
    & $Probe "{$($metadata.clsid)}" $first *> $null
    Assert ($LASTEXITCODE -ne 0) 'No stale COM registration after uninstall'
    Add-AppxPackage -Path $Package
    $installed=Get-AppxPackage -Name $metadata.identity
    Assert (-not (Test-Path $sentinel)) 'Reinstall starts with clean package-local state'
    Write-Host 'Installer lifecycle checks passed.'
} finally {
    if($installed) { CloseApp; Get-AppxPackage -Name $metadata.identity | Remove-AppxPackage }
    Remove-Item $root -Recurse -Force
}
