param([string]$ReportDirectory, [switch]$NonVisualOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RunnerTools.ps1')
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $repo ('artifacts\test-runs\all-' + [guid]::NewGuid().ToString('N')) }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Run([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$Timeout = 600) {
    $log = Join-Path $ReportDirectory ($Name + '.log')
    try { $code = Invoke-LoggedCommand $Executable $Arguments $log $Timeout }
    catch { [IO.File]::WriteAllText($log, $_.ToString()); $code = 1 }
    $status = if ($code -eq 0) { 'PASS' } else { 'FAIL' }
    $results.Add([pscustomobject]@{ Name=$Name; Status=$status; ExitCode=$code; Log=$log })
    Write-Output "$status : $Name (exit $code)"
    return $code
}
$project = Join-Path $PSScriptRoot 'AIHappey.Desktop.Realtime.Tests\AIHappey.Desktop.Realtime.Tests.csproj'
$build = Run 'build-nonvisual' dotnet @('build',$project,'-c','Debug','-v:quiet','-clp:ErrorsOnly')
# Run emits progress as well as its exit code; use the recorded result, not mixed PowerShell output.
if ($results[$results.Count-1].ExitCode -eq 0) {
    foreach ($name in @('AIHappey.Desktop.Tests','AIHappey.Desktop.Realtime.Tests')) {
        $binary = Join-Path $PSScriptRoot ("$name\bin\Debug\net11.0-windows10.0.26100.0\$name.dll")
        Run $name dotnet @($binary) | Out-Host
    }
} else {
    foreach ($name in @('AIHappey.Desktop.Tests','AIHappey.Desktop.Realtime.Tests')) {
        $results.Add([pscustomobject]@{ Name=$name; Status='BLOCKED'; ExitCode=1; Log='Current-source build failed; stale execution refused.' })
    }
}
foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'check-*.cjs') | Sort-Object Name) { Run $file.BaseName node @($file.FullName) | Out-Host }
foreach ($name in @('sync-local-tool-contracts.cjs','sync-model-providers.cjs','sync-provider-resources.cjs')) {
    Run $name node @((Join-Path $PSScriptRoot $name),'--check') | Out-Host
}
Run 'packaging' powershell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'check-packaging.ps1')) | Out-Host
if ($NonVisualOnly) {
    $results.Add([pscustomobject]@{ Name='native-ui'; Status='NOT RUN'; ExitCode=1; Log='Explicit -NonVisualOnly selection; not a complete all-suite run.' })
} else {
    Run 'native-ui' powershell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'run-ui-tests.ps1'),
        '-ReportDirectory',(Join-Path $ReportDirectory 'ui')) 3600 | Out-Host
}
$results.Add([pscustomobject]@{ Name='signed-installer-lifecycle'; Status='BLOCKED'; ExitCode=1;
    Log='Requires signed release and upgrade packages, native Explorer probe, and an explicitly disposable interactive Windows 11 environment. No installation attempted.' })
$results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ReportDirectory 'summary.json') -Encoding UTF8
$results | Format-Table -AutoSize | Out-String | Set-Content (Join-Path $ReportDirectory 'summary.txt') -Encoding UTF8
$results | Format-Table -AutoSize | Out-Host
Write-Output "Reports retained at $ReportDirectory"
if (@($results | Where-Object Status -ne 'PASS').Count) { exit 1 }
