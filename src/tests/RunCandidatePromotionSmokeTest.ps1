param(
    [string]$Version = '1.17'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$sourcePromotionScript = Join-Path $projectRoot 'promote-release-candidate.ps1'
$sourceEvidenceHelper = Join-Path $testRoot 'ReleaseAcceptanceEvidence.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-CandidatePromotion-' + [Guid]::NewGuid().ToString('N'))
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$evidenceName = "ReleaseCandidate-v$Version.txt"
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Invoke-GitChecked {
    param(
        [string]$Root,
        [string[]]$Arguments
    )

    $output = & git -C $Root @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in $Root.`n$output"
    }
    return $output.Trim()
}

function Invoke-Promotion {
    param([object]$Fixture)

    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & powershell.exe `
            -NoLogo `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $Fixture.Script `
            -Version $Version 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Assert-Accepted {
    param(
        [string]$Description,
        [object]$Result
    )

    if ($Result.ExitCode -ne 0 -or
        $Result.Output -notmatch '(?m)^PROMOTED \|' -or
        $Result.Output -notmatch '(?m)^CANDIDATE ONLY \|') {
        throw "$Description failed unexpectedly.`n$($Result.Output)"
    }
}

function Assert-Rejected {
    param(
        [string]$Description,
        [object]$Result,
        [string]$Pattern
    )

    if ($Result.ExitCode -eq 0) {
        throw "$Description was accepted unexpectedly.`n$($Result.Output)"
    }
    if ($Result.Output -notmatch $Pattern) {
        throw "$Description failed for an unexpected reason.`n$($Result.Output)"
    }
}

function Get-DeliveryHashes {
    param([object]$Fixture)

    $result = @{}
    foreach ($path in @(
            $Fixture.DestinationArchive,
            $Fixture.DestinationSidecar,
            $Fixture.DestinationReadme,
            $Fixture.DestinationEvidence)) {
        $result[(Split-Path -Leaf $path)] = (
            Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    return $result
}

function Assert-DeliveryUnchanged {
    param(
        [string]$Description,
        [object]$Fixture,
        [hashtable]$Before
    )

    $after = Get-DeliveryHashes -Fixture $Fixture
    foreach ($name in $Before.Keys) {
        if ($after[$name] -cne $Before[$name]) {
            throw "$Description changed the existing delivery file: $name"
        }
    }
}

function New-PromotionFixture {
    param(
        [string]$Name,
        [switch]$WithoutLfs
    )

    $root = Join-Path $temporaryRoot $Name
    $sourceRoot = Join-Path $root 'src'
    $destinationRoot = Join-Path $root 'exe'
    $fixtureTests = Join-Path $sourceRoot 'tests'
    $receiptRoot = Join-Path $fixtureTests 'build-artifacts\release'
    $stagingRoot = Join-Path $sourceRoot 'package-staging'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    Copy-Item -LiteralPath $sourcePromotionScript -Destination $sourceRoot
    Copy-Item -LiteralPath $sourceEvidenceHelper -Destination $fixtureTests
    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot '.gitignore'),
        "FilePromptAI-Win7-Full-v*.zip`r`n" +
            "FilePromptAI-Win7-Full-v*.zip.sha256.txt`r`n" +
            "tests/build-artifacts/`r`n" +
            "package-staging/`r`n",
        $utf8NoBom)
    $attributes = if ($WithoutLfs) {
        "* text=auto`r`n"
    }
    else {
        "* text=auto`r`n*.zip filter=lfs diff=lfs merge=lfs -text`r`n"
    }
    [IO.File]::WriteAllText(
        (Join-Path $destinationRoot '.gitattributes'),
        $attributes,
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot 'candidate.txt'),
        "tested source candidate`r`n",
        $utf8NoBom)

    $destinationArchive = Join-Path $destinationRoot $archiveName
    $destinationSidecar = "$destinationArchive.sha256.txt"
    $destinationReadme = Join-Path $destinationRoot 'README.txt'
    $destinationEvidence = Join-Path $destinationRoot $evidenceName
    [IO.File]::WriteAllText(
        $destinationArchive,
        "old untested delivery bytes`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        $destinationSidecar,
        (('0' * 64) + " *$archiveName`r`n"),
        $utf8NoBom)
    [IO.File]::WriteAllText(
        $destinationReadme,
        "old delivery`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        $destinationEvidence,
        "old evidence must not survive`r`n",
        $utf8NoBom)

    Invoke-GitChecked -Root $root -Arguments @('init', '--quiet') | Out-Null
    Invoke-GitChecked -Root $root -Arguments @(
        'config', 'user.name', 'Promotion Test') | Out-Null
    Invoke-GitChecked -Root $root -Arguments @(
        'config', 'user.email', 'promotion-test@example.invalid') | Out-Null
    Invoke-GitChecked -Root $root -Arguments @(
        'config', 'core.autocrlf', 'false') | Out-Null
    Invoke-GitChecked -Root $root -Arguments @('add', '--', '.') | Out-Null
    Invoke-GitChecked -Root $root -Arguments @(
        'commit', '--quiet', '-m', 'tested candidate') | Out-Null
    $candidate = Invoke-GitChecked -Root $root -Arguments @(
        'rev-parse', 'HEAD')

    $payload = Join-Path $stagingRoot 'payload.txt'
    [IO.File]::WriteAllText(
        $payload,
        "promoted fixture payload`r`n",
        $utf8NoBom)
    $payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    $manifest = Join-Path $stagingRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
    [IO.File]::WriteAllText(
        $manifest,
        "$payloadHash *payload.txt`r`n",
        $utf8NoBom)
    $sourceArchive = Join-Path $sourceRoot $archiveName
    Compress-Archive `
        -Path (Join-Path $stagingRoot '*') `
        -DestinationPath $sourceArchive `
        -Force
    $archiveHash = (Get-FileHash `
        -LiteralPath $sourceArchive `
        -Algorithm SHA256).Hash
    $sourceSidecar = "$sourceArchive.sha256.txt"
    [IO.File]::WriteAllText(
        $sourceSidecar,
        "$archiveHash *$archiveName`r`n",
        $utf8NoBom)
    $manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
    $receiptPath = Join-Path $receiptRoot $evidenceName
    $receipt =
        "FilePromptAI-Release-Receipt: 2`r`n" +
        "Suite: tests/RunAllSmokeTests.ps1`r`n" +
        "Result: PASS`r`n" +
        "Version: $Version`r`n" +
        "Candidate-Commit: $candidate`r`n" +
        "Archive-Name: $archiveName`r`n" +
        "Archive-SHA256: $archiveHash`r`n" +
        "Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256.txt`r`n" +
        "Package-Manifest-SHA256: $manifestHash`r`n" +
        "Package-Manifest-Entry-Count: 1`r`n"
    [IO.File]::WriteAllText($receiptPath, $receipt, $utf8NoBom)

    return [pscustomobject]@{
        Root = $root
        SourceRoot = $sourceRoot
        DestinationRoot = $destinationRoot
        Script = Join-Path $sourceRoot 'promote-release-candidate.ps1'
        Candidate = $candidate
        SourceArchive = $sourceArchive
        SourceSidecar = $sourceSidecar
        Receipt = $receiptPath
        ArchiveHash = $archiveHash
        ArchiveSize = (Get-Item -LiteralPath $sourceArchive).Length
        ManifestHash = $manifestHash
        DestinationArchive = $destinationArchive
        DestinationSidecar = $destinationSidecar
        DestinationReadme = $destinationReadme
        DestinationEvidence = $destinationEvidence
    }
}

foreach ($required in @($sourcePromotionScript, $sourceEvidenceHelper)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required candidate-promotion source is missing: $required"
    }
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $success = New-PromotionFixture -Name 'success'
    $successResult = Invoke-Promotion -Fixture $success
    Assert-Accepted -Description 'Exact tested candidate promotion' -Result $successResult

    $sourceHash = (Get-FileHash `
        -LiteralPath $success.SourceArchive `
        -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash `
        -LiteralPath $success.DestinationArchive `
        -Algorithm SHA256).Hash
    if ($sourceHash -cne $destinationHash -or
        $destinationHash -cne $success.ArchiveHash -or
        (Get-Item -LiteralPath $success.DestinationArchive).Length -ne
            $success.ArchiveSize) {
        throw 'The promoted ZIP is not the exact tested ZIP.'
    }
    $sourceSidecarHash = (Get-FileHash `
        -LiteralPath $success.SourceSidecar `
        -Algorithm SHA256).Hash
    $destinationSidecarHash = (Get-FileHash `
        -LiteralPath $success.DestinationSidecar `
        -Algorithm SHA256).Hash
    if ($sourceSidecarHash -cne $destinationSidecarHash) {
        throw 'The promoted sidecar differs from the tested sidecar.'
    }

    $receiptHash = (Get-FileHash `
        -LiteralPath $success.Receipt `
        -Algorithm SHA256).Hash
    $evidence = [IO.File]::ReadAllText(
        $success.DestinationEvidence,
        (New-Object Text.UTF8Encoding($false, $true)))
    $expectedEvidence =
        "FilePromptAI-Candidate-Promotion: 1`r`n" +
        "State: TESTED-CANDIDATE`r`n" +
        "Version: $Version`r`n" +
        "Candidate-Commit: $($success.Candidate)`r`n" +
        "Archive-Name: $archiveName`r`n" +
        "Archive-SHA256: $($success.ArchiveHash)`r`n" +
        "Archive-Size: $($success.ArchiveSize)`r`n" +
        "Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256.txt`r`n" +
        "Package-Manifest-SHA256: $($success.ManifestHash)`r`n" +
        "Package-Manifest-Entry-Count: 1`r`n" +
        "Test-Receipt-SHA256: $receiptHash`r`n" +
        "Promotion-Scope: CANDIDATE-ONLY`r`n" +
        "Windows-7-Acceptance: NOT-ASSERTED`r`n"
    if ($evidence -cne $expectedEvidence) {
        throw 'The tracked candidate evidence is not canonical or complete.'
    }
    $readme = [IO.File]::ReadAllText(
        $success.DestinationReadme,
        (New-Object Text.UTF8Encoding($false, $true)))
    if ($readme -notmatch 'tested candidate, not a sealed release' -or
        $readme -notmatch [Text.RegularExpressions.Regex]::Escape(
            $success.ArchiveHash) -or
        $readme -notmatch 'Windows 7 acceptance is not asserted') {
        throw 'The promoted README does not identify the exact candidate state.'
    }

    $statusPaths = @(
        & git -C $success.Root status `
            --porcelain=v1 `
            --untracked-files=all 2>&1 |
            ForEach-Object { $_.Substring(3).Replace('\', '/') } |
            Sort-Object
    )
    $expectedPaths = @(
        "exe/$archiveName",
        "exe/$archiveName.sha256.txt",
        'exe/README.txt',
        "exe/$evidenceName"
    ) | Sort-Object
    if (($statusPaths -join "`n") -cne ($expectedPaths -join "`n")) {
        throw "Promotion changed paths outside the delivery set.`n$($statusPaths -join "`n")"
    }
    $headAfter = Invoke-GitChecked -Root $success.Root -Arguments @(
        'rev-parse', 'HEAD')
    if ($headAfter -cne $success.Candidate) {
        throw 'Promotion created or changed a Git commit.'
    }
    $tags = Invoke-GitChecked -Root $success.Root -Arguments @('tag', '--list')
    if (-not [string]::IsNullOrEmpty($tags)) {
        throw 'Promotion created a release tag.'
    }

    Invoke-GitChecked -Root $success.Root -Arguments @(
        'add', '--', "exe/$archiveName") | Out-Null
    $pointer = Invoke-GitChecked -Root $success.Root -Arguments @(
        'show', ":exe/$archiveName")
    $expectedPointer =
        "version https://git-lfs.github.com/spec/v1`n" +
        "oid sha256:$($success.ArchiveHash.ToLowerInvariant())`n" +
        "size $($success.ArchiveSize)"
    if ($pointer.Replace("`r`n", "`n") -cne $expectedPointer) {
        throw "The promoted ZIP did not stage as the expected Git LFS pointer.`n$pointer"
    }

    $changedZip = New-PromotionFixture -Name 'changed-source'
    $changedBefore = Get-DeliveryHashes -Fixture $changedZip
    [IO.File]::AppendAllText(
        $changedZip.SourceArchive,
        'changed after successful tests',
        $utf8NoBom)
    $changedHash = (Get-FileHash `
        -LiteralPath $changedZip.SourceArchive `
        -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        $changedZip.SourceSidecar,
        "$changedHash *$archiveName`r`n",
        $utf8NoBom)
    $changedResult = Invoke-Promotion -Fixture $changedZip
    Assert-Rejected `
        -Description 'A changed self-consistent source ZIP' `
        -Result $changedResult `
        -Pattern 'source ZIP identity does not match'
    Assert-DeliveryUnchanged `
        -Description 'Changed source ZIP rejection' `
        -Fixture $changedZip `
        -Before $changedBefore

    $stale = New-PromotionFixture -Name 'stale-receipt'
    $staleBefore = Get-DeliveryHashes -Fixture $stale
    [IO.File]::AppendAllText(
        (Join-Path $stale.SourceRoot 'candidate.txt'),
        "new commit`r`n",
        $utf8NoBom)
    Invoke-GitChecked -Root $stale.Root -Arguments @(
        'add', '--', 'src/candidate.txt') | Out-Null
    Invoke-GitChecked -Root $stale.Root -Arguments @(
        'commit', '--quiet', '-m', 'new source commit') | Out-Null
    $staleResult = Invoke-Promotion -Fixture $stale
    Assert-Rejected `
        -Description 'A receipt for an older candidate commit' `
        -Result $staleResult `
        -Pattern 'receipt does not match HEAD'
    Assert-DeliveryUnchanged `
        -Description 'Stale receipt rejection' `
        -Fixture $stale `
        -Before $staleBefore

    $dirty = New-PromotionFixture -Name 'dirty-candidate'
    $dirtyBefore = Get-DeliveryHashes -Fixture $dirty
    [IO.File]::AppendAllText(
        (Join-Path $dirty.SourceRoot 'candidate.txt'),
        "dirty source`r`n",
        $utf8NoBom)
    $dirtyResult = Invoke-Promotion -Fixture $dirty
    Assert-Rejected `
        -Description 'A dirty tested candidate' `
        -Result $dirtyResult `
        -Pattern 'requires a clean tested commit'
    Assert-DeliveryUnchanged `
        -Description 'Dirty candidate rejection' `
        -Fixture $dirty `
        -Before $dirtyBefore

    $noLfs = New-PromotionFixture -Name 'missing-lfs' -WithoutLfs
    $noLfsBefore = Get-DeliveryHashes -Fixture $noLfs
    $noLfsResult = Invoke-Promotion -Fixture $noLfs
    Assert-Rejected `
        -Description 'A delivery ZIP outside Git LFS' `
        -Result $noLfsResult `
        -Pattern 'must be tracked through Git LFS'
    Assert-DeliveryUnchanged `
        -Description 'Missing LFS rejection' `
        -Fixture $noLfs `
        -Before $noLfsBefore

    $rollback = New-PromotionFixture -Name 'transaction-rollback'
    $rollbackBefore = Get-DeliveryHashes -Fixture $rollback
    $readmeLock = [IO.File]::Open(
        $rollback.DestinationReadme,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        $rollbackResult = Invoke-Promotion -Fixture $rollback
    }
    finally {
        $readmeLock.Dispose()
    }
    Assert-Rejected `
        -Description 'A transaction interrupted after ZIP replacement' `
        -Result $rollbackResult `
        -Pattern 'promotion|process|access|used|Replace'
    Assert-DeliveryUnchanged `
        -Description 'Interrupted promotion rollback' `
        -Fixture $rollback `
        -Before $rollbackBefore
    $transactionDebris = @(
        Get-ChildItem -LiteralPath $rollback.DestinationRoot -Force -File |
            Where-Object { $_.Name -match '\.(?:new|bak|discard)$' }
    )
    if ($transactionDebris.Count -ne 0) {
        throw "Interrupted promotion left transaction files: $($transactionDebris.Name -join ', ')"
    }

    Write-Host 'PASS | tested candidate promotion topology'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
