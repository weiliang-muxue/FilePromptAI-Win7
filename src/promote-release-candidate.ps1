param(
    [string]$Version = '1.17',
    [string]$SourceRoot,
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^[0-9A-Za-z](?:[0-9A-Za-z._-]{0,30}[0-9A-Za-z])?$') {
    throw 'Version may contain only letters, digits, dots, underscores, and hyphens.'
}

$SourceProjectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $MyInvocation.MyCommand.Path))
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = $SourceProjectRoot
}
if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path (Split-Path -Parent $SourceProjectRoot) 'exe'
}
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$sourceArchive = Join-Path $SourceRoot $archiveName
$sourceSidecar = "$sourceArchive.sha256.txt"
$receiptPath = Join-Path $SourceRoot (
    "tests\build-artifacts\release\ReleaseCandidate-v$Version.txt")
$evidenceName = "ReleaseCandidate-v$Version.txt"
$destinationArchive = Join-Path $DestinationRoot $archiveName
$destinationSidecar = "$destinationArchive.sha256.txt"
$destinationReadme = Join-Path $DestinationRoot 'README.txt'
$destinationEvidence = Join-Path $DestinationRoot $evidenceName
$evidenceHelper = Join-Path $SourceRoot 'tests\ReleaseAcceptanceEvidence.ps1'
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Read-CanonicalReceipt {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The successful release-candidate receipt is missing: $Path"
    }
    if ((Get-Item -LiteralPath $Path).Length -gt 16384) {
        throw 'The release-candidate receipt exceeds the 16 KB safety limit.'
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
        'Package-Manifest-Entry-Count: (?<ManifestCount>[1-9][0-9]{0,8})\r\n\z'
    $match = [Text.RegularExpressions.Regex]::Match(
        $text,
        $pattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw 'The release-candidate receipt has an invalid or non-canonical format.'
    }
    $manifestCount = 0
    if (-not [int]::TryParse(
            $match.Groups['ManifestCount'].Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$manifestCount)) {
        throw 'The release-candidate receipt has an invalid manifest count.'
    }
    return [pscustomobject]@{
        Bytes = $bytes
        Version = $match.Groups['Version'].Value
        Candidate = $match.Groups['Candidate'].Value
        Archive = $match.Groups['Archive'].Value
        Hash = $match.Groups['Hash'].Value
        ManifestHash = $match.Groups['ManifestHash'].Value
        ManifestCount = $manifestCount
    }
}

function Write-AtomicBytes {
    param(
        [string]$Path,
        [byte[]]$Bytes
    )

    $temporary = Join-Path (Split-Path -Parent $Path) (
        '.' + (Split-Path -Leaf $Path) + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = "$temporary.bak"
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporary, $Path, $backup)
            Remove-Item -LiteralPath $backup -Force
        }
        else {
            [IO.File]::Move($temporary, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}

function Copy-AtomicFile {
    param(
        [string]$Source,
        [string]$Destination
    )

    $temporary = Join-Path (Split-Path -Parent $Destination) (
        '.' + (Split-Path -Leaf $Destination) + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp')
    $backup = "$temporary.bak"
    $input = $null
    $output = $null
    try {
        $input = [IO.File]::Open(
            $Source,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $output = [IO.File]::Open(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $input.CopyTo($output, 1048576)
        $output.Flush($true)
        $output.Dispose()
        $output = $null
        $input.Dispose()
        $input = $null
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            [IO.File]::Replace($temporary, $Destination, $backup)
            Remove-Item -LiteralPath $backup -Force
        }
        else {
            [IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        if ($null -ne $output) {
            $output.Dispose()
        }
        if ($null -ne $input) {
            $input.Dispose()
        }
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}

function Install-PromotionTransaction {
    param(
        [object[]]$Items,
        [scriptblock]$Validate
    )

    foreach ($item in $Items) {
        if (Test-Path -LiteralPath $item.Target -PathType Container) {
            throw "A candidate delivery target is a directory: $($item.Target)"
        }
        $item.Existed = Test-Path -LiteralPath $item.Target -PathType Leaf
    }

    $installed = New-Object Collections.ArrayList
    $transactionComplete = $false
    $rollbackComplete = $false
    try {
        foreach ($item in $Items) {
            if ($item.Existed) {
                [IO.File]::Replace($item.Staged, $item.Target, $item.Backup)
            }
            else {
                [IO.File]::Move($item.Staged, $item.Target)
            }
            [void]$installed.Add($item)
        }
        & $Validate
        $transactionComplete = $true
    }
    catch {
        $installError = $_
        $rollbackErrors = New-Object Collections.Generic.List[string]
        for ($index = $installed.Count - 1; $index -ge 0; $index--) {
            $item = $installed[$index]
            try {
                if ($item.Existed) {
                    if (Test-Path -LiteralPath $item.Target -PathType Leaf) {
                        [IO.File]::Replace(
                            $item.Backup,
                            $item.Target,
                            $item.Discard)
                        Remove-Item -LiteralPath $item.Discard -Force
                    }
                    else {
                        [IO.File]::Move($item.Backup, $item.Target)
                    }
                }
                elseif (Test-Path -LiteralPath $item.Target -PathType Leaf) {
                    Remove-Item -LiteralPath $item.Target -Force
                }
            }
            catch {
                $rollbackErrors.Add(
                    "$($item.Target): $($_.Exception.Message)")
            }
        }
        $rollbackComplete = $rollbackErrors.Count -eq 0
        if (-not $rollbackComplete) {
            throw "Candidate promotion failed and rollback was incomplete. " +
                "Recovery files were preserved.`nInstall error: " +
                "$($installError.Exception.Message)`nRollback errors:`n" +
                ($rollbackErrors -join "`n")
        }
        throw $installError
    }
    finally {
        foreach ($item in $Items) {
            if (Test-Path -LiteralPath $item.Staged -PathType Leaf) {
                Remove-Item -LiteralPath $item.Staged -Force
            }
            if (($transactionComplete -or $rollbackComplete) -and
                (Test-Path -LiteralPath $item.Backup -PathType Leaf)) {
                Remove-Item -LiteralPath $item.Backup -Force
            }
            if (($transactionComplete -or $rollbackComplete) -and
                (Test-Path -LiteralPath $item.Discard -PathType Leaf)) {
                Remove-Item -LiteralPath $item.Discard -Force
            }
        }
    }
}

foreach ($required in @(
        $sourceArchive,
        $sourceSidecar,
        $receiptPath,
        $evidenceHelper)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required tested-candidate input is missing: $required"
    }
}
if (-not (Test-Path -LiteralPath $DestinationRoot -PathType Container)) {
    throw "The candidate delivery directory is missing: $DestinationRoot"
}

. $evidenceHelper
$receipt = Read-CanonicalReceipt -Path $receiptPath
if ($receipt.Version -cne $Version -or $receipt.Archive -cne $archiveName) {
    throw 'The release-candidate receipt is for a different version or archive.'
}

$headCommit = (& git -C $SourceRoot rev-parse --verify HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not [string]::Equals(
        $headCommit,
        $receipt.Candidate,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "The tested candidate receipt does not match HEAD: receipt=$($receipt.Candidate); HEAD=$headCommit"
}
$statusLines = @(& git -C $SourceRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the tested candidate working tree.'
}
if ($statusLines.Count -ne 0) {
    throw "Candidate promotion requires a clean tested commit.`n$($statusLines -join "`n")"
}

$gitRoot = (& git -C $SourceRoot rev-parse --show-toplevel 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Candidate promotion requires a Git worktree.'
}
$lfsVersion = (& git -C $gitRoot lfs version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $lfsVersion -notmatch '^git-lfs/') {
    throw 'Candidate promotion requires Git LFS.'
}
$gitRootPrefix = [IO.Path]::GetFullPath($gitRoot).TrimEnd('\') + '\'
$receiptFullPath = [IO.Path]::GetFullPath($receiptPath)
if (-not $receiptFullPath.StartsWith(
        $gitRootPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release-candidate receipt must be inside the tested Git worktree.'
}
$gitReceipt = $receiptFullPath.Substring(
    $gitRootPrefix.Length).Replace('\', '/')
& git -C $gitRoot check-ignore -q -- $gitReceipt
if ($LASTEXITCODE -ne 0) {
    throw 'The local release-candidate receipt must remain ignored by Git.'
}
$destinationFullPath = [IO.Path]::GetFullPath($destinationArchive)
if (-not $destinationFullPath.StartsWith(
        $gitRootPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The candidate delivery directory must be inside the tested Git worktree.'
}
$gitDestination = $destinationFullPath.Substring(
    $gitRootPrefix.Length).Replace('\', '/')
$lfsFilter = (& git -C $gitRoot check-attr filter -- $gitDestination 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not $lfsFilter.EndsWith(': filter: lfs', [StringComparison]::Ordinal)) {
    throw 'The promoted ZIP path must be tracked through Git LFS.'
}
foreach ($trackablePath in @(
        $destinationArchive,
        $destinationSidecar,
        $destinationReadme,
        $destinationEvidence)) {
    $gitPath = [IO.Path]::GetFullPath($trackablePath).Substring(
        $gitRootPrefix.Length).Replace('\', '/')
    & git -C $gitRoot check-ignore -q -- $gitPath
    if ($LASTEXITCODE -eq 0) {
        throw "The promoted candidate evidence path must be trackable: $gitPath"
    }
    if ($LASTEXITCODE -ne 1) {
        throw "Unable to check whether the promoted candidate path is ignored: $gitPath"
    }
}

$sourceIdentity = Read-FilePromptReleaseArchiveIdentity `
    -ArchivePath $sourceArchive
if ($sourceIdentity.ArchiveSha256 -cne $receipt.Hash -or
    $sourceIdentity.ManifestSha256 -cne $receipt.ManifestHash -or
    $sourceIdentity.ManifestEntryCount -ne $receipt.ManifestCount) {
    throw 'The source ZIP identity does not match the successful test receipt.'
}
$archiveSize = (Get-Item -LiteralPath $sourceArchive).Length
$expectedSidecar = "$($receipt.Hash) *$archiveName`r`n"
$sourceSidecarLength = (Get-Item -LiteralPath $sourceSidecar).Length
if ($sourceSidecarLength -le 0 -or $sourceSidecarLength -gt 1024) {
    throw 'The source ZIP sidecar has an invalid size.'
}
$sourceSidecarBytes = [IO.File]::ReadAllBytes($sourceSidecar)
if ($strictUtf8.GetString($sourceSidecarBytes) -cne $expectedSidecar) {
    throw 'The source ZIP sidecar is not the canonical tested checksum record.'
}

$receiptHash = Get-FilePromptSha256Hex -Bytes $receipt.Bytes
$readmeText =
    "FilePrompt AI for Windows 7 - v$Version tested candidate`r`n" +
    "========================================================`r`n`r`n" +
    "This directory contains a tested candidate, not a sealed release.`r`n" +
    "Archive: $archiveName`r`n" +
    "SHA-256: $($receipt.Hash)`r`n" +
    "Candidate commit: $($receipt.Candidate)`r`n" +
    "Candidate evidence: $evidenceName`r`n`r`n" +
    "Windows 7 acceptance is not asserted by this promotion. Formal release`r`n" +
    "still requires the acceptance, sealing, and annotated-tag gates.`r`n"
$evidenceText =
    "FilePromptAI-Candidate-Promotion: 1`r`n" +
    "State: TESTED-CANDIDATE`r`n" +
    "Version: $Version`r`n" +
    "Candidate-Commit: $($receipt.Candidate)`r`n" +
    "Archive-Name: $archiveName`r`n" +
    "Archive-SHA256: $($receipt.Hash)`r`n" +
    "Archive-Size: $archiveSize`r`n" +
    "Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256.txt`r`n" +
    "Package-Manifest-SHA256: $($receipt.ManifestHash)`r`n" +
    "Package-Manifest-Entry-Count: $($receipt.ManifestCount)`r`n" +
    "Test-Receipt-SHA256: $receiptHash`r`n" +
    "Promotion-Scope: CANDIDATE-ONLY`r`n" +
    "Windows-7-Acceptance: NOT-ASSERTED`r`n"

$transactionId = [Guid]::NewGuid().ToString('N')
$items = @(
    [pscustomobject]@{
        Target = $destinationArchive
        Staged = Join-Path $DestinationRoot ".$archiveName.$transactionId.new"
        Backup = Join-Path $DestinationRoot ".$archiveName.$transactionId.bak"
        Discard = Join-Path $DestinationRoot ".$archiveName.$transactionId.discard"
        Existed = $false
    },
    [pscustomobject]@{
        Target = $destinationSidecar
        Staged = Join-Path $DestinationRoot ".$archiveName.sha256.txt.$transactionId.new"
        Backup = Join-Path $DestinationRoot ".$archiveName.sha256.txt.$transactionId.bak"
        Discard = Join-Path $DestinationRoot ".$archiveName.sha256.txt.$transactionId.discard"
        Existed = $false
    },
    [pscustomobject]@{
        Target = $destinationReadme
        Staged = Join-Path $DestinationRoot ".README.txt.$transactionId.new"
        Backup = Join-Path $DestinationRoot ".README.txt.$transactionId.bak"
        Discard = Join-Path $DestinationRoot ".README.txt.$transactionId.discard"
        Existed = $false
    },
    [pscustomobject]@{
        Target = $destinationEvidence
        Staged = Join-Path $DestinationRoot ".$evidenceName.$transactionId.new"
        Backup = Join-Path $DestinationRoot ".$evidenceName.$transactionId.bak"
        Discard = Join-Path $DestinationRoot ".$evidenceName.$transactionId.discard"
        Existed = $false
    }
)
try {
    Copy-AtomicFile -Source $sourceArchive -Destination $items[0].Staged
    Write-AtomicBytes -Path $items[1].Staged -Bytes $sourceSidecarBytes
    Write-AtomicBytes `
        -Path $items[2].Staged `
        -Bytes $utf8NoBom.GetBytes($readmeText)
    Write-AtomicBytes `
        -Path $items[3].Staged `
        -Bytes $utf8NoBom.GetBytes($evidenceText)

    $sourceIdentityAfterCopy = Read-FilePromptReleaseArchiveIdentity `
        -ArchivePath $sourceArchive
    $stagedIdentity = Read-FilePromptReleaseArchiveIdentity `
        -ArchivePath $items[0].Staged
    if ($sourceIdentityAfterCopy.ArchiveSha256 -cne $receipt.Hash -or
        $sourceIdentityAfterCopy.ManifestSha256 -cne $receipt.ManifestHash -or
        $sourceIdentityAfterCopy.ManifestEntryCount -ne $receipt.ManifestCount -or
        (Get-Item -LiteralPath $sourceArchive).Length -ne $archiveSize -or
        $stagedIdentity.ArchiveSha256 -cne $receipt.Hash -or
        $stagedIdentity.ManifestSha256 -cne $receipt.ManifestHash -or
        $stagedIdentity.ManifestEntryCount -ne $receipt.ManifestCount -or
        (Get-Item -LiteralPath $items[0].Staged).Length -ne $archiveSize) {
        throw 'The staged ZIP differs from the successfully tested source ZIP.'
    }
    $stagedSidecarBytes = [IO.File]::ReadAllBytes($items[1].Staged)
    if ($sourceSidecarBytes.Length -ne $stagedSidecarBytes.Length -or
        (Get-FilePromptSha256Hex -Bytes $sourceSidecarBytes) -cne
            (Get-FilePromptSha256Hex -Bytes $stagedSidecarBytes)) {
        throw 'The staged sidecar differs from the tested sidecar.'
    }

    $validateInstalled = {
        $destinationIdentity = Read-FilePromptReleaseArchiveIdentity `
            -ArchivePath $destinationArchive
        if ($destinationIdentity.ArchiveSha256 -cne $receipt.Hash -or
            $destinationIdentity.ManifestSha256 -cne $receipt.ManifestHash -or
            $destinationIdentity.ManifestEntryCount -ne $receipt.ManifestCount -or
            (Get-Item -LiteralPath $destinationArchive).Length -ne $archiveSize) {
            throw 'The promoted ZIP differs from the successfully tested source ZIP.'
        }
        $installedSidecar = [IO.File]::ReadAllBytes($destinationSidecar)
        if ($sourceSidecarBytes.Length -ne $installedSidecar.Length -or
            (Get-FilePromptSha256Hex -Bytes $sourceSidecarBytes) -cne
                (Get-FilePromptSha256Hex -Bytes $installedSidecar)) {
            throw 'The promoted sidecar differs from the tested sidecar.'
        }
        if ([IO.File]::ReadAllText($destinationReadme, $strictUtf8) -cne
                $readmeText -or
            [IO.File]::ReadAllText($destinationEvidence, $strictUtf8) -cne
                $evidenceText) {
            throw 'The promoted candidate metadata failed exact verification.'
        }
    }
    Install-PromotionTransaction -Items $items -Validate $validateInstalled
}
finally {
    foreach ($item in $items) {
        if (Test-Path -LiteralPath $item.Staged -PathType Leaf) {
            Remove-Item -LiteralPath $item.Staged -Force
        }
    }
}

Write-Host "PROMOTED | $destinationArchive | candidate=$($receipt.Candidate) | sha256=$($receipt.Hash) | bytes=$archiveSize | manifestSha256=$($receipt.ManifestHash) | manifestEntries=$($receipt.ManifestCount)"
Write-Host 'CANDIDATE ONLY | Windows 7 acceptance and release sealing are still required.'
