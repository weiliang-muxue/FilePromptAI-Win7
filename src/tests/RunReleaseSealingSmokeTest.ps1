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
$sourceReleaseEvidence = Join-Path $testRoot 'ReleaseAcceptanceEvidence.ps1'
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
        [string]$Root,
        [switch]$OmitAcceptanceReport
    )

    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $arguments = @(
            '-NoLogo',
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            $ScriptPath,
            '-Version',
            $Version
        )
        if ((Split-Path -Leaf $ScriptPath) -eq 'VerifyTaggedRelease.ps1') {
            $arguments += @('-ProjectRoot', (Join-Path $Root 'src'))
        }
        if (-not $OmitAcceptanceReport -and
            (Split-Path -Leaf $ScriptPath) -in @(
                'seal-release.ps1',
                'VerifyTaggedRelease.ps1')) {
            $arguments += @(
                '-AcceptanceReportPath',
                (Join-Path $temporaryRoot (
                    'acceptance-' + (Split-Path -Leaf $Root) + '.xml')))
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

function Write-AcceptanceFixtureReport {
    param(
        [string]$Path,
        [string]$ArchiveHash,
        [int64]$ArchiveSize,
        [string]$ManifestHash,
        [int]$ManifestEntryCount
    )

    $requiredChecks = @(
        'os.win7-sp1',
        'runtime.dotnet-4.8',
        'display.fullhd-100-percent',
        'archive.identity',
        'package.checksums',
        'files.extract',
        'files.export',
        'api.models',
        'api.chat-completions',
        'application.launch',
        'application.ui-journey',
        'application.cleanup'
    )
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = $utf8NoBom
    $settings.Indent = $true
    $settings.NewLineChars = "`r`n"
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('filePromptAiAcceptance')
        $writer.WriteAttributeString('schemaVersion', '3')
        $writer.WriteAttributeString('result', 'pass')
        $writer.WriteAttributeString('exitCode', '0')
        $writer.WriteAttributeString(
            'createdUtc',
            [DateTime]::UtcNow.ToString(
                'o',
                [Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteAttributeString('verifierVersion', '1.17.0.0')
        foreach ($name in @(
            'packageRoot',
            'reportPath',
            'isolatedDataRoot',
            'is64BitOperatingSystem',
            'clrVersion')) {
            $writer.WriteElementString($name, 'fixture')
        }
        $writer.WriteStartElement('packageIdentity')
        $writer.WriteAttributeString('status', 'verified')
        $writer.WriteAttributeString('archiveName', $archiveName)
        $writer.WriteAttributeString('archiveSha256', $ArchiveHash)
        $writer.WriteAttributeString(
            'archiveSize',
            $ArchiveSize.ToString(
                [Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteAttributeString(
            'manifestName',
            'PACKAGE-CHECKSUMS-SHA256.txt')
        $writer.WriteAttributeString('manifestSha256', $ManifestHash)
        $writer.WriteAttributeString(
            'manifestEntryCount',
            $ManifestEntryCount.ToString(
                [Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteEndElement()
        $writer.WriteStartElement('checks')
        foreach ($identifier in $requiredChecks) {
            $writer.WriteStartElement('check')
            $writer.WriteAttributeString('id', $identifier)
            $writer.WriteAttributeString('status', 'pass')
            $writer.WriteAttributeString('durationMs', '1')
            $writer.WriteElementString('message', 'fixture pass')
            $writer.WriteElementString('evidence', 'fixture evidence')
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        "$Path.sha256.txt",
        "$hash *$(Split-Path -Leaf $Path)`r`n",
        $utf8NoBom)
}

function Update-AcceptanceFixtureSidecar {
    param([string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        "$Path.sha256.txt",
        "$hash *$(Split-Path -Leaf $Path)`r`n",
        $utf8NoBom)
}

function Set-AcceptanceFixtureText {
    param(
        [string]$Path,
        [string]$OriginalText,
        [string]$OldValue,
        [string]$NewValue
    )

    $changed = $OriginalText.Replace($OldValue, $NewValue)
    if ([string]::Equals(
        $changed,
        $OriginalText,
        [StringComparison]::Ordinal)) {
        throw "The acceptance fixture mutation did not find: $OldValue"
    }
    [IO.File]::WriteAllText($Path, $changed, $utf8NoBom)
    Update-AcceptanceFixtureSidecar -Path $Path
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
    $sourceRoot = Join-Path $root 'src'
    $distributionRoot = Join-Path $root 'exe'
    $fixtureTestRoot = Join-Path $sourceRoot 'tests'
    $receiptRoot = Join-Path $fixtureTestRoot 'build-artifacts\release'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $distributionRoot -Force | Out-Null

    Copy-Item -LiteralPath $sourceSealScript -Destination $sourceRoot
    Copy-Item -LiteralPath $sourceHashVerifier -Destination $fixtureTestRoot
    Copy-Item -LiteralPath $sourceTagVerifier -Destination $fixtureTestRoot
    Copy-Item -LiteralPath $sourceAllSmokeTests -Destination $fixtureTestRoot
    Copy-Item -LiteralPath $sourceReleaseEvidence -Destination $fixtureTestRoot

    $stubScript =
        "param([string]`$Version = '')`r`n" +
        "`$ErrorActionPreference = 'Stop'`r`n" +
        "Write-Host 'PASS | fixture suite'`r`n"
    $stubNames = @(
        'RunReleaseSha256SmokeTest.ps1',
        'RunCandidatePromotionSmokeTest.ps1',
        'RunReleaseSealingSmokeTest.ps1',
        'RunDefenderScanGateSmokeTest.ps1',
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
        'RunInstalledUserJourneySmokeTest.ps1',
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
        (Join-Path $sourceRoot 'build.ps1'),
        $stubScript,
        $utf8NoBom)
    $packageStub = @'
param([string]$Version = '1.17')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$name = "FilePromptAI-Win7-Full-v$Version.zip"
$path = Join-Path $root $name
$staging = Join-Path $root "FilePromptAI-offline-release-v$Version"
$utf8NoBom = New-Object Text.UTF8Encoding($false)
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging | Out-Null
$payloadPath = Join-Path $staging 'payload.txt'
[IO.File]::WriteAllText($payloadPath, "fixture payload $Version`r`n", $utf8NoBom)
$payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    (Join-Path $staging 'PACKAGE-CHECKSUMS-SHA256.txt'),
    "$payloadHash *payload.txt`r`n",
    $utf8NoBom)
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $path -Force
Write-Host 'PASS | fixture package build'
'@
    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot 'build-offline-package.ps1'),
        $packageStub,
        $utf8NoBom)

    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot '.gitattributes'),
        "* text=auto`r`nRELEASE-SHA256.txt -text`r`nRELEASE-EVIDENCE.txt -text`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot '.gitignore'),
        "FilePromptAI-Win7-Full-v*.zip`r`n" +
            "tests/build-artifacts/`r`n" +
            "FilePromptAI-offline-release-v*/`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $root '.gitattributes'),
        "exe/*.zip filter=lfs diff=lfs merge=lfs -text`r`n",
        $utf8NoBom)
    [IO.File]::WriteAllText(
        (Join-Path $sourceRoot 'candidate.txt'),
        "tested candidate`r`n",
        $utf8NoBom)
    if ($IncludeTrackedManifest) {
        [IO.File]::WriteAllText(
            (Join-Path $sourceRoot 'RELEASE-SHA256.txt'),
            "fixture pre-existing digest`r`n",
            $utf8NoBom)
    }

    Invoke-GitChecked -Root $root -GitArguments @('init', '--quiet') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('config', 'user.name', 'Release Test') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('config', 'user.email', 'release-test@example.invalid') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('config', 'core.autocrlf', 'true') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('lfs', 'install', '--local') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('add', '--', '.') | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @('commit', '--quiet', '-m', 'tested source candidate') | Out-Null
    $candidateCommit = Invoke-GitChecked -Root $root -GitArguments @('rev-parse', 'HEAD')

    $suiteResult = Invoke-Script `
        -ScriptPath (Join-Path $fixtureTestRoot 'RunAllSmokeTests.ps1') `
        -Root $root
    Assert-Accepted `
        -Description 'The full-suite release receipt integration' `
        -Result $suiteResult `
        -OutputPattern '(?m)^RECEIPT \|'

    $sourceArchivePath = Join-Path $sourceRoot $archiveName
    $fixtureManifestPath = Join-Path `
        (Join-Path $sourceRoot "FilePromptAI-offline-release-v$Version") `
        'PACKAGE-CHECKSUMS-SHA256.txt'
    $archiveHash = (Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash
    $archiveSize = (Get-Item -LiteralPath $sourceArchivePath).Length
    $manifestHash = (Get-FileHash -LiteralPath $fixtureManifestPath -Algorithm SHA256).Hash
    $manifestEntryCount = 1

    $archivePath = Join-Path $distributionRoot $archiveName
    Copy-Item -LiteralPath $sourceArchivePath -Destination $archivePath

    $receiptPath = Join-Path $receiptRoot "ReleaseCandidate-v$Version.txt"

    Invoke-GitChecked -Root $root -GitArguments @(
        'add', '--',
        "exe/$archiveName") | Out-Null
    Invoke-GitChecked -Root $root -GitArguments @(
        'commit', '--quiet', '-m', 'promote tested candidate') | Out-Null
    $promotionCommit = Invoke-GitChecked -Root $root -GitArguments @('rev-parse', 'HEAD')

    $acceptanceReportPath = Join-Path $temporaryRoot "acceptance-$Name.xml"
    Write-AcceptanceFixtureReport `
        -Path $acceptanceReportPath `
        -ArchiveHash $archiveHash `
        -ArchiveSize $archiveSize `
        -ManifestHash $manifestHash `
        -ManifestEntryCount $manifestEntryCount

    return [pscustomobject]@{
        Root = $root
        SourceRoot = $sourceRoot
        SealScript = Join-Path $sourceRoot 'seal-release.ps1'
        TagVerifier = Join-Path $fixtureTestRoot 'VerifyTaggedRelease.ps1'
        AllSmokeTests = Join-Path $fixtureTestRoot 'RunAllSmokeTests.ps1'
        ArchivePath = $archivePath
        ReceiptPath = $receiptPath
        Candidate = $candidateCommit
        Promotion = $promotionCommit
        ArchiveHash = $archiveHash
        ArchiveSize = $archiveSize
        ManifestHash = $manifestHash
        ManifestEntryCount = $manifestEntryCount
        AcceptanceReportPath = $acceptanceReportPath
        SuiteResult = $suiteResult
    }
}

function Write-ReleaseFixtureReceipt {
    param([object]$Fixture)

    $receiptText =
        "FilePromptAI-Release-Receipt: 2`r`n" +
        "Suite: tests/RunAllSmokeTests.ps1`r`n" +
        "Result: PASS`r`n" +
        "Version: $Version`r`n" +
        "Candidate-Commit: $($Fixture.Candidate)`r`n" +
        "Archive-Name: $archiveName`r`n" +
        "Archive-SHA256: $($Fixture.ArchiveHash)`r`n" +
        "Package-Manifest-Name: PACKAGE-CHECKSUMS-SHA256.txt`r`n" +
        "Package-Manifest-SHA256: $($Fixture.ManifestHash)`r`n" +
        "Package-Manifest-Entry-Count: $($Fixture.ManifestEntryCount)`r`n"
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
            (Join-Path $Fixture.SourceRoot 'candidate.txt'),
            "unexpected seal change`r`n",
            $utf8NoBom)
    }
    Invoke-GitChecked -Root $Fixture.Root -GitArguments @(
        'add', '--',
        'src/RELEASE-SHA256.txt',
        'src/RELEASE-EVIDENCE.txt') | Out-Null
    if ($ChangeAnotherFile) {
        Invoke-GitChecked -Root $Fixture.Root -GitArguments @(
            'add', '--', 'src/candidate.txt') | Out-Null
    }
    Invoke-GitChecked -Root $Fixture.Root -GitArguments @('commit', '--quiet', '-m', 'seal release') | Out-Null
}

New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $success = New-ReleaseFixture -Name 'success'
    Assert-Accepted `
        -Description 'The full-suite release receipt integration' `
        -Result $success.SuiteResult `
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
    $successAcceptanceText = [IO.File]::ReadAllText(
        $success.AcceptanceReportPath,
        (New-Object Text.UTF8Encoding($false, $true)))
    Set-AcceptanceFixtureText `
        -Path $success.AcceptanceReportPath `
        -OriginalText $successAcceptanceText `
        -OldValue 'id="application.cleanup" status="pass"' `
        -NewValue 'id="application.cleanup" status="fail"'
    Assert-Rejected `
        -Description 'A tagged release with a subsequently failed acceptance report' `
        -Result (Invoke-Script `
            -ScriptPath $success.TagVerifier `
            -Root $success.Root) `
        -OutputPattern 'check.*failed'
    [IO.File]::WriteAllText(
        $success.AcceptanceReportPath,
        $successAcceptanceText,
        $utf8NoBom)
    Update-AcceptanceFixtureSidecar -Path $success.AcceptanceReportPath

    $manifestPath = Join-Path $success.SourceRoot 'RELEASE-SHA256.txt'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($manifestBytes.Length -lt 2 -or
        $manifestBytes[$manifestBytes.Length - 2] -ne 0x0D -or
        $manifestBytes[$manifestBytes.Length - 1] -ne 0x0A) {
        throw 'The sealed working manifest is not CRLF-terminated.'
    }
    $rawWorkingBlob = Invoke-GitChecked `
        -Root $success.Root `
        -GitArguments @('hash-object', '--no-filters', '--', 'src/RELEASE-SHA256.txt')
    $tagBlob = Invoke-GitChecked `
        -Root $success.Root `
        -GitArguments @('rev-parse', "v$Version`:src/RELEASE-SHA256.txt")
    if ($rawWorkingBlob -ne $tagBlob) {
        throw 'core.autocrlf=true changed the tagged release manifest bytes.'
    }

    $invalidEvidence = New-ReleaseFixture -Name 'invalid-acceptance'
    Write-ReleaseFixtureReceipt -Fixture $invalidEvidence
    $acceptanceText = [IO.File]::ReadAllText(
        $invalidEvidence.AcceptanceReportPath,
        (New-Object Text.UTF8Encoding($false, $true)))

    Assert-Rejected `
        -Description 'A seal invocation without the mandatory acceptance report' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root `
            -OmitAcceptanceReport) `
        -OutputPattern 'AcceptanceReportPath|mandatory parameter'

    Move-Item `
        -LiteralPath $invalidEvidence.AcceptanceReportPath `
        -Destination "$($invalidEvidence.AcceptanceReportPath).missing"
    try {
        Assert-Rejected `
            -Description 'A missing Windows 7 acceptance report' `
            -Result (Invoke-Script `
                -ScriptPath $invalidEvidence.SealScript `
                -Root $invalidEvidence.Root) `
            -OutputPattern 'acceptance XML report.*missing'
    }
    finally {
        Move-Item `
            -LiteralPath "$($invalidEvidence.AcceptanceReportPath).missing" `
            -Destination $invalidEvidence.AcceptanceReportPath
    }

    [IO.File]::WriteAllText(
        "$($invalidEvidence.AcceptanceReportPath).sha256.txt",
        (('0' * 64) + " *$(Split-Path -Leaf $invalidEvidence.AcceptanceReportPath)`r`n"),
        $utf8NoBom)
    Assert-Rejected `
        -Description 'A tampered Windows 7 acceptance sidecar' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'sidecar.*does not match'
    Update-AcceptanceFixtureSidecar `
        -Path $invalidEvidence.AcceptanceReportPath

    Set-AcceptanceFixtureText `
        -Path $invalidEvidence.AcceptanceReportPath `
        -OriginalText $acceptanceText `
        -OldValue 'result="pass" exitCode="0"' `
        -NewValue 'result="fail" exitCode="1"'
    Assert-Rejected `
        -Description 'A failed acceptance result with a fresh sidecar' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'not a passing v1\.17'

    Set-AcceptanceFixtureText `
        -Path $invalidEvidence.AcceptanceReportPath `
        -OriginalText $acceptanceText `
        -OldValue 'verifierVersion="1.17.0.0"' `
        -NewValue 'verifierVersion="1.16.0.0"'
    Assert-Rejected `
        -Description 'An acceptance report from an older verifier' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'not a passing v1\.17'

    Set-AcceptanceFixtureText `
        -Path $invalidEvidence.AcceptanceReportPath `
        -OriginalText $acceptanceText `
        -OldValue 'schemaVersion="3"' `
        -NewValue 'schemaVersion="2"'
    Assert-Rejected `
        -Description 'A legacy schema 2 acceptance report' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'not a passing v1\.17'

    Set-AcceptanceFixtureText `
        -Path $invalidEvidence.AcceptanceReportPath `
        -OriginalText $acceptanceText `
        -OldValue 'id="application.ui-journey" status="pass"' `
        -NewValue 'id="application.launch" status="pass"'
    Assert-Rejected `
        -Description 'An acceptance report missing the full UI journey' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'duplicated|missing required'

    Set-AcceptanceFixtureText `
        -Path $invalidEvidence.AcceptanceReportPath `
        -OriginalText $acceptanceText `
        -OldValue 'id="application.cleanup" status="pass"' `
        -NewValue 'id="application.cleanup" status="fail"'
    Assert-Rejected `
        -Description 'A failed required acceptance check' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'check.*failed'

    Set-AcceptanceFixtureText `
        -Path $invalidEvidence.AcceptanceReportPath `
        -OriginalText $acceptanceText `
        -OldValue $invalidEvidence.ManifestHash `
        -NewValue ('A' * 64)
    Assert-Rejected `
        -Description 'A passing report for a different package manifest' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'package identity does not match'

    $dtdText = $acceptanceText.Replace(
        '<filePromptAiAcceptance ',
        "<!DOCTYPE filePromptAiAcceptance [<!ENTITY xxe SYSTEM 'file:///C:/Windows/win.ini'>]>`r`n<filePromptAiAcceptance ")
    [IO.File]::WriteAllText(
        $invalidEvidence.AcceptanceReportPath,
        $dtdText,
        $utf8NoBom)
    Update-AcceptanceFixtureSidecar `
        -Path $invalidEvidence.AcceptanceReportPath
    Assert-Rejected `
        -Description 'An acceptance XML report with an external entity DTD' `
        -Result (Invoke-Script `
            -ScriptPath $invalidEvidence.SealScript `
            -Root $invalidEvidence.Root) `
        -OutputPattern 'unsafe or invalid|DTD'

    $staged = New-ReleaseFixture -Name 'staged-old-digest'
    Write-ReleaseFixtureReceipt -Fixture $staged
    [IO.File]::WriteAllText(
        (Join-Path $staged.SourceRoot 'RELEASE-SHA256.txt'),
        (('0' * 64) + " *$archiveName`r`n"),
        $utf8NoBom)
    Invoke-GitChecked `
        -Root $staged.Root `
        -GitArguments @('add', '--', 'src/RELEASE-SHA256.txt') | Out-Null
    Assert-Rejected `
        -Description 'A staged old release digest' `
        -Result (Invoke-Script -ScriptPath $staged.SealScript -Root $staged.Root) `
        -OutputPattern 'empty Git index'

    $staleReceipt = New-ReleaseFixture -Name 'stale-receipt'
    Write-ReleaseFixtureReceipt -Fixture $staleReceipt
    [IO.File]::AppendAllText(
        (Join-Path $staleReceipt.SourceRoot 'candidate.txt'),
        "new candidate`r`n",
        $utf8NoBom)
    Invoke-GitChecked -Root $staleReceipt.Root -GitArguments @('add', '--', 'src/candidate.txt') | Out-Null
    Invoke-GitChecked -Root $staleReceipt.Root -GitArguments @('commit', '--quiet', '-m', 'new candidate') | Out-Null
    Assert-Rejected `
        -Description 'A commit added after the promotion commit' `
        -Result (Invoke-Script -ScriptPath $staleReceipt.SealScript -Root $staleReceipt.Root) `
        -OutputPattern 'promotion commit parent is not the tested source candidate'

    $changedZip = New-ReleaseFixture -Name 'changed-zip'
    Write-ReleaseFixtureReceipt -Fixture $changedZip
    [IO.File]::AppendAllText($changedZip.ArchivePath, 'changed', $utf8NoBom)
    Assert-Rejected `
        -Description 'A changed ZIP' `
        -Result (Invoke-Script -ScriptPath $changedZip.SealScript -Root $changedZip.Root) `
        -OutputPattern 'clean promotion commit'

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
    Remove-Item -LiteralPath (Join-Path $missingDigest.SourceRoot 'RELEASE-SHA256.txt') -Force
    Invoke-GitChecked `
        -Root $missingDigest.Root `
        -GitArguments @('add', '--update', '--', 'src/RELEASE-SHA256.txt') | Out-Null
    Invoke-GitChecked `
        -Root $missingDigest.Root `
        -GitArguments @('commit', '--quiet', '-m', 'remove digest') | Out-Null
    Invoke-GitChecked `
        -Root $missingDigest.Root `
        -GitArguments @('tag', '-a', "v$Version", '-m', 'missing digest') | Out-Null
    Assert-Rejected `
        -Description 'A release tag missing the digest' `
        -Result (Invoke-Script -ScriptPath $missingDigest.TagVerifier -Root $missingDigest.Root) `
        -OutputPattern 'change exactly RELEASE-SHA256.txt and RELEASE-EVIDENCE.txt'
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
