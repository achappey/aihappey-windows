
param(
    [ValidateSet('All', 'HeaderAuth', 'AzureAuth')][string]$HostName = 'All',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$AppName
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$workspace = Split-Path $repo -Parent
$hosts = if ($HostName -eq 'All') { @('HeaderAuth', 'AzureAuth') } else { @($HostName) }
$timestamp = Get-Date -Format 'yyMMdd.HHmm'

# Publishing must never silently fall back to an old snapshot.
& (Join-Path $repo 'Tests/sync-provider-catalog.ps1') -RequireSource

function Invoke-Publish([string]$Project, [string]$Output, [string]$Config, [switch]$Desktop) {
    $arguments = @(
        'publish', $Project,
        '-c', $Config,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-v:quiet',
        '-clp:ErrorsOnly',
        '-o', $Output
    )

    if ($Desktop) {
        $arguments += '-p:Platform=x64'
        if (-not [string]::IsNullOrWhiteSpace($AppName)) {
            $arguments += "-p:DesktopAppName=$AppName"
        }
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $Project (exit $LASTEXITCODE)."
    }
}

function Assert-File([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Incomplete output: $Path"
    }
}

function Get-DesktopAppName([string]$Project) {
    $arguments = @(
        'msbuild', $Project,
        '-getProperty:DesktopAppName',
        "-p:Configuration=$Configuration",
        '-p:Platform=x64',
        '-nologo',
        '-verbosity:quiet'
    )

    if (-not [string]::IsNullOrWhiteSpace($AppName)) {
        $arguments += "-p:DesktopAppName=$AppName"
    }

    $result = @(& dotnet @arguments)

    if ($LASTEXITCODE -ne 0) {
        throw "Cannot resolve DesktopAppName for $Project."
    }

    $displayName = ($result -join "`n").Trim()

    if ([string]::IsNullOrWhiteSpace($displayName)) {
        throw "DesktopAppName is empty for $Project."
    }

    return $displayName
}

# Resolve application names and detect ZIP collisions before building.
$projects = @{}
$zipNames = @{}
$zipOwners = @{}

foreach ($name in $hosts) {
    $sample = Join-Path $repo "Samples\AIHappey.Desktop.$name"
    $project = Join-Path $sample "AIHappey.Desktop.$name.csproj"

    Assert-File $project
    $projects[$name] = $project

    if ($Configuration -eq 'Release') {
        $displayName = Get-DesktopAppName $project

        $slug = $displayName.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
        $slug = $slug.Trim('-')

        if ([string]::IsNullOrWhiteSpace($slug)) {
            throw "Invalid DesktopAppName: $displayName"
        }

        $zipName = "$slug-$timestamp-win-x64.zip"

        if ($zipOwners.ContainsKey($zipName)) {
            throw "ZIP name collision: $zipName ($($zipOwners[$zipName]) and $name). Use distinct DesktopAppName values."
        }

        $zipOwners[$zipName] = $name
        $zipNames[$name] = $zipName
    }
}

# Both desktop hosts produce aihappey.exe.
# Never overwrite or kill an app being debugged.
$running = Get-Process -Name 'aihappey' -ErrorAction SilentlyContinue

if ($running) {
    throw "Close aihappey.exe (and stop debugging) before rebuilding."
}

foreach ($name in $hosts) {
    $output = Join-Path $repo "artifacts\$Configuration\$name"

    if (Test-Path $output) {
        Remove-Item $output -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $output | Out-Null

    Write-Host "Building $name $Configuration (unpackaged Windows x64)..."

    Invoke-Publish $projects[$name] $output $Configuration -Desktop

    if ($name -eq 'HeaderAuth') {
        $ai = Join-Path $output 'runtimes\ai'
        $agents = Join-Path $output 'runtimes\agents'

        # Standalone services run out of process.
        Invoke-Publish (
            Join-Path $workspace 'aihappey-ai\Samples\AIHappey.Windows\AIHappey.Windows.csproj'
        ) $ai 'Release'

        Invoke-Publish (
            Join-Path $workspace 'aihappey-agents\samples\AgentHappey.Windows\AgentHappey.Windows.csproj'
        ) $agents 'Release'

        foreach ($service in @(
            @($ai, 'AIHappey.Windows'),
            @($agents, 'AgentHappey.Windows')
        )) {
            foreach ($extension in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
                Assert-File (Join-Path $service[0] ($service[1] + '.' + $extension))
            }

            Assert-File (Join-Path $service[0] 'appsettings.json')
        }

        Assert-File (Join-Path $agents 'Agents\OpenAIAgent\Agent.json')
    }
    else {
        Assert-File (Join-Path $output 'desktop.json')
    }

    $executable = Join-Path $output 'aihappey.exe'

    Assert-File $executable
    Assert-File (Join-Path $output 'aihappey.pdb')
    Assert-File (Join-Path $output 'Microsoft.UI.Xaml.dll')
    Assert-File (Join-Path $output 'coreclr.dll')
    Assert-File (Join-Path $output 'Assets\AppIcon.ico')

    if ($Configuration -eq 'Release') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem

       $releases = Join-Path $repo 'Releases'
        New-Item -ItemType Directory -Force -Path $releases | Out-Null

        $zip = Join-Path $releases $zipNames[$name]

        if (Test-Path $zip) {
            Remove-Item $zip -Force
        }

        [IO.Compression.ZipFile]::CreateFromDirectory(
            $output,
            $zip,
            [IO.Compression.CompressionLevel]::Optimal,
            $false
        )

        Write-Host "Release ZIP: $zip"
    }

    Write-Host "Complete $name $Configuration output: $output"
    Write-Host ('Launch: & "' + $executable + '"')
}
