param(
    [string]$Version = '1.19',
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
$formalEvidencePath = Join-Path $projectRoot 'RELEASE-EVIDENCE.txt'
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$verifyScript = Join-Path $projectRoot 'tests\VerifyReleaseSha256.ps1'
$releaseEvidenceScript = Join-Path $projectRoot 'tests\ReleaseAcceptanceEvidence.ps1'
$receiptRelativePath = "tests/build-artifacts/release/ReleaseCandidate-v$Version.txt"
$receiptPath = Join-Path $projectRoot ($receiptRelativePath.Replace('/', '\'))
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Get-GitRelativeProjectPath {
    param([string]$GitRoot)

    $root = [IO.Path]::GetFullPath($GitRoot).TrimEnd('\')
    $project = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
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

$gitRoot = (& git -C $projectRoot rev-parse --show-toplevel 2>&1 | Out-String).Trim()
$gitExitCode = $LASTEXITCODE
if ($gitExitCode -ne 0) {
    throw 'seal-release.ps1 requires a Git worktree.'
}
$gitProjectPath = Get-GitRelativeProjectPath -GitRoot $gitRoot
$distributionRoot = Join-Path $gitRoot 'exe'
$archivePath = Join-Path $distributionRoot $archiveName
foreach ($required in @(
    $archivePath,
    $verifyScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release artifact is missing: $required"
    }
}
$gitReceiptPath = if ([string]::IsNullOrEmpty($gitProjectPath)) {
    $receiptRelativePath
}
else {
    "$gitProjectPath/$receiptRelativePath"
}
$gitManifestPath = if ([string]::IsNullOrEmpty($gitProjectPath)) {
    'RELEASE-SHA256.txt'
}
else {
    "$gitProjectPath/RELEASE-SHA256.txt"
}
$gitFormalEvidencePath = if ([string]::IsNullOrEmpty($gitProjectPath)) {
    'RELEASE-EVIDENCE.txt'
}
else {
    "$gitProjectPath/RELEASE-EVIDENCE.txt"
}
$gitArchivePath = "exe/$archiveName"

& git -C $gitRoot check-ignore -q -- $gitReceiptPath
if ($LASTEXITCODE -ne 0) {
    throw 'The local release-candidate receipt must remain ignored by Git.'
}

$receipt = Read-FilePromptReleaseReceipt -Path $receiptPath
if (-not [string]::Equals($receipt.Version, $Version, [StringComparison]::Ordinal) -or
    -not [string]::Equals($receipt.Archive, $archiveName, [StringComparison]::Ordinal)) {
    throw 'The release-candidate receipt is for a different release version or archive.'
}
$acceptance = Read-FilePromptAcceptanceEvidence `
    -Path $AcceptanceReportPath `
    -Version $Version
if (-not [string]::Equals(
        $acceptance.ArchiveSha256,
        $receipt.Hash,
        [StringComparison]::Ordinal) -or
    -not [string]::Equals(
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
$parentLine = (& git -C $gitRoot rev-list --parents -n 1 $headCommit 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the promotion commit parent.'
}
$parentFields = @($parentLine -split '\s+')
if ($parentFields.Count -ne 2 -or
    -not [string]::Equals(
        $parentFields[1],
        $receipt.Candidate,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The promotion commit parent is not the tested source candidate: receipt=$($receipt.Candidate); HEAD=$headCommit"
}
$promotionPaths = @(& git -C $gitRoot diff --name-only --no-renames $receipt.Candidate $headCommit --)
$expectedPromotionPaths = @(
    $gitArchivePath
)
if ($LASTEXITCODE -ne 0 -or
    @(Compare-Object `
        -ReferenceObject @($expectedPromotionPaths | Sort-Object) `
        -DifferenceObject @($promotionPaths | Sort-Object) `
        -CaseSensitive).Count -ne 0) {
    throw 'The promotion commit must change exactly the authorized exe ZIP path.'
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

$statusLines = @(& git -C $gitRoot status --porcelain=v1 --untracked-files=all --ignore-submodules=none)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git working tree before sealing the release.'
}
$unexpectedChanges = @(
    $statusLines | Where-Object {
        $_ -notin @(
            " M $gitManifestPath",
            " D $gitManifestPath",
            "?? $gitManifestPath",
            " M $gitFormalEvidencePath",
            " D $gitFormalEvidencePath",
            "?? $gitFormalEvidencePath"
        )
    }
)
if ($unexpectedChanges.Count -ne 0) {
    throw "Release sealing requires a clean promotion commit. Only unstaged formal release evidence may differ.`n$($unexpectedChanges -join "`n")"
}

foreach ($gitTextPath in @($gitManifestPath, $gitFormalEvidencePath)) {
    $textAttribute = (& git -C $gitRoot check-attr text -- $gitTextPath 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        -not $textAttribute.EndsWith(': text: unset', [StringComparison]::Ordinal)) {
        throw 'Formal release evidence must be marked -text in .gitattributes before sealing.'
    }
}

$archiveIdentity = Read-FilePromptReleaseArchiveIdentity `
    -ArchivePath $archivePath
$archiveHash = $archiveIdentity.ArchiveSha256
$archiveSize = (Get-Item -LiteralPath $archivePath).Length
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
if ($acceptance.ArchiveSize -ne $archiveSize -or
    -not [string]::Equals(
        $acceptance.ArchiveSha256,
        $archiveHash,
        [StringComparison]::Ordinal)) {
    throw 'The Windows 7 acceptance report does not identify the exact promoted release ZIP.'
}
$expectedText = "$archiveHash *$archiveName`r`n"
$formalEvidenceText =
    "FilePromptAI-Release-Evidence: 1`r`n" +
    "State: FORMAL-RELEASE`r`n" +
    "Version: $Version`r`n" +
    "Source-Candidate-Commit: $($receipt.Candidate)`r`n" +
    "Promotion-Commit: $headCommit`r`n" +
    "Archive-Name: $archiveName`r`n" +
    "Archive-SHA256: $archiveHash`r`n" +
    "Archive-Size: $archiveSize`r`n" +
    "Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256.txt`r`n" +
    "Package-Manifest-SHA256: $($receipt.ManifestHash)`r`n" +
    "Package-Manifest-Entry-Count: $($receipt.ManifestEntryCount)`r`n" +
    "Test-Receipt-SHA256: $($receipt.Sha256)`r`n" +
    "Windows-7-Acceptance-Report-SHA256: $($acceptance.ReportSha256)`r`n" +
    "Windows-7-Acceptance-Created-UTC: $($acceptance.CreatedUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture))`r`n" +
    "Windows-7-Acceptance-Verifier-Version: $($acceptance.VerifierVersion)`r`n"

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

$temporaryEvidence = Join-Path $projectRoot (
    '.RELEASE-EVIDENCE.' + [Guid]::NewGuid().ToString('N') + '.tmp'
)
try {
    [IO.File]::WriteAllText($temporaryEvidence, $formalEvidenceText, $utf8NoBom)
    if (Test-Path -LiteralPath $formalEvidencePath -PathType Leaf) {
        [IO.File]::Replace($temporaryEvidence, $formalEvidencePath, $null)
    }
    else {
        [IO.File]::Move($temporaryEvidence, $formalEvidencePath)
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryEvidence -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryEvidence -Force
    }
}

$writtenEvidence = Read-FilePromptFormalReleaseEvidence -Path $formalEvidencePath
if (-not [string]::Equals(
        $writtenEvidence.ReportHash,
        $acceptance.ReportSha256,
        [StringComparison]::Ordinal)) {
    throw 'The written formal release evidence failed its self-check.'
}

& powershell.exe `
    -NoLogo `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $verifyScript `
    -Version $Version `
    -ProjectRoot $distributionRoot `
    -ReleaseManifestPath $manifestPath
if ($LASTEXITCODE -ne 0) {
    throw 'The sealed release SHA-256 record failed verification.'
}

Write-Host "SEALED | $manifestPath | promotion=$headCommit | sha256=$archiveHash | manifestSha256=$($receipt.ManifestHash) | acceptanceSha256=$($acceptance.ReportSha256)"
Write-Host 'Commit only RELEASE-SHA256.txt and RELEASE-EVIDENCE.txt, then create the annotated release tag.'
