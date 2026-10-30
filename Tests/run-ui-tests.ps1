param([switch]$SkipBuild, [switch]$StartupOnly, [switch]$TranscriptOnly, [switch]$ActivityOnly, [switch]$McpPresentationOnly, [switch]$ElicitationOnly, [switch]$SkillsOnly, [switch]$ImagesOnly, [switch]$VideosOnly, [switch]$OverviewTabsOnly, [switch]$AgentsOnly, [switch]$ProvidersOnly, [switch]$LocalToolsOnly, [switch]$ChatSettingsOnly, [switch]$SidebarOnly, [switch]$IconsOnly, [switch]$ExplorerOnly, [switch]$AuthoringToolsOnly, [switch]$ToolOutputOnly, [switch]$ChatFilesOnly, [switch]$ContextSearchOnly, [switch]$SettingsAppearanceOnly, [switch]$UsageOnly, [switch]$ChartsOnly, [switch]$ChatProviderFormsOnly, [switch]$RealtimeOnly,
    [string[]]$Suite, [string]$ReportDirectory, [ValidateRange(10,600)][int]$TimeoutSeconds = 120, [switch]$FishAudioMediaOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RunnerTools.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts\Debug\UiTests'
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $repo ('artifacts\test-runs\ui-' + [guid]::NewGuid().ToString('N')) }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
$failures = [Collections.Generic.List[string]]::new()
$manifest = Get-Content (Join-Path $PSScriptRoot 'ui-suites.json') -Raw | ConvertFrom-Json
$aliases = @{ StartupOnly='startup'; TranscriptOnly='transcript'; ActivityOnly='activity'; McpPresentationOnly='mcp-presentation'; ElicitationOnly='elicitation'; SkillsOnly='skills'; ImagesOnly='images'; VideosOnly='videos'; OverviewTabsOnly='overview-tabs'; AgentsOnly='agents'; ProvidersOnly='providers'; LocalToolsOnly='local-tools'; ChatSettingsOnly='chat-settings'; SidebarOnly='sidebar'; IconsOnly='icons'; ExplorerOnly='explorer'; AuthoringToolsOnly='authoring-tools'; ToolOutputOnly='tool-output'; ChatFilesOnly='chat-files'; ContextSearchOnly='context-search'; SettingsAppearanceOnly='settings-appearance'; UsageOnly='usage'; ChartsOnly='charts'; ChatProviderFormsOnly='chat-provider-forms'; RealtimeOnly='realtime' }
$selected = @()
if ($FishAudioMediaOnly) { $aliases['FishAudioMediaOnly'] = 'fishaudio-media' }
if ($PSBoundParameters.ContainsKey('Suite')) { $selected = @($Suite) }
foreach ($key in $aliases.Keys) { if ($PSBoundParameters.ContainsKey($key) -and $PSBoundParameters[$key]) { $selected += $aliases[$key] } }
if ($selected.Count -eq 0) { $selected = $manifest }
foreach ($name in $selected) { if ($name -notin $manifest -and $name -notin $aliases.Values) { throw "Unknown UI suite: $name" } }
$selected = @($selected | Select-Object -Unique)
foreach ($audit in @('icons','video-resources','agent-resources','usage-resources','overview-resources','chat-provider-resources','realtime-resources')) {
    $code = Invoke-LoggedCommand node @((Join-Path $PSScriptRoot ("check-$audit.cjs"))) (Join-Path $ReportDirectory ("audit-$audit.log"))
    if ($code -ne 0) { $failures.Add("audit-$audit") }
}

# Guard against reintroducing the resource-lookup patterns behind the startup/rendering crashes.
$unsafe = Get-ChildItem (Join-Path $repo 'Core\AIHappey.Desktop.Core') -Filter '*.cs' |
    Select-String -Pattern 'Application\.Current\.Resources\s*\[|\{(?:ThemeResource|StaticResource)\s'
if ($unsafe) { $failures.Add('Unsafe optional resource lookup or detached resource snippet: ' + ($unsafe -join "`n")) }

if ($SkipBuild) { Write-Warning '-SkipBuild is retained for compatibility but no longer bypasses current-source publication.' }
$code = Invoke-LoggedCommand dotnet @('publish', (Join-Path $PSScriptRoot 'AIHappey.Desktop.UiTests\AIHappey.Desktop.UiTests.csproj'),
    '-c','Debug','-r','win-x64','--self-contained','true','-p:Platform=x64','-v:quiet','-clp:ErrorsOnly','-o',$output) (Join-Path $ReportDirectory 'publish.log')
if ($code -ne 0) { throw "UI build failed; all selected suites BLOCKED (not passed): $($selected -join ', '). See $ReportDirectory\publish.log" }
& node (Join-Path $PSScriptRoot 'check-icons.cjs') $output
if ($LASTEXITCODE -ne 0) { throw 'Published Fluent icon assets are missing.' }
foreach ($name in $selected) {
    $report = Join-Path $ReportDirectory ($name + '.txt')
    if (Test-Path $report) { Remove-Item $report -Force } # Never accept a report from an earlier process.
    $process = $null
    try {
        $process = Start-Process (Join-Path $output 'AIHappey.Desktop.UiTests.exe') -ArgumentList @(('"' + $report + '"'), "--$name-only") -PassThru
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill(); $process.WaitForExit()
            throw "UI suite $name timed out after $TimeoutSeconds seconds."
        }
        Assert-UiReport -Report $report -ExitCode $process.ExitCode
        Write-Output "PASS: UI $name"
    } catch {
        $failures.Add($name)
        Write-Output "FAIL: UI $name : $_"
    } finally {
        if (Test-Path $report) { Get-Content $report }
        if ($process) { $process.Dispose() }
    }
}
Write-Output "UI RUN COMPLETE: $($selected.Count) suites attempted; $($failures.Count) failures. Reports: $ReportDirectory"
if ($failures.Count) { throw ('UI run failed: ' + ($failures -join ', ')) }
