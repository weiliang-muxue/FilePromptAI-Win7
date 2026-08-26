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
$evidenceName = "ReleaseCandidate-v$Version.txt"
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$strictUtf8 = New-Object Text.UTF8Encoding($false, $true)

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
            -Version $Version 2>&1 |
            ForEach-Object { $_.ToString() } |
            Out-String
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

function Start-PromotionProcess {
    param([object]$Fixture)

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = (Get-Command powershell.exe).Source
    $startInfo.Arguments =
        '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        '-File "' + $Fixture.Script.Replace('"', '\"') + '" ' +
        '-Version "' + $Version.Replace('"', '\"') + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Unable to start the candidate-promotion child process.'
    }
    return [pscustomobject]@{
        Process = $process
        StandardOutput = $process.StandardOutput.ReadToEndAsync()
        StandardError = $process.StandardError.ReadToEndAsync()
    }
}

function Complete-PromotionProcess {
    param(
        [object]$Running,
        [int]$TimeoutMilliseconds = 60000
    )

    try {
        if (-not $Running.Process.WaitForExit($TimeoutMilliseconds)) {
            try { $Running.Process.Kill() } catch {}
            throw 'The candidate-promotion child process timed out.'
        }
        $Running.Process.WaitForExit()
        return [pscustomobject]@{
            ExitCode = $Running.Process.ExitCode
            Output = $Running.StandardOutput.Result +
                $Running.StandardError.Result
        }
    }
    finally {
        $Running.Process.Dispose()
    }
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
        throw "The private promotion fixture could not enable $Description exactly once."
    }
    return $ScriptText.Replace($Disabled, $Enabled)
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

function Get-DeliverySnapshot {
    param([string]$Root)

    $snapshot = @{}
    foreach ($entry in @(Get-ChildItem -LiteralPath $Root -Force)) {
        if ($entry.PSIsContainer) {
            $snapshot[$entry.Name] = '<directory>'
        }
        else {
            $snapshot[$entry.Name] = (
                Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256
            ).Hash
        }
    }
    return $snapshot
}

function Assert-DeliverySnapshotEqual {
    param(
        [hashtable]$Before,
        [hashtable]$After,
        [string]$Description
    )

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

function Get-PromotionTransactionEntries {
    param([object]$Fixture)

    $promotionRoot = Join-Path $Fixture.SourceRoot (
        'tests\build-artifacts\promotion')
    if (-not (Test-Path -LiteralPath $promotionRoot -PathType Container)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $promotionRoot -Force)
}

function Assert-ExactDeliveryInventory {
    param(
        [object]$Fixture,
        [string]$Description
    )

    $expected = @(
        '.gitattributes',
        $archiveName,
        "$archiveName.sha256.txt",
        'README.txt',
        $evidenceName
    ) | Sort-Object
    $actual = @(
        Get-ChildItem -LiteralPath $Fixture.DestinationRoot -Force |
            ForEach-Object { $_.Name } |
            Sort-Object
    )
    if (($actual -join "`n") -cne ($expected -join "`n")) {
        throw "$Description did not leave the exact delivery inventory.`n$($actual -join "`n")"
    }
}

function New-PromotionFixture {
    param(
        [string]$Name,
        [switch]$WithoutLfs,
        [switch]$InjectPostCommitCleanupFailure,
        [ValidateRange(0, 4)]
        [int]$CrashAfterReplacement = 0,
        [ValidateRange(0, 4)]
        [int]$ThrowAfterReplacement = 0,
        [ValidateRange(0, 16)]
        [int]$CrashAfterCleanupDeletion = 0,
        [ValidateRange(0, 60000)]
        [int]$HoldPromotionLockMilliseconds = 0,
        [switch]$StopAfterRecovery,
        [switch]$FailInstalledJourney
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
    $fixtureInstalledJourney = Join-Path $fixtureTests (
        'RunInstalledUserJourneySmokeTest.ps1')
    $installedJourneyFixture =
        "param([string]`$Version = '1.17', [string]`$ArchivePath = '')`r`n" +
        "`$resolved = [IO.Path]::GetFullPath(`$ArchivePath)`r`n"
    if ($FailInstalledJourney) {
        $installedJourneyFixture +=
            "[Console]::Error.WriteLine('injected installed journey failure')`r`n" +
            "exit 55`r`n"
    }
    else {
        $installedJourneyFixture +=
            'Write-Host ("PASS | final ZIP installed user journey | archive=$resolved")' +
            "`r`n" +
            "exit 0`r`n"
    }
    [IO.File]::WriteAllText(
        $fixtureInstalledJourney,
        $installedJourneyFixture,
        $utf8NoBom)
    $fixtureScript = Join-Path $sourceRoot 'promote-release-candidate.ps1'
    $scriptText = [IO.File]::ReadAllText($fixtureScript, $strictUtf8)
    if ($InjectPostCommitCleanupFailure) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$postCommitCleanupFailureForTests = $false' `
            -Enabled ('$postCommitCleanupFailureForTests = $true' +
                "`r`n" + '$WarningPreference = ''Stop''') `
            -Description 'the post-commit cleanup failure'
    }
    if ($CrashAfterReplacement -gt 0) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$crashAfterReplacementForTests = 0' `
            -Enabled ('$crashAfterReplacementForTests = ' +
                $CrashAfterReplacement.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)) `
            -Description 'the replacement crash'
    }
    if ($ThrowAfterReplacement -gt 0) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$throwAfterReplacementForTests = 0' `
            -Enabled ('$throwAfterReplacementForTests = ' +
                $ThrowAfterReplacement.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)) `
            -Description 'the post-replacement exception'
    }
    if ($CrashAfterCleanupDeletion -gt 0) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$crashAfterCleanupDeletionForTests = 0' `
            -Enabled ('$crashAfterCleanupDeletionForTests = ' +
                $CrashAfterCleanupDeletion.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)) `
            -Description 'the cleanup crash'
    }
    if ($HoldPromotionLockMilliseconds -gt 0) {
        $scriptText = Set-PrivateFixtureConstant `
            -ScriptText $scriptText `
            -Disabled '$holdPromotionLockMillisecondsForTests = 0' `
            -Enabled ('$holdPromotionLockMillisecondsForTests = ' +
                $HoldPromotionLockMilliseconds.ToString(
                    [Globalization.CultureInfo]::InvariantCulture)) `
            -Description 'the promotion lock hold'
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
        (Join-Path $sourceRoot '.gitignore'),
        "FilePromptAI-Win7-Full-v*.zip`r`n" +
            "FilePromptAI-Win7-Full-v*.zip.sha256.txt`r`n" +
            "tests/build-artifacts/`r`n" +
            "package-staging/`r`n",
        $utf8NoBom)
    $attributes = "* text=auto`r`n"
    if (-not $WithoutLfs) {
        $attributes += "*.zip filter=lfs diff=lfs merge=lfs -text`r`n"
    }
    $attributes += "*.zip.sha256.txt -text`r`n"
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
        Script = $fixtureScript
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

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
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
    $expectedSidecarText = "$($success.ArchiveHash) *$archiveName`r`n"
    $destinationSidecarText = $strictUtf8.GetString(
        [IO.File]::ReadAllBytes($success.DestinationSidecar))
    if ($destinationSidecarText -cne $expectedSidecarText) {
        throw 'The promoted sidecar did not preserve its canonical CRLF bytes.'
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
        'add', '--',
        "exe/$archiveName",
        "exe/$archiveName.sha256.txt") | Out-Null
    $pointer = Invoke-GitChecked -Root $success.Root -Arguments @(
        'show', ":exe/$archiveName")
    $expectedPointer =
        "version https://git-lfs.github.com/spec/v1`n" +
        "oid sha256:$($success.ArchiveHash.ToLowerInvariant())`n" +
        "size $($success.ArchiveSize)"
    if ($pointer.Replace("`r`n", "`n") -cne $expectedPointer) {
        throw "The promoted ZIP did not stage as the expected Git LFS pointer.`n$pointer"
    }
    $indexSidecarBlob = Invoke-GitChecked `
        -Root $success.Root `
        -Arguments @('rev-parse', ":exe/$archiveName.sha256.txt")
    $workingSidecarBlob = Invoke-GitChecked `
        -Root $success.Root `
        -Arguments @(
            'hash-object',
            '--no-filters',
            '--',
            "exe/$archiveName.sha256.txt")
    if ($indexSidecarBlob -cne $workingSidecarBlob) {
        throw 'Git staging changed the promoted sidecar original CRLF bytes.'
    }
    Assert-ExactDeliveryInventory `
        -Fixture $success `
        -Description 'Exact tested candidate promotion'
    $successTransactionEntries = @(
        Get-PromotionTransactionEntries -Fixture $success)
    if ($successTransactionEntries.Count -ne 0) {
        throw "Successful promotion left transaction entries: $($successTransactionEntries.Name -join ', ')"
    }

    $cleanupFailure = New-PromotionFixture `
        -Name 'post-commit-cleanup' `
        -InjectPostCommitCleanupFailure `
        -StopAfterRecovery
    $cleanupBefore = Get-DeliveryHashes -Fixture $cleanupFailure
    $cleanupResult = Invoke-Promotion -Fixture $cleanupFailure
    Assert-Accepted `
        -Description 'Promotion with post-commit cleanup failure' `
        -Result $cleanupResult
    if ($cleanupResult.Output -notmatch
        'PROMOTION COMMITTED cleanup warning.*temporary cleanup is incomplete') {
        throw "Post-commit cleanup failure did not report an explicit committed-state cleanup warning.`n$($cleanupResult.Output)"
    }
    Assert-ExactDeliveryInventory `
        -Fixture $cleanupFailure `
        -Description 'Post-commit cleanup failure'
    $cleanupAfter = Get-DeliveryHashes -Fixture $cleanupFailure
    foreach ($name in $cleanupBefore.Keys) {
        if ($cleanupAfter[$name] -ceq $cleanupBefore[$name]) {
            throw "Post-commit cleanup failure did not install new delivery bytes: $name"
        }
    }
    if ($cleanupAfter[$archiveName] -cne
            (Get-FileHash -LiteralPath $cleanupFailure.SourceArchive `
                -Algorithm SHA256).Hash -or
        $cleanupAfter["$archiveName.sha256.txt"] -cne
            (Get-FileHash -LiteralPath $cleanupFailure.SourceSidecar `
                -Algorithm SHA256).Hash) {
        throw 'Post-commit cleanup failure did not retain the exact tested archive and sidecar bytes.'
    }
    $cleanupTransactionEntries = @(
        Get-PromotionTransactionEntries -Fixture $cleanupFailure)
    if ($cleanupTransactionEntries.Count -ne 1 -or
        -not $cleanupTransactionEntries[0].PSIsContainer -or
        $cleanupTransactionEntries[0].Name -cnotmatch '^[0-9a-f]{32}$') {
        throw 'Post-commit cleanup failure did not preserve exactly one identifiable transaction directory.'
    }
    $cleanupCommittedSnapshot = Get-DeliverySnapshot `
        -Root $cleanupFailure.DestinationRoot
    $cleanupRecoveryResult = Invoke-Promotion -Fixture $cleanupFailure
    if ($cleanupRecoveryResult.ExitCode -ne 0 -or
        $cleanupRecoveryResult.Output -notmatch
            'PROMOTION RECOVERED COMMITTED' -or
        $cleanupRecoveryResult.Output -notmatch
            'PROMOTION RECOVERY TEST STOP') {
        throw "Post-commit cleanup was not recovered as committed.`n$($cleanupRecoveryResult.Output)"
    }
    Assert-DeliverySnapshotEqual `
        -Before $cleanupCommittedSnapshot `
        -After (Get-DeliverySnapshot -Root $cleanupFailure.DestinationRoot) `
        -Description 'Post-commit cleanup recovery'
    if (@(Get-PromotionTransactionEntries -Fixture $cleanupFailure).Count -ne 0) {
        throw 'Post-commit cleanup recovery left transaction evidence.'
    }

    foreach ($crashStep in @(1, 2, 3, 4)) {
        $crash = New-PromotionFixture `
            -Name "crash-after-$crashStep" `
            -CrashAfterReplacement $crashStep `
            -StopAfterRecovery
        $oldSnapshot = Get-DeliverySnapshot -Root $crash.DestinationRoot
        $crashResult = Invoke-Promotion -Fixture $crash
        if ($crashResult.ExitCode -eq 0) {
            throw "Crash injection after replacement $crashStep did not terminate promotion."
        }
        $crashTransactionEntries = @(
            Get-PromotionTransactionEntries -Fixture $crash)
        if ($crashTransactionEntries.Count -ne 1 -or
            -not (Test-Path -LiteralPath (Join-Path (
                $crashTransactionEntries[0].FullName) 'transaction.xml') -PathType Leaf)) {
            throw "Crash injection after replacement $crashStep did not preserve a journal."
        }
        $recoveryResult = Invoke-Promotion -Fixture $crash
        $expectedRecovery = 'PROMOTION RECOVERED ROLLED BACK'
        if ($recoveryResult.ExitCode -ne 0 -or
            $recoveryResult.Output -notmatch $expectedRecovery -or
            $recoveryResult.Output -notmatch 'PROMOTION RECOVERY TEST STOP') {
            throw "Crash recovery after replacement $crashStep failed.`n$($recoveryResult.Output)"
        }
        Assert-DeliverySnapshotEqual `
            -Before $oldSnapshot `
            -After (Get-DeliverySnapshot -Root $crash.DestinationRoot) `
            -Description "Crash recovery after replacement $crashStep"
        if (@(Get-PromotionTransactionEntries -Fixture $crash).Count -ne 0) {
            throw "Crash recovery after replacement $crashStep left transaction evidence."
        }
        $finalResult = Invoke-Promotion -Fixture $crash
        Assert-Accepted `
            -Description "Promotion after crash recovery step $crashStep" `
            -Result $finalResult
        Assert-ExactDeliveryInventory `
            -Fixture $crash `
            -Description "Promotion after crash recovery step $crashStep"
    }

    foreach ($fallbackName in @('transaction.previous', 'transaction.next')) {
        $fallbackLabel = $fallbackName.Substring('transaction.'.Length)
        $fallback = New-PromotionFixture `
            -Name "fallback-journal-$fallbackLabel" `
            -CrashAfterReplacement 2 `
            -StopAfterRecovery
        $fallbackBefore = Get-DeliverySnapshot -Root $fallback.DestinationRoot
        $fallbackCrash = Invoke-Promotion -Fixture $fallback
        if ($fallbackCrash.ExitCode -eq 0) {
            throw "$fallbackName recovery fixture did not terminate during promotion."
        }
        $fallbackTransactions = @(
            Get-PromotionTransactionEntries -Fixture $fallback)
        if ($fallbackTransactions.Count -ne 1) {
            throw "$fallbackName recovery fixture did not preserve one transaction."
        }
        $fallbackRoot = $fallbackTransactions[0].FullName
        $canonicalJournal = Join-Path $fallbackRoot 'transaction.xml'
        if (-not (Test-Path -LiteralPath $canonicalJournal -PathType Leaf)) {
            throw "$fallbackName recovery fixture is missing its canonical journal."
        }
        Move-Item `
            -LiteralPath $canonicalJournal `
            -Destination (Join-Path $fallbackRoot $fallbackName)

        $fallbackRecovery = Invoke-Promotion -Fixture $fallback
        if ($fallbackRecovery.ExitCode -ne 0 -or
            $fallbackRecovery.Output -notmatch
                'PROMOTION RECOVERED ROLLED BACK' -or
            $fallbackRecovery.Output -notmatch
                'PROMOTION RECOVERY TEST STOP') {
            throw "$fallbackName was not recovered as a durable journal.`n$($fallbackRecovery.Output)"
        }
        Assert-DeliverySnapshotEqual `
            -Before $fallbackBefore `
            -After (Get-DeliverySnapshot -Root $fallback.DestinationRoot) `
            -Description "$fallbackName recovery"
        if (@(Get-PromotionTransactionEntries -Fixture $fallback).Count -ne 0) {
            throw "$fallbackName recovery left transaction evidence."
        }
    }

    $damagedFallback = New-PromotionFixture `
        -Name 'damaged-fallback-journal' `
        -CrashAfterReplacement 2 `
        -StopAfterRecovery
    $damagedFallbackCrash = Invoke-Promotion -Fixture $damagedFallback
    if ($damagedFallbackCrash.ExitCode -eq 0) {
        throw 'Damaged fallback journal fixture did not terminate during promotion.'
    }
    $damagedTransactions = @(
        Get-PromotionTransactionEntries -Fixture $damagedFallback)
    if ($damagedTransactions.Count -ne 1) {
        throw 'Damaged fallback journal fixture did not preserve one transaction.'
    }
    $damagedRoot = $damagedTransactions[0].FullName
    $damagedCanonical = Join-Path $damagedRoot 'transaction.xml'
    $damagedPrevious = Join-Path $damagedRoot 'transaction.previous'
    Move-Item -LiteralPath $damagedCanonical -Destination $damagedPrevious
    [IO.File]::AppendAllText(
        $damagedPrevious,
        'damaged journal bytes',
        $utf8NoBom)
    $damagedDelivery = Get-DeliverySnapshot -Root $damagedFallback.DestinationRoot
    $damagedRecovery = Invoke-Promotion -Fixture $damagedFallback
    Assert-Rejected `
        -Description 'A damaged previous journal' `
        -Result $damagedRecovery `
        -Pattern 'journal|XML|root level|invalid'
    Assert-DeliverySnapshotEqual `
        -Before $damagedDelivery `
        -After (Get-DeliverySnapshot -Root $damagedFallback.DestinationRoot) `
        -Description 'Damaged previous journal rejection'
    $preservedDamagedTransactions = @(
        Get-PromotionTransactionEntries -Fixture $damagedFallback)
    if ($preservedDamagedTransactions.Count -ne 1 -or
        -not (Test-Path -LiteralPath (
            Join-Path $preservedDamagedTransactions[0].FullName 'transaction.xml') `
            -PathType Leaf)) {
        throw 'Damaged previous journal rejection did not preserve recovery evidence.'
    }

    foreach ($cleanupDeletion in @(1, 3)) {
        $cleanupCrash = New-PromotionFixture `
            -Name "cleanup-crash-after-$cleanupDeletion" `
            -CrashAfterCleanupDeletion $cleanupDeletion `
            -StopAfterRecovery
        $cleanupCrashResult = Invoke-Promotion -Fixture $cleanupCrash
        if ($cleanupCrashResult.ExitCode -eq 0) {
            throw "Cleanup crash after deletion $cleanupDeletion did not terminate promotion."
        }
        $committedBeforeRecovery = Get-DeliverySnapshot `
            -Root $cleanupCrash.DestinationRoot
        Assert-ExactDeliveryInventory `
            -Fixture $cleanupCrash `
            -Description "Cleanup crash after deletion $cleanupDeletion"
        $cleanupCrashRecovery = Invoke-Promotion -Fixture $cleanupCrash
        if ($cleanupCrashRecovery.ExitCode -ne 0 -or
            $cleanupCrashRecovery.Output -notmatch
                'PROMOTION RECOVERED COMMITTED' -or
            $cleanupCrashRecovery.Output -notmatch
                'PROMOTION RECOVERY TEST STOP') {
            throw "Committed cleanup recovery after deletion $cleanupDeletion failed.`n$($cleanupCrashRecovery.Output)"
        }
        Assert-DeliverySnapshotEqual `
            -Before $committedBeforeRecovery `
            -After (Get-DeliverySnapshot -Root $cleanupCrash.DestinationRoot) `
            -Description "Committed cleanup recovery after deletion $cleanupDeletion"
        if (@(Get-PromotionTransactionEntries -Fixture $cleanupCrash).Count -ne 0) {
            throw "Committed cleanup recovery after deletion $cleanupDeletion left transaction evidence."
        }
    }

    foreach ($throwStep in @(1, 2, 3, 4)) {
        $thrown = New-PromotionFixture `
            -Name "throw-after-$throwStep" `
            -ThrowAfterReplacement $throwStep
        $throwBefore = Get-DeliverySnapshot -Root $thrown.DestinationRoot
        $throwResult = Invoke-Promotion -Fixture $thrown
        Assert-Rejected `
            -Description "A catchable failure after replacement $throwStep" `
            -Result $throwResult `
            -Pattern "injected failure after replacement $throwStep"
        Assert-DeliverySnapshotEqual `
            -Before $throwBefore `
            -After (Get-DeliverySnapshot -Root $thrown.DestinationRoot) `
            -Description "Catchable failure rollback after replacement $throwStep"
        if (@(Get-PromotionTransactionEntries -Fixture $thrown).Count -ne 0) {
            throw "Catchable failure after replacement $throwStep left transaction evidence."
        }
    }

    $journeyFailure = New-PromotionFixture `
        -Name 'installed-journey-failure' `
        -FailInstalledJourney
    $journeyFailureBefore = Get-DeliverySnapshot `
        -Root $journeyFailure.DestinationRoot
    $journeyFailureResult = Invoke-Promotion -Fixture $journeyFailure
    Assert-Rejected `
        -Description 'A promoted ZIP whose final installed journey fails' `
        -Result $journeyFailureResult `
        -Pattern 'final installed user journey|injected installed journey failure'
    Assert-DeliverySnapshotEqual `
        -Before $journeyFailureBefore `
        -After (Get-DeliverySnapshot -Root $journeyFailure.DestinationRoot) `
        -Description 'Final installed journey failure rollback'
    Assert-ExactDeliveryInventory `
        -Fixture $journeyFailure `
        -Description 'Final installed journey failure rollback'
    if (@(Get-PromotionTransactionEntries -Fixture $journeyFailure).Count -ne 0) {
        throw 'Final installed journey failure left transaction evidence.'
    }

    $locked = New-PromotionFixture -Name 'persistent-lock-conflict'
    $lockedBefore = Get-DeliverySnapshot -Root $locked.DestinationRoot
    $lockPath = Join-Path $locked.SourceRoot (
        'tests\build-artifacts\promotion.lock')
    $lockStream = [IO.File]::Open(
        $lockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $lockConflict = Invoke-Promotion -Fixture $locked
    }
    finally {
        $lockStream.Dispose()
    }
    Assert-Rejected `
        -Description 'A candidate promotion while its persistent lock is held' `
        -Result $lockConflict `
        -Pattern 'Another candidate promotion is already running|promotion lock cannot be acquired'
    Assert-DeliverySnapshotEqual `
        -Before $lockedBefore `
        -After (Get-DeliverySnapshot -Root $locked.DestinationRoot) `
        -Description 'Persistent promotion lock rejection'
    if (@(Get-PromotionTransactionEntries -Fixture $locked).Count -ne 0) {
        throw 'Persistent promotion lock rejection created a transaction.'
    }
    $afterLockRelease = Invoke-Promotion -Fixture $locked
    Assert-Accepted `
        -Description 'Candidate promotion after persistent lock release' `
        -Result $afterLockRelease
    Assert-ExactDeliveryInventory `
        -Fixture $locked `
        -Description 'Candidate promotion after persistent lock release'

    $competing = New-PromotionFixture `
        -Name 'two-process-lock-race' `
        -HoldPromotionLockMilliseconds 2500
    $raceBefore = Get-DeliverySnapshot -Root $competing.DestinationRoot
    $firstPromotion = Start-PromotionProcess -Fixture $competing
    $secondPromotion = $null
    try {
        $raceLockPath = Join-Path $competing.SourceRoot (
            'tests\build-artifacts\promotion.lock')
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $observedHeldLock = $false
        while ([DateTime]::UtcNow -lt $deadline -and
            -not $firstPromotion.Process.HasExited) {
            try {
                $probe = [IO.File]::Open(
                    $raceLockPath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::None)
                $probe.Dispose()
            }
            catch [IO.IOException] {
                $observedHeldLock = $true
                break
            }
            Start-Sleep -Milliseconds 25
        }
        if (-not $observedHeldLock) {
            throw 'The first competing promotion did not acquire its persistent lock in time.'
        }
        $secondPromotion = Start-PromotionProcess -Fixture $competing
        $secondRaceResult = Complete-PromotionProcess `
            -Running $secondPromotion
        $secondPromotion = $null
        $firstRaceResult = Complete-PromotionProcess `
            -Running $firstPromotion
        $firstPromotion = $null
    }
    finally {
        foreach ($running in @($firstPromotion, $secondPromotion)) {
            if ($null -ne $running) {
                try {
                    if (-not $running.Process.HasExited) {
                        $running.Process.Kill()
                        $running.Process.WaitForExit()
                    }
                }
                catch {}
                $running.Process.Dispose()
            }
        }
    }
    $raceResults = @($firstRaceResult, $secondRaceResult)
    $raceWinners = @($raceResults | Where-Object {
        $_.ExitCode -eq 0 -and $_.Output -match '(?m)^PROMOTED \|'
    })
    $raceLosers = @($raceResults | Where-Object {
        $_.ExitCode -ne 0 -and
        $_.Output -match
            'Another candidate promotion is already running|promotion lock cannot be acquired'
    })
    if ($raceWinners.Count -ne 1 -or $raceLosers.Count -ne 1) {
        throw "Two-process promotion competition did not produce exactly one winner and one lock rejection.`nFIRST:`n$($firstRaceResult.Output)`nSECOND:`n$($secondRaceResult.Output)"
    }
    Assert-ExactDeliveryInventory `
        -Fixture $competing `
        -Description 'Two-process promotion competition'
    if (@(Get-PromotionTransactionEntries -Fixture $competing).Count -ne 0) {
        throw 'Two-process promotion competition left transaction evidence.'
    }
    $raceAfter = Get-DeliverySnapshot -Root $competing.DestinationRoot
    if ($raceAfter[$archiveName] -ceq $raceBefore[$archiveName]) {
        throw 'Two-process promotion competition did not install the tested ZIP.'
    }

    $obsolete = New-PromotionFixture -Name 'obsolete-delivery-assets'
    $obsoleteArchiveName = 'FilePromptAI-Win7-Full-v1.16.zip'
    foreach ($obsoleteName in @(
            $obsoleteArchiveName,
            "$obsoleteArchiveName.sha256.txt",
            'ReleaseCandidate-v1.16.txt')) {
        [IO.File]::WriteAllText(
            (Join-Path $obsolete.DestinationRoot $obsoleteName),
            "obsolete delivery asset must be removed in C`r`n",
            $utf8NoBom)
    }
    $obsoleteBefore = Get-DeliverySnapshot -Root $obsolete.DestinationRoot
    $obsoleteResult = Invoke-Promotion -Fixture $obsolete
    Assert-Rejected `
        -Description 'A delivery directory containing obsolete version assets' `
        -Result $obsoleteResult `
        -Pattern 'obsolete or unauthorized entry'
    $obsoleteAfter = Get-DeliverySnapshot -Root $obsolete.DestinationRoot
    Assert-DeliverySnapshotEqual `
        -Before $obsoleteBefore `
        -After $obsoleteAfter `
        -Description 'Obsolete delivery rejection'

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
    Assert-ExactDeliveryInventory `
        -Fixture $rollback `
        -Description 'Interrupted promotion rollback'
    $rollbackTransactionEntries = @(
        Get-PromotionTransactionEntries -Fixture $rollback)
    if ($rollbackTransactionEntries.Count -ne 0) {
        throw "Interrupted promotion left transaction entries: $($rollbackTransactionEntries.Name -join ', ')"
    }

    Write-Host 'PASS | tested candidate promotion topology'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
