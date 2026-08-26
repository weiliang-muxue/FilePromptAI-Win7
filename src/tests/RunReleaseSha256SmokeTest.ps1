param(
    [string]$Version = '1.18'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$verifyScript = Join-Path $testRoot 'VerifyReleaseSha256.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-ReleaseHash-' + [Guid]::NewGuid().ToString('N')
)
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$archivePath = Join-Path $temporaryRoot $archiveName
$manifestPath = Join-Path $temporaryRoot 'RELEASE-SHA256.txt'
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Set-ValidFixture {
    [IO.File]::WriteAllBytes(
        $archivePath,
        [Text.Encoding]::ASCII.GetBytes('deterministic release fixture'))
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    $line = "$hash *$archiveName`r`n"
    [IO.File]::WriteAllText($manifestPath, $line, $utf8NoBom)
    return $line
}

function Invoke-Verification {
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & powershell.exe `
            -NoLogo `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $verifyScript `
            -Version $Version `
            -ProjectRoot $temporaryRoot 2>&1 | Out-String
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-Rejected {
    param([string]$Description)

    $result = Invoke-Verification
    if ($result.ExitCode -eq 0) {
        throw "$Description was accepted unexpectedly.`n$($result.Output)"
    }
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $validLine = Set-ValidFixture
    $validResult = Invoke-Verification
    if ($validResult.ExitCode -ne 0 -or
        $validResult.Output -notmatch '(?m)^PASS \| release SHA-256 \|') {
        throw "The valid release checksum fixture failed.`n$($validResult.Output)"
    }

    [IO.File]::AppendAllText($archivePath, 'x', $utf8NoBom)
    Assert-Rejected 'A one-byte ZIP mutation'

    $validLine = Set-ValidFixture
    $wrongHash = ('0' * 64) + $validLine.Substring(64)
    [IO.File]::WriteAllText($manifestPath, $wrongHash, $utf8NoBom)
    Assert-Rejected 'An incorrect tracked digest'

    $validLine = Set-ValidFixture
    $wrongName = $validLine.Replace($archiveName, 'wrong.zip')
    [IO.File]::WriteAllText($manifestPath, $wrongName, $utf8NoBom)
    Assert-Rejected 'An incorrect archive name'

    $validLine = Set-ValidFixture
    [IO.File]::WriteAllText(
        $manifestPath,
        $validLine + $validLine,
        $utf8NoBom)
    Assert-Rejected 'A duplicate checksum line'

    $validLine = Set-ValidFixture
    $bomBytes = New-Object byte[] (3 + [Text.Encoding]::UTF8.GetByteCount($validLine))
    $bomBytes[0] = 0xEF
    $bomBytes[1] = 0xBB
    $bomBytes[2] = 0xBF
    [Text.Encoding]::UTF8.GetBytes(
        $validLine,
        0,
        $validLine.Length,
        $bomBytes,
        3) | Out-Null
    [IO.File]::WriteAllBytes($manifestPath, $bomBytes)
    Assert-Rejected 'A BOM-prefixed checksum file'
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporary.StartsWith(
        $resolvedSystemTemp,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary).StartsWith(
            'FilePromptAI-ReleaseHash-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

Write-Host 'PASS | release SHA-256 strict-format and tamper tests'
