Set-StrictMode -Version Latest

function Get-PackageVersion([string]$ReleaseVersion) {
    if ($ReleaseVersion -notmatch '^(\d{2})(\d{2})(\d{2})\.(\d{2})(\d{2})$') { throw 'Release version must be yyMMdd.HHmm (UTC).' }
    $date = [datetime]::ParseExact($ReleaseVersion, 'yyMMdd.HHmm', [Globalization.CultureInfo]::InvariantCulture)
    # ParseExact's rolling two-digit year must not change package identities/version ordering in the future.
    $year = 2000 + [int]$ReleaseVersion.Substring(0, 2)
    if ($date.Year -ne $year) { $date = [datetime]::new($year, $date.Month, $date.Day, $date.Hour, $date.Minute, 0) }
    return '{0}.{1}.{2}.0' -f $year, [int]$date.ToString('MMdd'), [int]$date.ToString('HHmm')
}

function Find-WindowsSdkTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin")
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages' }
    $roots += @(Get-ChildItem (Join-Path $nuget 'microsoft.windows.sdk.buildtools') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'bin' })
    foreach ($root in $roots) {
        $tool = Get-ChildItem $root -Filter $Name -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    throw "Missing Windows SDK tool $Name. Restore the desktop project or install the Windows SDK."
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed (exit $LASTEXITCODE)." }
}

function Assert-SigningIdentity([string]$Thumbprint, [string]$Publisher, [switch]$Development) {
    if ([string]::IsNullOrWhiteSpace($Thumbprint) -or [string]::IsNullOrWhiteSpace($Publisher)) {
        throw 'Production MSIX publication requires a signing certificate and exact publisher. Portable ZIPs do not require these inputs.'
    }
    $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne $Publisher -or $certificate.NotAfter -le (Get-Date) -or
        $certificate.NotBefore -gt (Get-Date)) { throw 'Signing certificate is invalid or does not match the package publisher.' }
    if (-not ($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })) { throw 'Certificate lacks code-signing EKU.' }
    if (-not $Development) {
        if ($Publisher -eq 'CN=AppPublisher' -or $Publisher -match 'Development|Test') { throw 'Development publisher cannot sign production MSIX.' }
        $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
        try { if (-not $chain.Build($certificate)) { throw 'Production signing certificate chain is not trusted.' } } finally { $chain.Dispose() }
    }
    return $certificate
}

function Sign-ReleaseFile([string]$File, [string]$Thumbprint, [string]$TimestampUrl, [switch]$Development) {
    $tool = Find-WindowsSdkTool 'signtool.exe'
    $arguments = @('sign', '/sha1', $Thumbprint, '/s', 'My', '/fd', 'SHA256')
    if (-not $Development) {
        if ($TimestampUrl -notmatch '^https://') { throw 'Production signing requires an HTTPS RFC 3161 timestamp endpoint.' }
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $arguments += $File
    Invoke-Checked $tool $arguments
    Invoke-Checked $tool @('verify', '/pa', '/v', $File)
    if (-not $Development) {
        $signature = Get-AuthenticodeSignature $File
        if (-not $signature.TimeStamperCertificate) { throw "Signature has no timestamp: $File" }
    }
}

function Write-Checksum([string]$File) {
    $hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($File + '.sha256', "$hash  $([IO.Path]::GetFileName($File))`n", [Text.UTF8Encoding]::new($false))
}

