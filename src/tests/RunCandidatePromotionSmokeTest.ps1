param(
    [string]$Version = '1.17'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$sourcePromotionScript = Join-Path $projectRoot 'promote-release-candidate.ps1'
$sourceEvidenceHelper = Join-Path $testRoot 'ReleaseAcceptanceEvidence.ps1'
$temporaryRoot = Join-Path $testRoot (
    'build-artifacts\candidate-promotion-fixtures\' +
    [Guid]::NewGuid().ToString('N'))
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$receiptName = "ReleaseCandidate-v$Version.txt"
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)

function Invoke-GitChecked {
    param([string]$Root, [string[]]$Arguments)

    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & git -C $Root @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($exitCode -ne 0) {
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
            -Version $Version 2>&1 |
            ForEach-Object { $_.ToString() } |
            Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Set-PrivateFixtureConstant {
    param(
        [string]$ScriptText,
        [string]$Disabled,
        [string]$Enabled,
        [string]$Description
    )

    $count = [Text.RegularExpressions.Regex]::Matches(
        $ScriptText,
        [Text.RegularExpressions.Regex]::Escape($Disabled)).Count
    if ($count -ne 1) {
        throw "The fixture could not enable $Description exactly once."
    }
    return $ScriptText.Replace($Disabled, $Enabled)
}

function Assert-Accepted {
    param([string]$Description, [object]$Result, [object]$Fixture)

    $journeyMarker =
        "PASS | final ZIP installed user journey | archive=$($Fixture.DestinationArchive)"
    if ($Result.ExitCode -ne 0 -or
        $Result.Output -notmatch '(?m)^PROMOTED \|' -or
        $Result.Output -notmatch '(?m)^CANDIDATE ONLY \|' -or
        @($Result.Output -split "`r?`n" | Where-Object {
            $_ -ceq $journeyMarker
        }).Count -ne 1) {
        throw "$Description failed unexpectedly.`n$($Result.Output)"
    }
}

function Assert-Rejected {
    param([string]$Description, [object]$Result, [string]$Pattern)

    if ($Result.ExitCode -eq 0) {
        throw "$Description was accepted unexpectedly.`n$($Result.Output)"
    }
    if ($Result.Output -notmatch $Pattern) {
        throw "$Description failed for an unexpected reason.`n$($Result.Output)"
    }
}

function Get-DeliverySnapshot {
    param([string]$Root)

    $snapshot = @{}
    foreach ($entry in @(Get-ChildItem -LiteralPath $Root -Force)) {
        $snapshot[$entry.Name] = if ($entry.PSIsContainer) {
            '<directory>'
        }
        else {
            (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
        }
    }
    return $snapshot
}

function Assert-DeliverySnapshotEqual {
    param([hashtable]$Before, [hashtable]$After, [string]$Description)

    $beforeNames = @($Before.Keys | Sort-Object)
    $afterNames = @($After.Keys | Sort-Object)
    if (($beforeNames -join "`n") -cne ($afterNames -join "`n")) {
        throw "$Description changed the delivery inventory."
    }
    foreach ($name in $beforeNames) {
        if ($Before[$name] -cne $After[$name]) {
            throw "$Description changed delivery bytes: $name"
        }
    }
}

function Assert-ExactDeliveryInventory {
    param([object]$Fixture, [string]$Description)

    $entries = @(Get-ChildItem -LiteralPath $Fixture.DestinationRoot -Force)
    if ($entries.Count -ne 1 -or
        $entries[0].PSIsContainer -or
        $entries[0].Name -cne $archiveName) {
        throw "$Description did not leave exactly one delivery ZIP.`n$($entries.Name -join "`n")"
    }
}

function Assert-NoPromotionTransactions {
    param([object]$Fixture, [string]$Description)

    $promotionRoot = Join-Path $Fixture.SourceRoot (
        'tests\build-artifacts\promotion')
    $entries = @(if (Test-Path `
            -LiteralPath $promotionRoot `
            -PathType Container) {
        Get-ChildItem -LiteralPath $promotionRoot -Force
    })
    if ($entries.Count -ne 0) {
        throw "$Description left transaction evidence: $($entries.Name -join ', ')"
    }
}

function New-PromotionFixture {
    param(
        [string]$Name,
        [ValidateRange(0, 1)]
        [int]$CrashAfterReplacement = 0,
        [ValidateRange(0, 1)]
        [int]$ThrowAfterReplacement = 0,
        [switch]$StopAfterRecovery,
        [switch]$FailInstalledJourney,
        [switch]$MissingDestination
    )

    $root = Join-Path $temporaryRoot $Name
    $sourceRoot = Join-Path $root 'src'
    $destinationRoot = Join-Path $root 'exe'
    $fixtureTests = Join-Path $sourceRoot 'tests'
    $receiptRoot = Join-Path $fixtureTests 'build-artifacts\release'
    $stagingRoot = Join-Path $sourceRoot 'package-staging'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    if (-not $MissingDestination) {
        New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    }
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    Copy-Item -LiteralPath $sourcePromotionScript -Destination $sourceRoot
    Copy-Item -LiteralPath $sourceEvidenceHelper -Destination $fixtureTests
    $fixtureInstalledJourney = Join-Path $fixtureTests (
        'RunInstalledUserJourneySmokeTest.ps1')
    $journeyText =
        "param([string]`$Version = '1.17', [string]`$ArchivePath = '')`r`n" +
        "`$resolved = [IO.Path]::GetFullPath(`$ArchivePath)`r`n"
    if ($FailInstalledJourney) {
        $journeyText +=
            "[Console]::Error.WriteLine('injected installed journey failure')`r`n" +
            "exit 55`r`n"
    }
    else {
        $journeyText +=
            'Write-Host ("PASS | final ZIP installed user journey | archive=$resolved")' +
            "`r`nexit 0`r`n"
    }
    [IO.File]::WriteAllText(
        $fixtureInstalledJourney,
        $journeyText,
        $utf8NoBom)

    $fixtureScript = Join-Path $sourceRoot 'promote-release-candidate.ps1'
    $scriptText = [IO.File]::ReadAllText($fixtureScript, $strictUtf8)
    if ($CrashAfterReplacement -gt 0) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$crashAfterReplacementForTests = 0' `
            -Enabled '$crashAfterReplacementForTests = 1' `
            -Description 'the replacement crash'
    }
    if ($ThrowAfterReplacement -gt 0) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$throwAfterReplacementForTests = 0' `
            -Enabled '$throwAfterReplacementForTests = 1' `
            -Description 'the post-replacement exception'
    }
    if ($StopAfterRecovery) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$stopAfterRecoveryForTests = $false' `
            -Enabled '$stopAfterRecoveryForTests = $true' `
            -Description 'the recovery stop'
    }
    [IO.File]::WriteAllText($fixtureScript, $scriptText, $utf8NoBom)

    [IO.File]::WriteAllText(
        (Join-Path $root '.gitignore'),
        "src/FilePromptAI-Win7-Full-v*.zip`r`n" +
            "src/tests/build-artifacts/`r`n" +
            "src/package-staging/`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $root '.gitattributes'),
        "* text=auto`r`n" +
            "exe/*.zip filter=lfs diff=lfs merge=lfs -text`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot 'candidate.txt'),
        "tested source candidate`r`n",
        $utf8NoBom)
    $destinationArchive = Join-Path $destinationRoot $archiveName
    if (-not $MissingDestination) {
        [IO.File]::WriteAllText(
            $destinationArchive,
            "old untested delivery bytes`r`n",
            $utf8NoBom)
    }

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
    $manifestHash = (Get-FileHash `
        -LiteralPath $manifest `
        -Algorithm SHA256).Hash
    $receiptPath = Join-Path $receiptRoot $receiptName
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
        Script = $fixtureScript
        Candidate = $candidate
        SourceArchive = $sourceArchive
        ArchiveHash = $archiveHash
        ArchiveSize = (Get-Item -LiteralPath $sourceArchive).Length
        DestinationArchive = $destinationArchive
    }
}

foreach ($required in @($sourcePromotionScript, $sourceEvidenceHelper)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required candidate-promotion source is missing: $required"
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $success = New-PromotionFixture -Name 'success'
    $successResult = Invoke-Promotion -Fixture $success
    Assert-Accepted `
        -Description 'Exact single-ZIP candidate promotion' `
        -Result $successResult `
        -Fixture $success
    Assert-ExactDeliveryInventory `
        -Fixture $success `
        -Description 'Exact single-ZIP candidate promotion'
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
        throw 'The promoted ZIP is not the exact receipt-bound source ZIP.'
    }
    Assert-NoPromotionTransactions `
        -Fixture $success `
        -Description 'Exact single-ZIP candidate promotion'

    $freshCheckout = New-PromotionFixture `
        -Name 'fresh-checkout' `
        -MissingDestination
    if (Test-Path -LiteralPath $freshCheckout.DestinationRoot) {
        throw 'The fresh-checkout fixture unexpectedly contains an exe directory.'
    }
    $freshCheckoutResult = Invoke-Promotion -Fixture $freshCheckout
    Assert-Accepted `
        -Description 'Fresh checkout without an exe directory' `
        -Result $freshCheckoutResult `
        -Fixture $freshCheckout
    Assert-ExactDeliveryInventory `
        -Fixture $freshCheckout `
        -Description 'Fresh checkout without an exe directory'
    Assert-NoPromotionTransactions `
        -Fixture $freshCheckout `
        -Description 'Fresh checkout without an exe directory'

    $statusPaths = @(
        & git -C $success.Root status `
            --porcelain=v1 `
            --untracked-files=all 2>&1 |
            ForEach-Object { $_.Substring(3).Replace('\', '/') }
    )
    if ($statusPaths.Count -ne 1 -or
        $statusPaths[0] -cne "exe/$archiveName") {
        throw "Promotion changed paths outside the single ZIP.`n$($statusPaths -join "`n")"
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

    $extra = New-PromotionFixture -Name 'extra-delivery-file'
    [IO.File]::WriteAllText(
        (Join-Path $extra.DestinationRoot 'README.txt'),
        "unauthorized delivery file`r`n",
        $utf8NoBom)
    $extraBefore = Get-DeliverySnapshot -Root $extra.DestinationRoot
    $extraResult = Invoke-Promotion -Fixture $extra
    Assert-Rejected `
        -Description 'A delivery directory containing an extra file' `
        -Result $extraResult `
        -Pattern 'obsolete or unauthorized entry: README\.txt'
    Assert-DeliverySnapshotEqual `
        -Before $extraBefore `
        -After (Get-DeliverySnapshot -Root $extra.DestinationRoot) `
        -Description 'Extra delivery file rejection'

    $journeyFailure = New-PromotionFixture `
        -Name 'installed-journey-failure' `
        -FailInstalledJourney
    $journeyBefore = Get-DeliverySnapshot `
        -Root $journeyFailure.DestinationRoot
    $journeyResult = Invoke-Promotion -Fixture $journeyFailure
    Assert-Rejected `
        -Description 'A promoted ZIP whose installed journey fails' `
        -Result $journeyResult `
        -Pattern 'final installed user journey|injected installed journey failure'
    Assert-DeliverySnapshotEqual `
        -Before $journeyBefore `
        -After (Get-DeliverySnapshot -Root $journeyFailure.DestinationRoot) `
        -Description 'Installed journey failure rollback'
    Assert-ExactDeliveryInventory `
        -Fixture $journeyFailure `
        -Description 'Installed journey failure rollback'
    Assert-NoPromotionTransactions `
        -Fixture $journeyFailure `
        -Description 'Installed journey failure rollback'

    $thrown = New-PromotionFixture `
        -Name 'transaction-rollback' `
        -ThrowAfterReplacement 1
    $throwBefore = Get-DeliverySnapshot -Root $thrown.DestinationRoot
    $throwResult = Invoke-Promotion -Fixture $thrown
    Assert-Rejected `
        -Description 'A catchable failure after ZIP replacement' `
        -Result $throwResult `
        -Pattern 'injected failure after replacement 1'
    Assert-DeliverySnapshotEqual `
        -Before $throwBefore `
        -After (Get-DeliverySnapshot -Root $thrown.DestinationRoot) `
        -Description 'Catchable replacement failure rollback'
    Assert-NoPromotionTransactions `
        -Fixture $thrown `
        -Description 'Catchable replacement failure rollback'

    $crash = New-PromotionFixture `
        -Name 'crash-recovery' `
        -CrashAfterReplacement 1 `
        -StopAfterRecovery
    $crashBefore = Get-DeliverySnapshot -Root $crash.DestinationRoot
    $crashResult = Invoke-Promotion -Fixture $crash
    if ($crashResult.ExitCode -eq 0) {
        throw 'The replacement crash fixture did not terminate promotion.'
    }
    $recoveryResult = Invoke-Promotion -Fixture $crash
    if ($recoveryResult.ExitCode -ne 0 -or
        $recoveryResult.Output -notmatch 'PROMOTION RECOVERED ROLLED BACK' -or
        $recoveryResult.Output -notmatch 'PROMOTION RECOVERY TEST STOP') {
        throw "The single-ZIP crash transaction was not recovered.`n$($recoveryResult.Output)"
    }
    Assert-DeliverySnapshotEqual `
        -Before $crashBefore `
        -After (Get-DeliverySnapshot -Root $crash.DestinationRoot) `
        -Description 'Single-ZIP crash recovery'
    Assert-NoPromotionTransactions `
        -Fixture $crash `
        -Description 'Single-ZIP crash recovery'

    $locked = New-PromotionFixture -Name 'promotion-lock'
    $lockedBefore = Get-DeliverySnapshot -Root $locked.DestinationRoot
    $lockPath = Join-Path $locked.SourceRoot (
        'tests\build-artifacts\promotion.lock')
    $lockStream = [IO.File]::Open(
        $lockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $lockResult = Invoke-Promotion -Fixture $locked
    }
    finally {
        $lockStream.Dispose()
    }
    Assert-Rejected `
        -Description 'A promotion while its lock is held' `
        -Result $lockResult `
        -Pattern 'Another candidate promotion is already running|promotion lock cannot be acquired'
    Assert-DeliverySnapshotEqual `
        -Before $lockedBefore `
        -After (Get-DeliverySnapshot -Root $locked.DestinationRoot) `
        -Description 'Promotion lock rejection'
    $afterUnlock = Invoke-Promotion -Fixture $locked
    Assert-Accepted `
        -Description 'Promotion after lock release' `
        -Result $afterUnlock `
        -Fixture $locked
    Assert-ExactDeliveryInventory `
        -Fixture $locked `
        -Description 'Promotion after lock release'

    $changed = New-PromotionFixture -Name 'changed-source-zip'
    $changedBefore = Get-DeliverySnapshot -Root $changed.DestinationRoot
    [IO.File]::AppendAllText(
        $changed.SourceArchive,
        'changed after successful tests',
        $utf8NoBom)
    $changedResult = Invoke-Promotion -Fixture $changed
    Assert-Rejected `
        -Description 'A source ZIP changed after its successful receipt' `
        -Result $changedResult `
        -Pattern 'source ZIP identity does not match'
    Assert-DeliverySnapshotEqual `
        -Before $changedBefore `
        -After (Get-DeliverySnapshot -Root $changed.DestinationRoot) `
        -Description 'Changed source ZIP rejection'

    Write-Host 'PASS | single-ZIP tested candidate promotion contract'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
