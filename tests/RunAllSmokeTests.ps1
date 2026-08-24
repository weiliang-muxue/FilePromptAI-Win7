param(
    [string]$Version = '1.17',
    [switch]$WriteReleaseReceipt
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$buildScript = Join-Path $projectRoot 'build.ps1'
$packageBuildScript = Join-Path $projectRoot 'build-offline-package.ps1'
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$archivePath = Join-Path $projectRoot $archiveName
$sidecarPath = "$archivePath.sha256.txt"
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$releaseEvidenceScript = Join-Path $testRoot 'ReleaseAcceptanceEvidence.ps1'
$receiptRelativePath = "tests/build-artifacts/release/ReleaseCandidate-v$Version.txt"
$receiptPath = Join-Path $projectRoot ($receiptRelativePath.Replace('/', '\'))
$candidateCommit = ''

if (-not (Test-Path -LiteralPath $releaseEvidenceScript -PathType Leaf)) {
    throw "The release evidence helper is missing: $releaseEvidenceScript"
}
. $releaseEvidenceScript

function Assert-CleanCandidate {
    param([string]$ExpectedCommit)

    $actualCommit = (& git -C $projectRoot rev-parse --verify HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to resolve the release candidate HEAD commit.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and
        -not [string]::Equals(
            $actualCommit,
            $ExpectedCommit,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "HEAD changed while the release suite was running: expected=$ExpectedCommit; actual=$actualCommit"
    }

    $statusLines = @(& git -C $projectRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect the release candidate working tree.'
    }
    if ($statusLines.Count -ne 0) {
        throw "Release receipt mode requires a clean committed candidate.`n$($statusLines -join "`n")"
    }

    return $actualCommit
}

if ($WriteReleaseReceipt) {
    $gitRoot = (& git -C $projectRoot rev-parse --show-toplevel 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($gitRoot).TrimEnd('\'),
            [IO.Path]::GetFullPath($projectRoot).TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release receipt mode must run from the root of its Git worktree.'
    }
    & git -C $projectRoot check-ignore -q -- $receiptRelativePath
    if ($LASTEXITCODE -ne 0) {
        throw 'The local release-candidate receipt must remain ignored by Git.'
    }
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        Remove-Item -LiteralPath $receiptPath -Force
    }
    $candidateCommit = Assert-CleanCandidate -ExpectedCommit ''
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript
if ($LASTEXITCODE -ne 0) {
    throw "Application build failed with exit code $LASTEXITCODE."
}

$scripts = @(
    'RunReleaseSha256SmokeTest.ps1',
    'RunReleaseSealingSmokeTest.ps1',
    'RunApiSmokeTest.ps1',
    'RunApiHardeningSmokeTest.ps1',
    'RunNetworkReliabilitySmokeTest.ps1',
    'RunToolLoopSmokeTest.ps1',
    'RunExtensionSettingsSmokeTest.ps1',
    'RunModelProfileSmokeTest.ps1',
    'RunGenerationSettingsSmokeTest.ps1',
    'RunMcpRuntimeSmokeTest.ps1',
    'RunConversationContextBudgetSmokeTest.ps1',
    'RunConversationStoreSmokeTest.ps1',
    'RunConversationBackupSmokeTest.ps1',
    'RunExportSmokeTest.ps1',
    'RunPresentationMindMapSmokeTest.ps1',
    'RunXMindExportSmokeTest.ps1',
    'RunExtractorSmokeTest.ps1',
    'RunExtractorHardeningSmokeTest.ps1',
    'RunMarkdownRendererSmokeTest.ps1',
    'RunUiStateSmokeTest.ps1',
    'LaunchSmokeTest.ps1'
)

foreach ($name in $scripts) {
    $script = Join-Path $testRoot $name
    Write-Host "RUN $name"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE."
    }
}

Write-Host "RUN build-offline-package.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass `
    -File $packageBuildScript `
    -Version $Version
if ($LASTEXITCODE -ne 0) {
    throw "Offline package build failed with exit code $LASTEXITCODE."
}

$packageScripts = @(
    'VerifyOfflinePackage.ps1',
    'RunVerifiedPayloadLeaseSmokeTest.ps1',
    'RunAcceptanceVerifierSmokeTest.ps1',
    'RunUninstallerSmokeTest.ps1',
    'RunUninstallerSecuritySmokeTest.ps1'
)
foreach ($name in $packageScripts) {
    $script = Join-Path $testRoot $name
    Write-Host "RUN $name"
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File $script `
        -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE."
    }
}

if ($WriteReleaseReceipt) {
    Assert-CleanCandidate -ExpectedCommit $candidateCommit | Out-Null
    $stagingManifestPath = Join-Path $stagingRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
    foreach ($required in @($archivePath, $sidecarPath, $stagingManifestPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "The tested release artifact is missing: $required"
        }
    }

    $archiveIdentity = Read-FilePromptReleaseArchiveIdentity `
        -ArchivePath $archivePath
    $archiveHash = $archiveIdentity.ArchiveSha256
    $expectedSidecar = "$archiveHash *$archiveName`r`n"
    $strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
    $sidecarBytes = [IO.File]::ReadAllBytes($sidecarPath)
    if ($sidecarBytes.Length -ge 3 -and
        $sidecarBytes[0] -eq 0xEF -and
        $sidecarBytes[1] -eq 0xBB -and
        $sidecarBytes[2] -eq 0xBF) {
        throw 'The tested release ZIP sidecar must be UTF-8 without BOM.'
    }
    $sidecarText = $strictUtf8.GetString($sidecarBytes)
    if (-not [string]::Equals(
        $sidecarText,
        $expectedSidecar,
        [StringComparison]::Ordinal)) {
        throw 'The tested release ZIP sidecar is not canonical.'
    }

    $stagingManifestIdentity = Read-FilePromptPackageManifestIdentity `
        -Path $stagingManifestPath
    if (-not [string]::Equals(
            $stagingManifestIdentity.Sha256,
            $archiveIdentity.ManifestSha256,
            [StringComparison]::Ordinal) -or
        $stagingManifestIdentity.EntryCount -ne $archiveIdentity.ManifestEntryCount) {
        throw 'The final staging directory and ZIP contain different package checksum manifests.'
    }

    $receiptText =
        "FilePromptAI-Release-Receipt: 2`r`n" +
        "Suite: tests/RunAllSmokeTests.ps1`r`n" +
        "Result: PASS`r`n" +
        "Version: $Version`r`n" +
        "Candidate-Commit: $candidateCommit`r`n" +
        "Archive-Name: $archiveName`r`n" +
        "Archive-SHA256: $archiveHash`r`n" +
        "Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256.txt`r`n" +
        "Package-Manifest-SHA256: $($archiveIdentity.ManifestSha256)`r`n" +
        "Package-Manifest-Entry-Count: $($archiveIdentity.ManifestEntryCount)`r`n"
    $receiptDirectory = Split-Path -Parent $receiptPath
    New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
    $temporaryReceipt = "$receiptPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText(
            $temporaryReceipt,
            $receiptText,
            (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            [IO.File]::Replace($temporaryReceipt, $receiptPath, $null)
        }
        else {
            [IO.File]::Move($temporaryReceipt, $receiptPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryReceipt -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryReceipt -Force
        }
    }
    Write-Host "RECEIPT | $receiptPath | candidate=$candidateCommit | sha256=$archiveHash | manifestSha256=$($archiveIdentity.ManifestSha256) | manifestEntries=$($archiveIdentity.ManifestEntryCount)"
}

$suiteCount = $scripts.Count + $packageScripts.Count
Write-Host "PASS | all smoke tests ($suiteCount suites + offline package build)"
