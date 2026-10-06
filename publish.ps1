param(
    [ValidateSet('All', 'HeaderAuth', 'AzureAuth')][string]$HostName = 'All',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$AppName
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$workspace = Split-Path $repo -Parent
$hosts = if ($HostName -eq 'All') { @('HeaderAuth', 'AzureAuth') } else { @($HostName) }

function Invoke-Publish([string]$Project, [string]$Output, [string]$Config, [switch]$Desktop) {
    $arguments = @('publish', $Project, '-c', $Config, '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '-v:quiet', '-clp:ErrorsOnly', '-o', $Output)
    if ($Desktop) {
        $arguments += '-p:Platform=x64'
        if (-not [string]::IsNullOrWhiteSpace($AppName)) { $arguments += "-p:DesktopAppName=$AppName" }
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $Project (exit $LASTEXITCODE)." }
}

function Assert-File([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) { throw "Incomplete output: $Path" }
}

foreach ($name in $hosts) {
    $sample = Join-Path $repo "Samples\AIHappey.Desktop.$name"
    $output = Join-Path $repo "artifacts\$Configuration\$name"
    # Never overwrite or kill an app being debugged. Close it before rebuilding.
    $running = Get-Process -Name "AIHappey.Desktop.$name" -ErrorAction SilentlyContinue
    if ($running) { throw "Close AIHappey.Desktop.$name (and stop debugging) before rebuilding." }
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $output | Out-Null

    Write-Host "Building $name $Configuration (unpackaged Windows x64)..."
    Invoke-Publish (Join-Path $sample "AIHappey.Desktop.$name.csproj") $output $Configuration -Desktop
    if ($name -eq 'HeaderAuth') {
        $ai = Join-Path $output 'runtimes\ai'
        $agents = Join-Path $output 'runtimes\agents'
        # Standalone service builds remain incremental; services run out of process in both workflows.
        Invoke-Publish (Join-Path $workspace 'aihappey-ai\Samples\AIHappey.Windows\AIHappey.Windows.csproj') $ai 'Release'
        Invoke-Publish (Join-Path $workspace 'aihappey-agents\samples\AgentHappey.Windows\AgentHappey.Windows.csproj') $agents 'Release'
        foreach ($service in @(@($ai, 'AIHappey.Windows'), @($agents, 'AgentHappey.Windows'))) {
            foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
                Assert-File (Join-Path $service[0] ($service[1] + '.' + $extension))
            }
            Assert-File (Join-Path $service[0] 'appsettings.json')
        }
        Assert-File (Join-Path $agents 'Agents\OpenAIAgent\Agent.json')
    } else {
        Assert-File (Join-Path $output 'desktop.json')
    }
    $executable = Join-Path $output "AIHappey.Desktop.$name.exe"
    Assert-File $executable
    Assert-File (Join-Path $output "AIHappey.Desktop.$name.pdb")
    Assert-File (Join-Path $output 'Microsoft.UI.Xaml.dll')
    Assert-File (Join-Path $output 'coreclr.dll')
    Assert-File (Join-Path $output 'Assets\AppIcon.ico')

    if ($Configuration -eq 'Release') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = Join-Path $repo "artifacts\AIHappey.Desktop.$name-win-x64.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        [IO.Compression.ZipFile]::CreateFromDirectory($output, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Write-Host "Release ZIP: $zip"
    }
    Write-Host "Complete $name $Configuration output: $output"
    Write-Host ('Launch: & "' + $executable + '"')
}
