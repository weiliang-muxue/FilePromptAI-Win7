param(
    [string]$Version = '1.17',
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

$projectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $MyInvocation.MyCommand.Path))
$manifestPath = Join-Path $projectRoot 'RELEASE-SHA256.txt'
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$archivePath = Join-Path $projectRoot $archiveName
$sidecarPath = "$archivePath.sha256.txt"
$verifyScript = Join-Path $projectRoot 'tests\VerifyReleaseSha256.ps1'
$releaseEvidenceScript = Join-Path $projectRoot 'tests\ReleaseAcceptanceEvidence.ps1'
$receiptRelativePath = "tests/build-artifacts/release/ReleaseCandidate-v$Version.txt"
$receiptPath = Join-Path $projectRoot ($receiptRelativePath.Replace('/', '\'))
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath $releaseEvidenceScript -PathType Leaf)) {
    throw "The release evidence helper is missing: $releaseEvidenceScript"
}
. $releaseEvidenceScript

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

foreach ($required in @($archivePath, $sidecarPath, $verifyScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release artifact is missing: $required"
    }
}

$gitRoot = (& git -C $projectRoot rev-parse --show-toplevel 2>&1 | Out-String).Trim()
$gitExitCode = $LASTEXITCODE
if ($gitExitCode -ne 0 -or
    -not [string]::Equals(
        [IO.Path]::GetFullPath($gitRoot).TrimEnd('\'),
        $projectRoot.TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'seal-release.ps1 must run from the root of its Git worktree.'
}

& git -C $projectRoot check-ignore -q -- $receiptRelativePath
if ($LASTEXITCODE -ne 0) {
    throw 'The local release-candidate receipt must remain ignored by Git.'
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

$headCommit = (& git -C $projectRoot rev-parse --verify HEAD 2>&1 | Out-String).Trim()
$gitExitCode = $LASTEXITCODE
if ($gitExitCode -ne 0) {
    throw 'Unable to resolve the release candidate HEAD commit.'
}
if (-not [string]::Equals(
    $headCommit,
    $receipt.Candidate,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "The tested candidate receipt does not match HEAD: receipt=$($receipt.Candidate); HEAD=$headCommit"
}

# No index entry may differ from HEAD. In particular, an old staged digest
# must never survive while the working copy is replaced with the new digest.
& git -C $projectRoot diff-index --cached --quiet HEAD --
$indexExitCode = $LASTEXITCODE
if ($indexExitCode -eq 1) {
    throw 'Release sealing requires an empty Git index; staged changes are not allowed.'
}
if ($indexExitCode -ne 0) {
    throw 'Unable to inspect the Git index before sealing the release.'
}

$statusLines = @(& git -C $projectRoot status --porcelain=v1 --untracked-files=all --ignore-submodules=none)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git working tree before sealing the release.'
}
$unexpectedChanges = @(
    $statusLines | Where-Object {
        $_ -notin @(
            ' M RELEASE-SHA256.txt',
            ' D RELEASE-SHA256.txt',
            '?? RELEASE-SHA256.txt'
        )
    }
)
if ($unexpectedChanges.Count -ne 0) {
    throw "Release sealing requires a clean source candidate. Only an unstaged RELEASE-SHA256.txt may differ.`n$($unexpectedChanges -join "`n")"
}

$textAttribute = (& git -C $projectRoot check-attr text -- RELEASE-SHA256.txt 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not $textAttribute.EndsWith(': text: unset', [StringComparison]::Ordinal)) {
    throw 'RELEASE-SHA256.txt must be marked -text in .gitattributes before sealing.'
}

$archiveIdentity = Read-FilePromptReleaseArchiveIdentity `
    -ArchivePath $archivePath
$archiveHash = $archiveIdentity.ArchiveSha256
if (-not [string]::Equals(
    $archiveHash,
    $receipt.Hash,
    [StringComparison]::Ordinal)) {
    throw 'The release ZIP no longer matches the successfully tested candidate receipt.'
}
if (-not [string]::Equals(
        $archiveIdentity.ManifestSha256,
        $receipt.ManifestHash,
        [StringComparison]::Ordinal) -or
    $archiveIdentity.ManifestEntryCount -ne $receipt.ManifestEntryCount) {
    throw 'The final release ZIP package manifest identity does not match the successful receipt.'
}
$expectedText = "$archiveHash *$archiveName`r`n"
$sidecarBytes = [IO.File]::ReadAllBytes($sidecarPath)
$sidecarText = $strictUtf8.GetString($sidecarBytes)
if (-not [string]::Equals(
    $sidecarText,
    $expectedText,
    [StringComparison]::Ordinal)) {
    throw 'The generated ZIP sidecar is not the exact SHA-256 record recorded by the successful test run.'
}

$temporaryManifest = Join-Path $projectRoot (
    '.RELEASE-SHA256.' + [Guid]::NewGuid().ToString('N') + '.tmp'
)
try {
    [IO.File]::WriteAllText($temporaryManifest, $expectedText, $utf8NoBom)
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        [IO.File]::Replace($temporaryManifest, $manifestPath, $null)
    }
    else {
        [IO.File]::Move($temporaryManifest, $manifestPath)
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryManifest -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryManifest -Force
    }
}

& powershell.exe `
    -NoLogo `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $verifyScript `
    -Version $Version `
    -ProjectRoot $projectRoot
if ($LASTEXITCODE -ne 0) {
    throw 'The sealed release SHA-256 record failed verification.'
}

Write-Host "SEALED | $manifestPath | candidate=$headCommit | sha256=$archiveHash | manifestSha256=$($receipt.ManifestHash) | acceptanceSha256=$($acceptance.ReportSha256)"
Write-Host 'Commit only RELEASE-SHA256.txt, then create the annotated release tag.'
