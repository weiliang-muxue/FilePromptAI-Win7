param(
    [string]$Version = '1.17'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$sourceSealScript = Join-Path $projectRoot 'seal-release.ps1'
$sourceHashVerifier = Join-Path $testRoot 'VerifyReleaseSha256.ps1'
$sourceTagVerifier = Join-Path $testRoot 'VerifyTaggedRelease.ps1'
$sourceAllSmokeTests = Join-Path $testRoot 'RunAllSmokeTests.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-ReleaseSeal-' + [Guid]::NewGuid().ToString('N'))
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"

function Invoke-GitChecked {
    param(
        [string]$Root,
        [string[]]$GitArguments
    )

    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & git -C $Root @GitArguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "git $($GitArguments -join ' ') failed in $Root.`n$output"
    }
    return $output.Trim()
}

function Invoke-Script {
    param(
        [string]$ScriptPath,
        [string]$Root
    )

    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $arguments = @(
            '-NoLogo',
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            $ScriptPath,
            '-Version',
            $Version
        )
        if ((Split-Path -Leaf $ScriptPath) -eq 'VerifyTaggedRelease.ps1') {
            $arguments += @('-ProjectRoot', $Root)
        }
        if ((Split-Path -Leaf $ScriptPath) -eq 'RunAllSmokeTests.ps1') {
            $arguments += '-WriteReleaseReceipt'
        }
        $output = & powershell.exe @arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Assert-Accepted {
    param(
        [string]$Description,
        [object]$Result,
        [string]$OutputPattern
    )

    if ($Result.ExitCode -ne 0 -or $Result.Output -notmatch $OutputPattern) {
        throw "$Description failed unexpectedly.`n$($Result.Output)"
    }
}

function Assert-Rejected {
    param(
        [string]$Description,
        [object]$Result,
        [string]$OutputPattern = ''
    )

    if ($Result.ExitCode -eq 0) {
        throw "$Description was accepted unexpectedly.`n$($Result.Output)"
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputPattern) -and
        $Result.Output -notmatch $OutputPattern) {
        throw "$Description failed for an unexpected reason.`n$($Result.Output)"
    }
}

function New-ReleaseFixture {
    param(
        [string]$Name,
        [switch]$IncludeTrackedManifest
    )

    $root = Join-Path $temporaryRoot $Name
    $fixtureTestRoot = Join-Path $root 'tests'
    $receiptRoot = Join-Path $fixtureTestRoot 'build-artifacts\release'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null

    Copy-Item -LiteralPath $sourceSealScript -Destination $root
    Copy-Item -LiteralPath $sourceHashVerifier -Destination $fixtureTestRoot
    Copy-Item -LiteralPath $sourceTagVerifier -Destination $fixtureTestRoot
    Copy-Item -LiteralPath $sourceAllSmokeTests -Destination $fixtureTestRoot

    $stubScript =
        "param([string]`$Version = '')`r`n" +
        "`$ErrorActionPreference = 'Stop'`r`n" +
        "Write-Host 'PASS | fixture suite'`r`n"
    $stubNames = @(
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
        'LaunchSmokeTest.ps1',
        'VerifyOfflinePackage.ps1',
        'RunVerifiedPayloadLeaseSmokeTest.ps1',
        'RunAcceptanceVerifierSmokeTest.ps1',
        'RunUninstallerSmokeTest.ps1',
        'RunUninstallerSecuritySmokeTest.ps1'
    )
    foreach ($stubName in $stubNames) {
        [IO.File]::WriteAllText(
            (Join-Path $fixtureTestRoot $stubName),
            $stubScript,
            $utf8NoBom)
    }

    [IO.File]::WriteAllText(
        (Join-Path $root 'build.ps1'),
        $stubScript,
        $utf8NoBom)
    $packageStub = @'
param([string]$Version = '1.17')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$name = "FilePromptAI-Win7-Full-v$Version.zip"
$path = Join-Path $root $name
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllBytes(
    $path,
    [Text.Encoding]::ASCII.GetBytes("suite-generated archive $Version"))
$hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    "$path.sha256.txt",
    "$hash *$name`r`n",
    $utf8NoBom)
Write-Host 'PASS | fixture package build'
'@
    [IO.File]::WriteAllText(
        (Join-Path $root 'build-offline-package.ps1'),
        $packageStub,
        $utf8NoBom)

    [IO.File]::WriteAllText(
        (Join-Path $root '.gitattributes'),
        "* text=auto`r`nRELEASE-SHA256.txt -text`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $root '.gitignore'),
        "FilePromptAI-Win7-Full-v*.zip`r`n" +
        "FilePromptAI-Win7-Full-v*.zip.sha256.txt`r`n" +
        "tests/build-artifacts/`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $root 'candidate.txt'),
        "tested candidate`r`n",
        $utf8NoBom)

    $archivePath = Join-Path $root $archiveName
    $sidecarPath = "$archivePath.sha256.txt"
    [IO.File]::WriteAllBytes(
        $archivePath,
        [Text.Encoding]::ASCII.GetBytes("release fixture $Name"))
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    $checksumLine = "$archiveHash *$archiveName`r`n"
    [IO.File]::WriteAllText($sidecarPath, $checksumLine, $utf8NoBom)
    if ($IncludeTrackedManifest) {
        [IO.File]::WriteAllText(
            (Join-Path $root 'RELEASE-SHA256.txt'),
            $checksumLine,
            $utf8NoBom)
    }

    Invoke-GitChecked -Root $root -GitArguments @('init', '--quiet') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('config', 'user.name', 'Release Test') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('config', 'user.email', 'release-test@example.invalid') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('config', 'core.autocrlf', 'true') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('add', '--', '.') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('commit', '--quiet', '-m', 'candidate') | Out-Null
    $candidateCommit = Invoke-GitChecked -Root $root -GitArguments @('rev-parse', 'HEAD')

    return [pscustomobject]@{
        Root = $root
        SealScript = Join-Path $root 'seal-release.ps1'
        TagVerifier = Join-Path $fixtureTestRoot 'VerifyTaggedRelease.ps1'
        AllSmokeTests = Join-Path $fixtureTestRoot 'RunAllSmokeTests.ps1'
        ArchivePath = $archivePath
        SidecarPath = $sidecarPath
        ReceiptPath = Join-Path $receiptRoot "ReleaseCandidate-v$Version.txt"
        Candidate = $candidateCommit
        ArchiveHash = $archiveHash
    }
}

function Write-ReleaseFixtureReceipt {
    param([object]$Fixture)

    $receiptText =
        "FilePromptAI-Release-Receipt: 1`r`n" +
        "Suite: tests/RunAllSmokeTests.ps1`r`n" +
        "Result: PASS`r`n" +
        "Version: $Version`r`n" +
        "Candidate-Commit: $($Fixture.Candidate)`r`n" +
        "Archive-Name: $archiveName`r`n" +
        "Archive-SHA256: $($Fixture.ArchiveHash)`r`n"
    [IO.File]::WriteAllText(
        $Fixture.ReceiptPath,
        $receiptText,
        $utf8NoBom)
}

function Complete-SealCommit {
    param(
        [object]$Fixture,
        [switch]$ChangeAnotherFile
    )

    if ($ChangeAnotherFile) {
        [IO.File]::AppendAllText(
            (Join-Path $Fixture.Root 'candidate.txt'),
            "unexpected seal change`r`n",
            $utf8NoBom)
    }
    Invoke-GitChecked -Root $Fixture.Root -GitArguments @('add', '--', '.') | Out-Null
    Invoke-GitChecked -Root $Fixture.Root -GitArguments @('commit', '--quiet', '-m', 'seal release') | Out-Null
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $success = New-ReleaseFixture -Name 'success'
    $suiteResult = Invoke-Script `
        -ScriptPath $success.AllSmokeTests `
        -Root $success.Root
    Assert-Accepted `
        -Description 'The full-suite release receipt integration' `
        -Result $suiteResult `
        -OutputPattern '(?m)^RECEIPT \|'
    $sealResult = Invoke-Script -ScriptPath $success.SealScript -Root $success.Root
    Assert-Accepted `
        -Description 'The tested candidate seal' `
        -Result $sealResult `
        -OutputPattern '(?m)^SEALED \|'
    Complete-SealCommit -Fixture $success
    Invoke-GitChecked `
        -Root $success.Root `
        -GitArguments @('tag', '-a', "v$Version", '-m', "release v$Version") | Out-Null
    $tagResult = Invoke-Script `
        -ScriptPath $success.TagVerifier `
        -Root $success.Root
    Assert-Accepted `
        -Description 'The annotated manifest-only release tag' `
        -Result $tagResult `
        -OutputPattern '(?m)^PASS \| annotated release tag \|'

    $manifestPath = Join-Path $success.Root 'RELEASE-SHA256.txt'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($manifestBytes.Length -lt 2 -or
        $manifestBytes[$manifestBytes.Length - 2] -ne 0x0D -or
        $manifestBytes[$manifestBytes.Length - 1] -ne 0x0A) {
        throw 'The sealed working manifest is not CRLF-terminated.'
    }
    $rawWorkingBlob = Invoke-GitChecked `
        -Root $success.Root `
        -GitArguments @('hash-object', '--no-filters', '--', 'RELEASE-SHA256.txt')
    $tagBlob = Invoke-GitChecked `
        -Root $success.Root `
        -GitArguments @('rev-parse', "v$Version`:RELEASE-SHA256.txt")
    if ($rawWorkingBlob -ne $tagBlob) {
        throw 'core.autocrlf=true changed the tagged release manifest bytes.'
    }

    $staged = New-ReleaseFixture -Name 'staged-old-digest'
    Write-ReleaseFixtureReceipt -Fixture $staged
    [IO.File]::WriteAllText(
        (Join-Path $staged.Root 'RELEASE-SHA256.txt'),
        (('0' * 64) + " *$archiveName`r`n"),
        $utf8NoBom)
    Invoke-GitChecked `
        -Root $staged.Root `
        -GitArguments @('add', '--', 'RELEASE-SHA256.txt') | Out-Null
    Assert-Rejected `
        -Description 'A staged old release digest' `
        -Result (Invoke-Script -ScriptPath $staged.SealScript -Root $staged.Root) `
        -OutputPattern 'empty Git index'

    $staleReceipt = New-ReleaseFixture -Name 'stale-receipt'
    Write-ReleaseFixtureReceipt -Fixture $staleReceipt
    [IO.File]::AppendAllText(
        (Join-Path $staleReceipt.Root 'candidate.txt'),
        "new candidate`r`n",
        $utf8NoBom)
    Invoke-GitChecked -Root $staleReceipt.Root -GitArguments @('add', '--', 'candidate.txt') | Out-Null
    Invoke-GitChecked -Root $staleReceipt.Root -GitArguments @('commit', '--quiet', '-m', 'new candidate') | Out-Null
    Assert-Rejected `
        -Description 'A receipt for an older HEAD' `
        -Result (Invoke-Script -ScriptPath $staleReceipt.SealScript -Root $staleReceipt.Root) `
        -OutputPattern 'receipt does not match HEAD'

    $changedZip = New-ReleaseFixture -Name 'changed-zip'
    Write-ReleaseFixtureReceipt -Fixture $changedZip
    [IO.File]::AppendAllText($changedZip.ArchivePath, 'changed', $utf8NoBom)
    $changedHash = (Get-FileHash -LiteralPath $changedZip.ArchivePath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        $changedZip.SidecarPath,
        "$changedHash *$archiveName`r`n",
        $utf8NoBom)
    Assert-Rejected `
        -Description 'A changed self-consistent ZIP and sidecar' `
        -Result (Invoke-Script -ScriptPath $changedZip.SealScript -Root $changedZip.Root) `
        -OutputPattern 'no longer matches.*receipt'

    $lightweight = New-ReleaseFixture -Name 'lightweight-tag'
    Write-ReleaseFixtureReceipt -Fixture $lightweight
    Assert-Accepted `
        -Description 'The lightweight-tag fixture seal' `
        -Result (Invoke-Script -ScriptPath $lightweight.SealScript -Root $lightweight.Root) `
        -OutputPattern '(?m)^SEALED \|'
    Complete-SealCommit -Fixture $lightweight
    Invoke-GitChecked -Root $lightweight.Root -GitArguments @('tag', "v$Version") | Out-Null
    Assert-Rejected `
        -Description 'A lightweight release tag' `
        -Result (Invoke-Script -ScriptPath $lightweight.TagVerifier -Root $lightweight.Root) `
        -OutputPattern 'annotated tag'

    $extraChange = New-ReleaseFixture -Name 'extra-seal-change'
    Write-ReleaseFixtureReceipt -Fixture $extraChange
    Assert-Accepted `
        -Description 'The extra-change fixture seal' `
        -Result (Invoke-Script -ScriptPath $extraChange.SealScript -Root $extraChange.Root) `
        -OutputPattern '(?m)^SEALED \|'
    Complete-SealCommit -Fixture $extraChange -ChangeAnotherFile
    Invoke-GitChecked `
        -Root $extraChange.Root `
        -GitArguments @('tag', '-a', "v$Version", '-m', 'invalid release') | Out-Null
    Assert-Rejected `
        -Description 'A seal commit that changes another file' `
        -Result (Invoke-Script -ScriptPath $extraChange.TagVerifier -Root $extraChange.Root) `
        -OutputPattern 'change exactly RELEASE-SHA256.txt'

    $missingDigest = New-ReleaseFixture -Name 'missing-digest' -IncludeTrackedManifest
    Write-ReleaseFixtureReceipt -Fixture $missingDigest
    Remove-Item -LiteralPath (Join-Path $missingDigest.Root 'RELEASE-SHA256.txt') -Force
    Invoke-GitChecked `
        -Root $missingDigest.Root `
        -GitArguments @('add', '--update', '--', 'RELEASE-SHA256.txt') | Out-Null
    Invoke-GitChecked `
        -Root $missingDigest.Root `
        -GitArguments @('commit', '--quiet', '-m', 'remove digest') | Out-Null
    Invoke-GitChecked `
        -Root $missingDigest.Root `
        -GitArguments @('tag', '-a', "v$Version", '-m', 'missing digest') | Out-Null
    Assert-Rejected `
        -Description 'A release tag missing the digest' `
        -Result (Invoke-Script -ScriptPath $missingDigest.TagVerifier -Root $missingDigest.Root) `
        -OutputPattern 'missing from the release tag'
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporary.StartsWith(
        $resolvedSystemTemp,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary).StartsWith(
            'FilePromptAI-ReleaseSeal-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

Write-Host 'PASS | release candidate receipt, sealing, and annotated-tag gate tests'
