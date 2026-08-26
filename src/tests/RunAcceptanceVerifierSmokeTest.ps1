param(
    [string]$Version = '1.17'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class FilePromptAIAcceptanceHostOs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public int Size;
        public int MajorVersion;
        public int MinorVersion;
        public int BuildNumber;
        public int PlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
        public short ServicePackMajor;
        public short ServicePackMinor;
        public short SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX value);

    public static bool IsWindows7Sp1Workstation()
    {
        OSVERSIONINFOEX value = new OSVERSIONINFOEX();
        value.Size = Marshal.SizeOf(typeof(OSVERSIONINFOEX));
        if (RtlGetVersion(ref value) != 0)
        {
            throw new InvalidOperationException("RtlGetVersion failed.");
        }

        return value.MajorVersion == 6 &&
            value.MinorVersion == 1 &&
            value.ServicePackMajor >= 1 &&
            value.ProductType == 1;
    }
}
'@

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$verifierPath = Join-Path $stagingRoot 'Verify-FilePromptAI.exe'
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$archivePath = Join-Path $projectRoot $archiveName
$isWindows7Sp1 = [FilePromptAIAcceptanceHostOs]::IsWindows7Sp1Workstation()

function Assert-ExpectedHostOutcome {
    param(
        [int]$ExitCode,
        [string]$Output,
        [string]$Context
    )

    if ($isWindows7Sp1) {
        if ($ExitCode -ne 0 -or
            $Output -notmatch '(?m)^PASS \| os\.win7-sp1 \|' -or
            $Output -notmatch '(?m)^PASS \| win7 acceptance \| exitCode=0$') {
            throw "$Context did not receive a complete Windows 7 SP1 acceptance PASS.`n$Output"
        }
        return
    }

    if ($ExitCode -eq 0) {
        throw "$Context produced a Win7 acceptance PASS on a non-Windows-7 host."
    }
    if (($ExitCode -band 1) -ne 1) {
        throw "$Context did not set the Windows 7 OS failure bit; exitCode=$ExitCode."
    }
    if ($Output -notmatch '(?m)^FAIL \| os\.win7-sp1 \|') {
        throw "$Context did not explicitly fail the Windows 7 SP1 gate."
    }
    if ($Output -match '(?m)^PASS \| win7 acceptance \|') {
        throw "$Context printed an invalid overall Win7 PASS."
    }
}

foreach ($required in @($verifierPath, $archivePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Acceptance verifier input was not found: $required"
    }
}

$beforeReports = @(
    Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) `
        -Filter 'FilePromptAI-Acceptance-*.xml' `
        -File |
        ForEach-Object { $_.FullName }
)

$acceptanceFallbackRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
) 'FilePromptAI-Acceptance\AcceptanceReports'
$acceptanceFallbackDataRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
) 'FilePromptAI-Acceptance\AcceptanceData'
$beforeFallbackReports = @()
if (Test-Path -LiteralPath $acceptanceFallbackRoot -PathType Container) {
    $beforeFallbackReports = @(
        Get-ChildItem -LiteralPath $acceptanceFallbackRoot `
            -Filter 'FilePromptAI-Acceptance-*.xml' `
            -File |
            ForEach-Object { $_.FullName }
    )
}

$output = & $verifierPath --archive $archivePath 2>&1 | Out-String
$exitCode = $LASTEXITCODE
Write-Host $output.TrimEnd()

Assert-ExpectedHostOutcome `
    -ExitCode $exitCode `
    -Output $output `
    -Context 'The primary verifier run'
foreach ($requiredPass in @(
    'runtime.dotnet-4.8',
    'archive.identity',
    'package.checksums',
    'files.extract',
    'files.export',
    'api.models',
    'api.chat-completions',
    'application.launch',
    'application.ui-journey',
    'application.cleanup'
)) {
    if ($output -notmatch "(?m)^PASS \| $([regex]::Escape($requiredPass)) \|") {
        throw "The verifier did not pass its $requiredPass check on the build host."
    }
}
$afterReports = @(
    Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) `
        -Filter 'FilePromptAI-Acceptance-*.xml' `
        -File |
        Where-Object { $beforeReports -notcontains $_.FullName } |
        Sort-Object LastWriteTimeUtc -Descending
)
if ($afterReports.Count -ne 1) {
    throw "Expected exactly one new acceptance XML report, found $($afterReports.Count)."
}

$report = $afterReports[0]
$checksumPath = "$($report.FullName).sha256.txt"
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Acceptance report checksum was not created: $checksumPath"
}
[xml]$document = Get-Content -LiteralPath $report.FullName -Raw -Encoding UTF8
$root = $document.filePromptAiAcceptance
if ($root.schemaVersion -ne '3' -or
    $root.verifierVersion -ne '1.17.0.0') {
    throw 'The acceptance report does not use the v1.17 schemaVersion=3 contract.'
}

function Assert-FailedReportHasNoVerifiedIdentity {
    param(
        [string]$Output,
        [string]$Context
    )

    $match = [Text.RegularExpressions.Regex]::Match(
        $Output,
        '(?m)^REPORT \| (?<Path>.+?)\r?$')
    if (-not $match.Success) {
        throw "$Context did not print an acceptance report path."
    }
    $path = $match.Groups['Path'].Value.Trim()
    $sidecar = "$path.sha256.txt"
    try {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            -not (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
            throw "$Context did not create its report and sidecar."
        }
        [xml]$failedDocument = Get-Content `
            -LiteralPath $path `
            -Raw `
            -Encoding UTF8
        $failedRoot = $failedDocument.filePromptAiAcceptance
        $failedIdentity = $failedRoot.packageIdentity
        if ($failedRoot.schemaVersion -ne '3' -or
            $failedRoot.result -ne 'fail' -or
            $failedIdentity.status -ne 'unverified' -or
            $failedIdentity.Attributes.Count -ne 1 -or
            $failedIdentity.HasAttribute('archiveName') -or
            $failedIdentity.HasAttribute('archiveSha256') -or
            $failedIdentity.HasAttribute('archiveSize') -or
            $failedIdentity.HasAttribute('manifestSha256') -or
            $failedIdentity.HasAttribute('manifestEntryCount') -or
            $failedIdentity.HasAttribute('manifestName')) {
            throw "$Context produced failure evidence that could be mistaken for a verified package identity."
        }
        $recorded = (
            Get-Content -LiteralPath $sidecar -Raw -Encoding UTF8
        ).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($recorded -cne $actual) {
            throw "$Context report sidecar does not match its failure XML."
        }
    }
    finally {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force
        }
        if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
            Remove-Item -LiteralPath $sidecar -Force
        }
    }
}

$missingArchiveOutput = & $verifierPath 2>&1 | Out-String
$missingArchiveExitCode = $LASTEXITCODE
if (($missingArchiveExitCode -band 128) -ne 128 -or
    $missingArchiveOutput -notmatch 'Usage: Verify-FilePromptAI\.exe --archive') {
    throw "The verifier accepted an invocation without the original ZIP.`n$missingArchiveOutput"
}
Assert-FailedReportHasNoVerifiedIdentity `
    -Output $missingArchiveOutput `
    -Context 'The missing-archive verifier run'

if ($document.filePromptAiAcceptance.result -ne $(
    if ($isWindows7Sp1) { 'pass' } else { 'fail' }
)) {
    throw 'The acceptance report result does not match the host OS gate.'
}
$osCheck = @(
    $document.filePromptAiAcceptance.checks.check |
        Where-Object { $_.id -eq 'os.win7-sp1' }
)
if ($osCheck.Count -ne 1 -or $osCheck[0].status -ne $(
    if ($isWindows7Sp1) { 'pass' } else { 'fail' }
)) {
    throw 'The XML report OS result does not match the independently detected host.'
}
$requiredIds = @(
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
foreach ($identifier in $requiredIds) {
    if (@($document.filePromptAiAcceptance.checks.check |
        Where-Object { $_.id -eq $identifier }).Count -ne 1) {
        throw "The XML report is missing check: $identifier"
    }
}

$identity = $root.packageIdentity
if ($isWindows7Sp1) {
    $manifestPath = Join-Path $stagingRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
    $expectedManifestHash = (
        Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256
    ).Hash
    $expectedManifestEntryCount = @(
        Get-Content -LiteralPath $manifestPath -Encoding UTF8
    ).Count
    $expectedArchive = Get-Item -LiteralPath $archivePath
    $expectedArchiveHash = (
        Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    ).Hash
    if ($identity.status -ne 'verified' -or
        $identity.archiveName -cne $archiveName -or
        $identity.archiveSha256 -cne $expectedArchiveHash -or
        [int64]$identity.archiveSize -ne $expectedArchive.Length -or
        $identity.manifestName -ne 'PACKAGE-CHECKSUMS-SHA256.txt' -or
        $identity.manifestSha256 -cne $expectedManifestHash -or
        [int]$identity.manifestEntryCount -ne $expectedManifestEntryCount) {
        throw 'A passing acceptance report does not identify the exact locked package manifest.'
    }
}
else {
    if ($identity.status -ne 'unverified' -or
        $identity.Attributes.Count -ne 1 -or
        $identity.HasAttribute('archiveName') -or
        $identity.HasAttribute('archiveSha256') -or
        $identity.HasAttribute('archiveSize') -or
        $identity.HasAttribute('manifestSha256') -or
        $identity.HasAttribute('manifestEntryCount') -or
        $identity.HasAttribute('manifestName')) {
        throw 'A failed acceptance report exposes a package identity that could be mistaken for PASS evidence.'
    }
}

$recordedHash = (
    Get-Content -LiteralPath $checksumPath -Raw -Encoding UTF8
).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
$actualHash = (Get-FileHash -LiteralPath $report.FullName -Algorithm SHA256).Hash
if ($recordedHash -ne $actualHash) {
    throw 'The acceptance report checksum does not match the XML report.'
}

$unexpectedPackageReports = @(
    Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
        Where-Object {
            $_.Name -like 'FilePromptAI-Acceptance-*.xml' -or
            $_.Name -like 'FilePromptAI-Acceptance-*.xml.sha256.txt'
        }
)
if ($unexpectedPackageReports.Count -ne 0) {
    throw 'The verifier polluted the exact offline package file set with a report.'
}

Remove-Item -LiteralPath $report.FullName -Force
Remove-Item -LiteralPath $checksumPath -Force

# A hostile TEMP setting inside the package must not add report files to the
# exact release file set. The verifier must use its isolated LocalAppData root.
$beforeFallbackData = @()
if (Test-Path -LiteralPath $acceptanceFallbackDataRoot -PathType Container) {
    $beforeFallbackData = @(
        Get-ChildItem -LiteralPath $acceptanceFallbackDataRoot `
            -Filter 'FilePromptAI-Acceptance-Data-*' `
            -Directory |
            ForEach-Object { $_.FullName }
    )
}
$savedTemp = $env:TEMP
$savedTmp = $env:TMP
try {
    $env:TEMP = $stagingRoot
    $env:TMP = $stagingRoot
    $fallbackOutput = & $verifierPath --archive $archivePath 2>&1 | Out-String
    $fallbackExitCode = $LASTEXITCODE
}
finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
}
Assert-ExpectedHostOutcome `
    -ExitCode $fallbackExitCode `
    -Output $fallbackOutput `
    -Context 'The package-contained TEMP verifier run'
if ($fallbackOutput -notmatch '(?m)^PASS \| application\.cleanup \|') {
    throw 'The verifier failed unexpectedly with TEMP inside the package.'
}
$fallbackReports = @(
    Get-ChildItem -LiteralPath $acceptanceFallbackRoot `
        -Filter 'FilePromptAI-Acceptance-*.xml' `
        -File |
        Where-Object { $beforeFallbackReports -notcontains $_.FullName } |
        Sort-Object LastWriteTimeUtc -Descending
)
if ($fallbackReports.Count -ne 1) {
    throw "Expected one fallback acceptance report, found $($fallbackReports.Count)."
}
$fallbackReport = $fallbackReports[0]
$fallbackChecksum = "$($fallbackReport.FullName).sha256.txt"
if (-not (Test-Path -LiteralPath $fallbackChecksum -PathType Leaf)) {
    throw 'The fallback acceptance report checksum is missing.'
}
Remove-Item -LiteralPath $fallbackReport.FullName -Force
Remove-Item -LiteralPath $fallbackChecksum -Force
$afterFallbackData = @()
if (Test-Path -LiteralPath $acceptanceFallbackDataRoot -PathType Container) {
    $afterFallbackData = @(
        Get-ChildItem -LiteralPath $acceptanceFallbackDataRoot `
            -Filter 'FilePromptAI-Acceptance-Data-*' `
            -Directory |
            Where-Object { $beforeFallbackData -notcontains $_.FullName }
    )
}
if ($afterFallbackData.Count -ne 0) {
    throw 'The fallback acceptance run left an isolated data directory behind.'
}

# A TEMP junction that resolves into the package must be rejected before any
# report or isolated application data is created through it.
$junctionPath = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-Acceptance-Junction-' + [Guid]::NewGuid().ToString('N')
)
$beforeJunctionReports = @()
if (Test-Path -LiteralPath $acceptanceFallbackRoot -PathType Container) {
    $beforeJunctionReports = @(
        Get-ChildItem -LiteralPath $acceptanceFallbackRoot `
            -Filter 'FilePromptAI-Acceptance-*.xml' `
            -File |
            ForEach-Object { $_.FullName }
    )
}
try {
    $junction = New-Item `
        -ItemType Junction `
        -Path $junctionPath `
        -Target $stagingRoot
    if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw 'The hostile TEMP test did not create a reparse point.'
    }

    $env:TEMP = $junctionPath
    $env:TMP = $junctionPath
    $junctionOutput = & $verifierPath --archive $archivePath 2>&1 | Out-String
    $junctionExitCode = $LASTEXITCODE
}
finally {
    $env:TEMP = $savedTemp
    $env:TMP = $savedTmp
    if (Test-Path -LiteralPath $junctionPath) {
        [IO.Directory]::Delete($junctionPath)
    }
}
Assert-ExpectedHostOutcome `
    -ExitCode $junctionExitCode `
    -Output $junctionOutput `
    -Context 'The TEMP-junction verifier run'
if ($junctionOutput -notmatch '(?m)^PASS \| application\.cleanup \|') {
    throw "The verifier failed unexpectedly with a TEMP junction into the package.`n$junctionOutput"
}
$junctionReports = @(
    Get-ChildItem -LiteralPath $acceptanceFallbackRoot `
        -Filter 'FilePromptAI-Acceptance-*.xml' `
        -File |
        Where-Object { $beforeJunctionReports -notcontains $_.FullName } |
        Sort-Object LastWriteTimeUtc -Descending
)
if ($junctionReports.Count -ne 1) {
    throw "Expected one fallback report for the TEMP junction, found $($junctionReports.Count)."
}
$junctionReport = $junctionReports[0]
$junctionChecksum = "$($junctionReport.FullName).sha256.txt"
if (-not (Test-Path -LiteralPath $junctionChecksum -PathType Leaf)) {
    throw 'The TEMP junction fallback report checksum is missing.'
}
$unexpectedJunctionPackageReports = @(
    Get-ChildItem -LiteralPath $stagingRoot -Recurse -File |
        Where-Object {
            $_.Name -like 'FilePromptAI-Acceptance-*.xml' -or
            $_.Name -like 'FilePromptAI-Acceptance-*.xml.sha256.txt'
        }
)
if ($unexpectedJunctionPackageReports.Count -ne 0) {
    throw 'The TEMP junction caused acceptance output inside the package.'
}
Remove-Item -LiteralPath $junctionReport.FullName -Force
Remove-Item -LiteralPath $junctionChecksum -Force

# A checksum failure must gate every operation that would load or execute the
# packaged application. Work on a disposable copy so the release stays exact.
$tamperRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-Acceptance-Tamper-' + [Guid]::NewGuid().ToString('N')
)
Copy-Item -LiteralPath $stagingRoot -Destination $tamperRoot -Recurse
try {
    Add-Content -LiteralPath (
        Join-Path $tamperRoot 'acceptance\fixtures\acceptance.txt'
    ) -Value 'tampered' -Encoding ASCII
    $tamperVerifier = Join-Path $tamperRoot 'Verify-FilePromptAI.exe'
    $tamperOutput = & $tamperVerifier --archive $archivePath 2>&1 | Out-String
    $tamperExitCode = $LASTEXITCODE
    if (($tamperExitCode -band 8) -ne 8 -or
        $tamperOutput -notmatch '(?m)^FAIL \| package\.checksums \|' -or
        $tamperOutput -notmatch '(?m)^SKIP \| files\.extract \|' -or
        $tamperOutput -notmatch '(?m)^SKIP \| files\.export \|' -or
        $tamperOutput -notmatch '(?m)^SKIP \| api\.models \|' -or
        $tamperOutput -notmatch '(?m)^SKIP \| api\.chat-completions \|' -or
        $tamperOutput -notmatch '(?m)^SKIP \| application\.launch \|' -or
        $tamperOutput -notmatch '(?m)^SKIP \| application\.ui-journey \|' -or
        $tamperOutput -match '(?m)^(PASS|FAIL|ERROR) \| application\.launch \|') {
        throw "A tampered package was not safely gated. exitCode=$tamperExitCode`n$tamperOutput"
    }
    Assert-FailedReportHasNoVerifiedIdentity `
        -Output $tamperOutput `
        -Context 'The tampered-package verifier run'

    # Recalculate the package-owned manifest after the same change. The
    # verifier's embedded trusted set must still reject the modified payload.
    $tamperManifest = Join-Path $tamperRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
    $tamperLines = @(
        Get-ChildItem -LiteralPath $tamperRoot -File -Recurse |
            Where-Object {
                -not [string]::Equals(
                    $_.FullName,
                    $tamperManifest,
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object FullName |
            ForEach-Object {
                $relativePath = $_.FullName.Substring(
                    $tamperRoot.Length
                ).TrimStart('\')
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                "$hash *$relativePath"
            }
    )
    [IO.File]::WriteAllLines(
        $tamperManifest,
        $tamperLines,
        (New-Object Text.UTF8Encoding($false)))
    $recalculatedOutput = & $tamperVerifier --archive $archivePath 2>&1 | Out-String
    $recalculatedExitCode = $LASTEXITCODE
    if (($recalculatedExitCode -band 8) -ne 8 -or
        $recalculatedOutput -notmatch '(?m)^FAIL \| package\.checksums \|' -or
        $recalculatedOutput -notmatch 'embedded trusted payload set' -or
        $recalculatedOutput -notmatch '(?m)^SKIP \| application\.launch \|' -or
        $recalculatedOutput -notmatch '(?m)^SKIP \| application\.ui-journey \|' -or
        $recalculatedOutput -match '(?m)^(PASS|FAIL|ERROR) \| application\.launch \|') {
        throw "A modified payload with a recalculated manifest bypassed the trusted set. exitCode=$recalculatedExitCode`n$recalculatedOutput"
    }
    Assert-FailedReportHasNoVerifiedIdentity `
        -Output $recalculatedOutput `
        -Context 'The recalculated-manifest verifier run'
}
finally {
    $resolvedTamperRoot = [IO.Path]::GetFullPath($tamperRoot)
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTamperRoot.StartsWith(
        $resolvedTempRoot,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTamperRoot).StartsWith(
            'FilePromptAI-Acceptance-Tamper-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTamperRoot -Recurse -Force
    }
}

if ($isWindows7Sp1) {
    Write-Host 'PASS | acceptance verifier accepts verified Windows 7 SP1 host'
}
else {
    Write-Host "PASS | acceptance verifier rejects non-Win7 host | exitCode=$exitCode"
}
