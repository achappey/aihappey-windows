param([switch]$SkipBuild, [switch]$TranscriptOnly, [switch]$McpPresentationOnly, [switch]$ElicitationOnly, [switch]$SkillsOnly, [switch]$ImagesOnly, [switch]$VideosOnly, [switch]$OverviewTabsOnly, [switch]$AgentsOnly, [switch]$ProvidersOnly, [switch]$LocalToolsOnly, [switch]$ChatSettingsOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts\Debug\UiTests'
& node (Join-Path $PSScriptRoot 'check-video-resources.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Video resource key audit failed.' }
& node (Join-Path $PSScriptRoot 'check-agent-resources.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Agent resource key audit failed.' }

# Guard against reintroducing the resource-lookup patterns behind the startup/rendering crashes.
$unsafe = Get-ChildItem (Join-Path $repo 'Core\AIHappey.Desktop.Core') -Filter '*.cs' |
    Select-String -Pattern 'Application\.Current\.Resources\s*\[|\{(?:ThemeResource|StaticResource)\s'
if ($unsafe) { throw ('Unsafe optional resource lookup or detached resource snippet: ' + ($unsafe -join "`n")) }

if (-not $SkipBuild) {
    & dotnet publish (Join-Path $PSScriptRoot 'AIHappey.Desktop.UiTests\AIHappey.Desktop.UiTests.csproj') `
        -c Debug -r win-x64 --self-contained true -p:Platform=x64 -v:quiet -clp:ErrorsOnly -o $output
    if ($LASTEXITCODE -ne 0) { throw "UI test build failed (exit $LASTEXITCODE)." }
}
$report = Join-Path ([IO.Path]::GetTempPath()) ('AIHappey-ui-tests-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
    $arguments = @('"' + $report + '"')
    if ($TranscriptOnly) { $arguments += '--transcript-only' }
    if ($McpPresentationOnly) { $arguments += '--mcp-presentation-only' }
    if ($ElicitationOnly) { $arguments += '--elicitation-only' }
    if ($SkillsOnly) { $arguments += '--skills-only' }
    if ($ImagesOnly) { $arguments += '--images-only' }
    if ($VideosOnly) { $arguments += '--videos-only' }
    if ($OverviewTabsOnly) { $arguments += '--overview-tabs-only' }
    if ($AgentsOnly) { $arguments += '--agents-only' }
    if ($ProvidersOnly) { $arguments += '--providers-only' }
    if ($LocalToolsOnly) { $arguments += '--local-tools-only' }
    if ($ChatSettingsOnly) { $arguments += '--chat-settings-only' }
    $process = Start-Process (Join-Path $output 'AIHappey.Desktop.UiTests.exe') -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        if (Test-Path $report) { Get-Content $report }
        throw 'Native UI test process timed out after 60 seconds.'
    }
    if (Test-Path $report) { Get-Content $report }
    else { throw "Native UI test process exited without completing its report (exit $($process.ExitCode); possible native startup crash)." }
    if ($process.ExitCode -ne 0) { throw "Native UI checks failed (exit $($process.ExitCode))." }
} finally {
    if (Test-Path $report) { Remove-Item $report -Force }
}
