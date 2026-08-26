param(
    [string]$Version = '1.18'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$archivePath = Join-Path $projectRoot "FilePromptAI-Win7-Full-v$Version.zip"
$archiveChecksumPath = "$archivePath.sha256.txt"
$approvedLibraryChecksumPath = Join-Path $projectRoot 'LIBRARIES-SHA256.txt'

if (-not (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
    throw "Missing staging directory: $stagingRoot"
}
$stagingItem = Get-Item -LiteralPath $stagingRoot -Force
if (($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The staging directory must not be a reparse point: $stagingRoot"
}
$stagingReparsePoints = @(
    Get-ChildItem -LiteralPath $stagingRoot -Force -Recurse |
        Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        }
)
if ($stagingReparsePoints.Count -gt 0) {
    throw "The staging directory contains a reparse point: $($stagingReparsePoints[0].FullName)"
}
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Missing archive: $archivePath"
}
if (-not (Test-Path -LiteralPath $archiveChecksumPath -PathType Leaf)) {
    throw "Missing archive checksum: $archiveChecksumPath"
}
if (-not (Test-Path -LiteralPath $approvedLibraryChecksumPath -PathType Leaf)) {
    throw "Missing approved library checksum manifest: $approvedLibraryChecksumPath"
}

$stagedLibraryChecksumPath = Join-Path `
    $stagingRoot `
    'app\LIBRARIES-SHA256.txt'
if (-not (Test-Path -LiteralPath $stagedLibraryChecksumPath -PathType Leaf)) {
    throw "Missing staged library checksum manifest: $stagedLibraryChecksumPath"
}
$approvedManifestHash = (Get-FileHash `
    -LiteralPath $approvedLibraryChecksumPath `
    -Algorithm SHA256).Hash
$stagedManifestHash = (Get-FileHash `
    -LiteralPath $stagedLibraryChecksumPath `
    -Algorithm SHA256).Hash
if ($approvedManifestHash -ne $stagedManifestHash) {
    throw 'The staged library checksum manifest does not match the repository manifest.'
}

$approvedLibraries = @{}
foreach ($line in Get-Content `
    -LiteralPath $approvedLibraryChecksumPath `
    -Encoding UTF8) {
    if ($line -notmatch '^([0-9A-F]{64}) \*([^\\/]+\.dll)$') {
        throw "Invalid approved library checksum line: $line"
    }
    if ($approvedLibraries.ContainsKey($Matches[2])) {
        throw "Duplicate approved library checksum entry: $($Matches[2])"
    }
    $approvedLibraries[$Matches[2]] = $Matches[1]
}
if ($approvedLibraries.Count -ne 33) {
    throw "Expected 33 approved libraries, found $($approvedLibraries.Count)."
}

$stagedLibraryNames = @(
    Get-ChildItem -LiteralPath (Join-Path $stagingRoot 'app') `
        -Filter '*.dll' `
        -File |
        ForEach-Object { $_.Name } |
        Sort-Object
)
$libraryDifferences = @(
    Compare-Object `
        -ReferenceObject @($approvedLibraries.Keys | Sort-Object) `
        -DifferenceObject $stagedLibraryNames `
        -CaseSensitive
)
if ($libraryDifferences.Count -gt 0) {
    throw 'The staged DLL set does not match the approved library checksum manifest.'
}
foreach ($name in $stagedLibraryNames) {
    $actualHash = (Get-FileHash `
        -LiteralPath (Join-Path $stagingRoot "app\$name") `
        -Algorithm SHA256).Hash
    if ($actualHash -ne $approvedLibraries[$name]) {
        throw "The staged library failed its approved SHA-256 check: $name"
    }
}

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$recordedArchiveHash = (
    Get-Content -LiteralPath $archiveChecksumPath -Encoding UTF8 -Raw
).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
if ($archiveHash -ne $recordedArchiveHash) {
    throw "Archive checksum mismatch: $archiveHash"
}

$payloadChecksumPath = Join-Path $stagingRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
if (-not (Test-Path -LiteralPath $payloadChecksumPath -PathType Leaf)) {
    throw "Missing payload checksum manifest: $payloadChecksumPath"
}

$checksumFailures = New-Object Collections.Generic.List[string]
$payloadRoot = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\') + '\'
$checksumEntries = @{}
foreach ($line in Get-Content -LiteralPath $payloadChecksumPath -Encoding UTF8) {
    if ($line -notmatch '^([0-9A-F]{64}) \*(.+)$') {
        $checksumFailures.Add("invalid line: $line")
        continue
    }

    $expectedHash = $Matches[1]
    $relativePath = $Matches[2]
    $segments = $relativePath.Split('\')
    if ([IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains(':') -or
        $relativePath.Contains('/') -or
        $segments -contains '' -or
        $segments -contains '.' -or
        $segments -contains '..') {
        $checksumFailures.Add("unsafe path: $relativePath")
        continue
    }

    if ($checksumEntries.ContainsKey($relativePath)) {
        $checksumFailures.Add("duplicate: $relativePath")
        continue
    }

    $payloadPath = [IO.Path]::GetFullPath((Join-Path $stagingRoot $relativePath))
    if (-not $payloadPath.StartsWith(
        $payloadRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
        $checksumFailures.Add("outside staging: $relativePath")
        continue
    }

    $canonicalRelativePath = $payloadPath.Substring($payloadRoot.Length)
    if (-not [string]::Equals(
        $canonicalRelativePath,
        $relativePath,
        [StringComparison]::Ordinal)) {
        $checksumFailures.Add("non-canonical path: $relativePath")
        continue
    }

    $checksumEntries[$relativePath] = $expectedHash
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        $checksumFailures.Add("missing: $relativePath")
        continue
    }

    $actual = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
    if ($actual -ne $expectedHash) {
        $checksumFailures.Add("hash: $relativePath")
    }
}

$stagedPayloadFiles = @(
    Get-ChildItem -LiteralPath $stagingRoot -File -Recurse |
        Where-Object {
            -not [string]::Equals(
                $_.FullName,
                $payloadChecksumPath,
                [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            $_.FullName.Substring($payloadRoot.Length)
        } |
        Sort-Object
)
$checksumDifferences = @(
    Compare-Object `
        -ReferenceObject $stagedPayloadFiles `
        -DifferenceObject @($checksumEntries.Keys | Sort-Object) `
        -CaseSensitive
)
if ($checksumDifferences.Count -gt 0) {
    $details = $checksumDifferences |
        ForEach-Object { "$($_.InputObject) [$($_.SideIndicator)]" }
    $checksumFailures.Add("file set: $($details -join ', ')")
}
if ($checksumFailures.Count -ne 0) {
    throw "Payload checksum failures: $($checksumFailures -join ', ')"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entryNames = New-Object Collections.Generic.List[string]
    foreach ($entry in $zip.Entries) {
        if ([string]::IsNullOrEmpty($entry.Name)) {
            continue
        }

        $name = $entry.FullName.Replace('/', '\')
        $segments = $name.Split('\')
        if ([IO.Path]::IsPathRooted($name) -or
            $name.Contains(':') -or
            $segments -contains '..') {
            throw "Unsafe ZIP entry: $name"
        }

        if ($entryNames.Contains($name)) {
            throw "Duplicate ZIP entry: $name"
        }

        $entryNames.Add($name)
    }
    if ($entryNames.Contains('RELEASE-SHA256.txt')) {
        throw 'RELEASE-SHA256.txt must remain outside the ZIP to avoid a self-referential release digest.'
    }
}
finally {
    $zip.Dispose()
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAIPackageVerify-' + [Guid]::NewGuid().ToString('N')
)
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $temporaryRoot)
    $stagedFiles = @(
        Get-ChildItem -LiteralPath $stagingRoot -File -Recurse |
            ForEach-Object {
                $_.FullName.Substring($stagingRoot.Length).TrimStart('\')
            } |
            Sort-Object
    )
    $extractedFiles = @(
        Get-ChildItem -LiteralPath $temporaryRoot -File -Recurse |
            ForEach-Object {
                $_.FullName.Substring($temporaryRoot.Length).TrimStart('\')
            } |
            Sort-Object
    )
    if (($stagedFiles -join "`n") -ne ($extractedFiles -join "`n")) {
        throw 'ZIP file list does not match the staging directory.'
    }

    foreach ($relativePath in $stagedFiles) {
        $stagedHash = (Get-FileHash -LiteralPath (
            Join-Path $stagingRoot $relativePath
        ) -Algorithm SHA256).Hash
        $extractedHash = (Get-FileHash -LiteralPath (
            Join-Path $temporaryRoot $relativePath
        ) -Algorithm SHA256).Hash
        if ($stagedHash -ne $extractedHash) {
            throw "ZIP payload mismatch: $relativePath"
        }
    }
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporary.StartsWith(
        $resolvedSystemTemp,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary).StartsWith(
            'FilePromptAIPackageVerify-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}

$appVersion = (Get-Item -LiteralPath (
    Join-Path $stagingRoot 'app\FilePromptAI.exe'
)).VersionInfo.FileVersion
$bootstrapperVersion = (Get-Item -LiteralPath (
    Join-Path $stagingRoot 'Start-FilePromptAI.exe'
)).VersionInfo.FileVersion
$uninstallerPath = Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe'
$uninstallerVersion = (Get-Item -LiteralPath $uninstallerPath).VersionInfo.FileVersion
$acceptancePath = Join-Path $stagingRoot 'Verify-FilePromptAI.exe'
$acceptanceVersion = (Get-Item -LiteralPath $acceptancePath).VersionInfo.FileVersion
if ($appVersion -ne '1.18.0.0' -or
    $bootstrapperVersion -ne '1.18.0.0' -or
    $uninstallerVersion -ne '1.18.0.0' -or
    $acceptanceVersion -ne '1.18.0.0') {
    throw "Unexpected executable versions: app=$appVersion bootstrapper=$bootstrapperVersion uninstaller=$uninstallerVersion acceptance=$acceptanceVersion"
}

foreach ($requiredAcceptancePath in @(
    'Verify-FilePromptAI.exe',
    'Verify-FilePromptAI.exe.config',
    'acceptance\fixtures\acceptance.txt',
    'acceptance\fixtures\sample.pdf',
    'acceptance\fixtures\sample.docx',
    'acceptance\fixtures\sample.png'
)) {
    if (-not (Test-Path -LiteralPath (
        Join-Path $stagingRoot $requiredAcceptancePath
    ) -PathType Leaf)) {
        throw "Missing acceptance artifact: $requiredAcceptancePath"
    }
}

$bootstrapperPath = Join-Path $stagingRoot 'Start-FilePromptAI.exe'
$runtimeVerification = Start-Process `
    -FilePath $bootstrapperPath `
    -ArgumentList '--verify-runtime' `
    -WorkingDirectory $stagingRoot `
    -PassThru `
    -Wait
if ($runtimeVerification.ExitCode -ne 0) {
    throw "Bundled .NET runtime verification failed with exit code $($runtimeVerification.ExitCode)."
}

$uninstallerCheck = Start-Process `
    -FilePath $uninstallerPath `
    -ArgumentList '--check' `
    -WorkingDirectory $stagingRoot `
    -PassThru `
    -Wait
if ($uninstallerCheck.ExitCode -ne 0) {
    throw "Uninstaller safety check failed with exit code $($uninstallerCheck.ExitCode)."
}

Write-Host "PASS | offline package | version=$Version | files=$($entryNames.Count) | checksums=$($checksumEntries.Count) | sha256=$archiveHash"
