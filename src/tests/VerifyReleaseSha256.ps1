param(
    [string]$Version = '1.17',
    [string]$ProjectRoot = '',
    [string]$ReleaseManifestPath = ''
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
$manifestPath = if ([string]::IsNullOrWhiteSpace($ReleaseManifestPath)) {
    Join-Path $ProjectRoot 'RELEASE-SHA256.txt'
}
else {
    [IO.Path]::GetFullPath($ReleaseManifestPath)
}
foreach ($required in @($archivePath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release checksum artifact is missing: $required"
    }
}

$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$expectedText = "$actualHash *$archiveName`r`n"
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$bytes = [IO.File]::ReadAllBytes($manifestPath)
if ($bytes.Length -ge 3 -and
    $bytes[0] -eq 0xEF -and
    $bytes[1] -eq 0xBB -and
    $bytes[2] -eq 0xBF) {
    throw "The release checksum file must be UTF-8 without BOM: $manifestPath"
}
$text = $strictUtf8.GetString($bytes)
if (-not [string]::Equals(
    $text,
    $expectedText,
    [StringComparison]::Ordinal)) {
    throw "The release checksum record is not the exact archive SHA-256 line: $manifestPath"
}

Write-Host "PASS | release SHA-256 | version=$Version | sha256=$actualHash"
