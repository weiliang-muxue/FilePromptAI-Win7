param(
    [string]$Version = '1.17',
    [string]$ProjectRoot = '',
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AcceptanceReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $testRoot
}
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$tagName = "v$Version"
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$archivePath = Join-Path $ProjectRoot $archiveName
$sidecarPath = "$archivePath.sha256.txt"
$receiptPath = Join-Path $ProjectRoot (
    "tests\build-artifacts\release\ReleaseCandidate-v$Version.txt")
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$releaseEvidenceScript = Join-Path $testRoot 'ReleaseAcceptanceEvidence.ps1'
if (-not (Test-Path -LiteralPath $releaseEvidenceScript -PathType Leaf)) {
    throw "The release evidence helper is missing: $releaseEvidenceScript"
}
. $releaseEvidenceScript

function Get-GitRelativeProjectPath {
    param([string]$GitRoot)

    $root = [IO.Path]::GetFullPath($GitRoot).TrimEnd('\')
    $project = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    if ([string]::Equals(
            $root,
            $project,
            [StringComparison]::OrdinalIgnoreCase)) {
        return ''
    }
    $prefix = $root + '\'
    if (-not $project.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The source project must be inside its Git worktree.'
    }
    return $project.Substring($prefix.Length).Replace('\', '/')
}

function Read-ReleaseReceipt {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The successful release-candidate receipt is missing: $Path"
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF) {
        throw 'The release-candidate receipt must be UTF-8 without BOM.'
    }
    $text = $strictUtf8.GetString($bytes)
    $pattern = '\A' +
        'FilePromptAI-Release-Receipt: 2\r\n' +
        'Suite: tests/RunAllSmokeTests\.ps1\r\n' +
        'Result: PASS\r\n' +
        'Version: (?<Version>[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?)\r\n' +
        'Candidate-Commit: (?<Candidate>[0-9a-f]{40}(?:[0-9a-f]{24})?)\r\n' +
        'Archive-Name: (?<Archive>[0-9A-Za-z._-]+)\r\n' +
        'Archive-SHA256: (?<Hash>[0-9A-F]{64})\r\n' +
        'Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256\.txt\r\n' +
        'Package-Manifest-SHA256: (?<ManifestHash>[0-9A-F]{64})\r\n' +
        'Package-Manifest-Entry-Count: (?<ManifestEntryCount>[1-9][0-9]{0,8})\r\n\z'
    $match = [Text.RegularExpressions.Regex]::Match(
        $text,
        $pattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw 'The release-candidate receipt has an invalid or non-canonical format.'
    }
    $manifestEntryCount = 0
    if (-not [int]::TryParse(
        $match.Groups['ManifestEntryCount'].Value,
        [Globalization.NumberStyles]::None,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$manifestEntryCount)) {
        throw 'The release-candidate receipt has an invalid package manifest entry count.'
    }
    return [pscustomobject]@{
        Version = $match.Groups['Version'].Value
        Candidate = $match.Groups['Candidate'].Value
        Archive = $match.Groups['Archive'].Value
        Hash = $match.Groups['Hash'].Value
        ManifestHash = $match.Groups['ManifestHash'].Value
        ManifestEntryCount = $manifestEntryCount
    }
}

$gitRoot = (& git -C $ProjectRoot rev-parse --show-toplevel 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'The tagged release verifier requires a Git worktree.'
}
$gitProjectPath = Get-GitRelativeProjectPath -GitRoot $gitRoot
$gitManifestPath = if ([string]::IsNullOrEmpty($gitProjectPath)) {
    'RELEASE-SHA256.txt'
}
else {
    "$gitProjectPath/RELEASE-SHA256.txt"
}

$receipt = Read-ReleaseReceipt -Path $receiptPath
if (-not [string]::Equals($receipt.Version, $Version, [StringComparison]::Ordinal) -or
    -not [string]::Equals($receipt.Archive, $archiveName, [StringComparison]::Ordinal)) {
    throw 'The release-candidate receipt is for a different release version or archive.'
}
$acceptance = Read-FilePromptAcceptanceEvidence `
    -Path $AcceptanceReportPath `
    -Version $Version
if (-not [string]::Equals(
        $acceptance.ManifestSha256,
        $receipt.ManifestHash,
        [StringComparison]::Ordinal) -or
    $acceptance.ManifestEntryCount -ne $receipt.ManifestEntryCount) {
    throw 'The Windows 7 acceptance report package identity does not match the successfully tested release receipt.'
}

$tagType = (& git -C $ProjectRoot cat-file -t "refs/tags/$tagName" 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $tagType -ne 'tag') {
    throw "The release tag must exist as an annotated tag: $tagName"
}
$tagCommit = (& git -C $ProjectRoot rev-parse "$tagName^{commit}" 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Unable to resolve the release tag commit: $tagName"
}
$headCommit = (& git -C $ProjectRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $headCommit) {
    throw "The release tag does not point to HEAD: tag=$tagCommit; HEAD=$headCommit"
}

$parentLine = (& git -C $ProjectRoot rev-list --parents -n 1 $tagCommit 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the release seal commit parents.'
}
$parentFields = @($parentLine -split '\s+')
if ($parentFields.Count -ne 2) {
    throw 'The release seal commit must have exactly one parent.'
}
$candidateCommit = $parentFields[1]
if (-not [string]::Equals(
    $candidateCommit,
    $receipt.Candidate,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "The seal commit parent is not the tested candidate: parent=$candidateCommit; receipt=$($receipt.Candidate)"
}

$sealedPaths = @(& git -C $gitRoot diff --name-only --no-renames $candidateCommit $tagCommit --)
if ($LASTEXITCODE -ne 0 -or
    $sealedPaths.Count -ne 1 -or
    -not [string]::Equals(
        $sealedPaths[0],
        $gitManifestPath,
        [StringComparison]::Ordinal)) {
    throw 'The release seal commit must change exactly RELEASE-SHA256.txt relative to the tested candidate.'
}

$trackedPath = (& git -C $gitRoot ls-tree --name-only $tagCommit -- $gitManifestPath 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $trackedPath -ne $gitManifestPath) {
    throw 'RELEASE-SHA256.txt is missing from the release tag.'
}
$tagBlob = (& git -C $gitRoot rev-parse "${tagName}:$gitManifestPath" 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the tagged release digest blob.'
}
$workingBlob = (& git -C $gitRoot hash-object --no-filters -- $gitManifestPath 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $tagBlob -ne $workingBlob) {
    throw 'The tagged release digest differs byte-for-byte from the working copy.'
}

foreach ($required in @($archivePath, $sidecarPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release artifact is missing: $required"
    }
}
$archiveIdentity = Read-FilePromptReleaseArchiveIdentity `
    -ArchivePath $archivePath
$archiveHash = $archiveIdentity.ArchiveSha256
if (-not [string]::Equals(
    $archiveHash,
    $receipt.Hash,
    [StringComparison]::Ordinal)) {
    throw 'The local release ZIP no longer matches the successfully tested candidate receipt.'
}
if (-not [string]::Equals(
        $archiveIdentity.ManifestSha256,
        $receipt.ManifestHash,
        [StringComparison]::Ordinal) -or
    $archiveIdentity.ManifestEntryCount -ne $receipt.ManifestEntryCount) {
    throw 'The final release ZIP package manifest identity does not match the successful receipt.'
}

& powershell.exe `
    -NoLogo `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File (Join-Path $testRoot 'VerifyReleaseSha256.ps1') `
    -Version $Version `
    -ProjectRoot $ProjectRoot
if ($LASTEXITCODE -ne 0) {
    throw 'The tagged release digest does not verify the local release ZIP.'
}

Write-Host "PASS | annotated release tag | tag=$tagName | commit=$tagCommit | candidate=$candidateCommit | sha256=$archiveHash | manifestSha256=$($receipt.ManifestHash) | acceptanceSha256=$($acceptance.ReportSha256)"
