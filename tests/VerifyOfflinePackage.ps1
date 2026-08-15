param(
    [string]$Version = '1.8'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$archivePath = Join-Path $projectRoot "FilePromptAI-Win7-Full-v$Version.zip"
$archiveChecksumPath = "$archivePath.sha256.txt"

if (-not (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
    throw "Missing staging directory: $stagingRoot"
}
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Missing archive: $archivePath"
}
if (-not (Test-Path -LiteralPath $archiveChecksumPath -PathType Leaf)) {
    throw "Missing archive checksum: $archiveChecksumPath"
}

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$recordedArchiveHash = (
    Get-Content -LiteralPath $archiveChecksumPath -Encoding UTF8 -Raw
).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
if ($archiveHash -ne $recordedArchiveHash) {
    throw "Archive checksum mismatch: $archiveHash"
}

$payloadChecksumPath = Join-Path $stagingRoot 'PACKAGE-CHECKSUMS-SHA256.txt'
$checksumFailures = New-Object Collections.Generic.List[string]
$checksumEntries = 0
foreach ($line in Get-Content -LiteralPath $payloadChecksumPath -Encoding UTF8) {
    if ($line -notmatch '^([0-9A-F]{64}) \*(.+)$') {
        $checksumFailures.Add("invalid line: $line")
        continue
    }

    $checksumEntries++
    $payloadPath = Join-Path $stagingRoot $Matches[2]
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        $checksumFailures.Add("missing: $($Matches[2])")
        continue
    }

    $actual = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
    if ($actual -ne $Matches[1]) {
        $checksumFailures.Add("hash: $($Matches[2])")
    }
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
if ($appVersion -ne '1.9.0.0' -or
    $bootstrapperVersion -ne '1.9.0.0' -or
    $uninstallerVersion -ne '1.9.0.0') {
    throw "Unexpected executable versions: app=$appVersion bootstrapper=$bootstrapperVersion uninstaller=$uninstallerVersion"
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

Write-Host "PASS | offline package | version=$Version | files=$($entryNames.Count) | checksums=$checksumEntries | sha256=$archiveHash"
