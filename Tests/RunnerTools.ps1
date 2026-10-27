function Assert-UiReport {
    param([string]$Report, [int]$ExitCode)
    if (-not (Test-Path -LiteralPath $Report)) { throw "UI process exited without a report (exit $ExitCode)." }
    $text = [IO.File]::ReadAllText($Report)
    if ($ExitCode -ne 0) { throw "UI process failed (exit $ExitCode); report: $Report" }
    if ($text -match '(?m)^(?:UNHANDLED:|FAILED:|SUITE FAIL:)' -or $text -notmatch '(?m)^UI COMPLETE: passed\r?$') {
        throw "UI report contains failures or lacks a successful completion marker: $Report"
    }
}

function Invoke-LoggedCommand {
    param([string]$FilePath, [string[]]$Arguments, [string]$Log, [int]$TimeoutSeconds = 600)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FilePath
    $info.Arguments = ($Arguments | ForEach-Object {
        if ($_ -notmatch '[\s"]' -and $_ -ne '') { $_ }
        else { '"' + ([regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"' }
    }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start $FilePath" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill(); $process.WaitForExit() }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($timedOut) { $text += "`nTIMEOUT: $TimeoutSeconds seconds.`n" }
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($Log), $text, [Text.UTF8Encoding]::new($false))
        if ($timedOut) { return 124 }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
