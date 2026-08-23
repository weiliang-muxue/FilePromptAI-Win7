param(
    [string]$Version = '1.17',
    [string]$ProjectRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ProjectRoot = Split-Path -Parent $testRoot
}
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)

$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$archivePath = Join-Path $ProjectRoot $archiveName
$sidecarPath = "$archivePath.sha256.txt"
$manifestPath = Join-Path $ProjectRoot 'RELEASE-SHA256.txt'
foreach ($required in @($archivePath, $sidecarPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release checksum artifact is missing: $required"
    }
}

$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$expectedText = "$actualHash *$archiveName`r`n"
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
foreach ($checksumPath in @($sidecarPath, $manifestPath)) {
    $bytes = [IO.File]::ReadAllBytes($checksumPath)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw "Release checksum files must be UTF-8 without BOM: $checksumPath"
    }
    $text = $strictUtf8.GetString($bytes)
    if (-not [string]::Equals(
        $text,
        $expectedText,
        [StringComparison]::Ordinal)) {
        throw "Release checksum record is not the exact archive SHA-256 line: $checksumPath"
    }
}

Write-Host "PASS | release SHA-256 | version=$Version | sha256=$actualHash"
