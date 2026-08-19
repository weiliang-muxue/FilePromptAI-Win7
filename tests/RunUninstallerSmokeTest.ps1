param(
    [string]$Version = '1.13'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testFolderName = 'FilePromptAI-UninstallSmoke-' + [Guid]::NewGuid().ToString('N')
$sandboxRoot = Join-Path $artifactRoot $testFolderName
$helperFolderName = 'FilePromptAI-Uninstall-' + [Guid]::NewGuid().ToString('N')
$helperRoot = Join-Path ([IO.Path]::GetTempPath()) $helperFolderName

if (-not (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
    throw "Missing package staging directory: $stagingRoot"
}

New-Item -ItemType Directory -Path $sandboxRoot -Force | Out-Null
New-Item -ItemType Directory -Path $helperRoot -Force | Out-Null

try {
    $stagingItems = @(Get-ChildItem -LiteralPath $stagingRoot -Force)
    if ($stagingItems.Count -eq 0) {
        throw "Package staging directory is empty: $stagingRoot"
    }
    foreach ($stagingItem in $stagingItems) {
        Copy-Item -LiteralPath $stagingItem.FullName `
            -Destination $sandboxRoot `
            -Recurse `
            -Force
    }

    $sentinelPath = Join-Path $sandboxRoot 'keep-user-file.txt'
    [IO.File]::WriteAllText(
        $sentinelPath,
        'This file is not part of the release manifest.',
        (New-Object Text.UTF8Encoding($false)))

    $sourceUninstaller = Join-Path $sandboxRoot 'Uninstall-FilePromptAI.exe'
    $sourceConfig = "$sourceUninstaller.config"
    $helperUninstaller = Join-Path $helperRoot 'Uninstall-FilePromptAI.exe'
    Copy-Item -LiteralPath $sourceUninstaller -Destination $helperUninstaller -Force
    Copy-Item -LiteralPath $sourceConfig -Destination "$helperUninstaller.config" -Force

    $arguments = @(
        '--execute',
        ('"' + $sandboxRoot + '"'),
        '--delete-data',
        'false',
        '--parent-pid',
        '2147483647',
        '--silent'
    ) -join ' '
    $process = Start-Process `
        -FilePath $helperUninstaller `
        -ArgumentList $arguments `
        -WorkingDirectory $helperRoot `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        throw "Uninstaller worker failed with exit code $($process.ExitCode)."
    }

    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(90)
    while ((Test-Path -LiteralPath $helperRoot) -and
        [DateTime]::UtcNow -lt $cleanupDeadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Test-Path -LiteralPath $helperRoot) {
        throw "Temporary uninstaller directory was not cleaned: $helperRoot"
    }

    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'The uninstaller removed an extra file that was not in the release manifest.'
    }
    if (Test-Path -LiteralPath (Join-Path $sandboxRoot 'app\FilePromptAI.exe')) {
        throw 'The uninstaller did not remove the packaged application.'
    }
    if (Test-Path -LiteralPath (Join-Path $sandboxRoot 'Start-FilePromptAI.exe')) {
        throw 'The uninstaller did not remove the packaged launcher.'
    }
    if (Test-Path -LiteralPath (Join-Path $sandboxRoot 'PACKAGE-CHECKSUMS-SHA256.txt')) {
        throw 'The uninstaller did not remove the release manifest.'
    }

    $remainingFiles = @(
        Get-ChildItem -LiteralPath $sandboxRoot -File -Recurse |
            ForEach-Object { $_.FullName }
    )
    if ($remainingFiles.Count -ne 1 -or
        $remainingFiles[0] -ne $sentinelPath) {
        throw "Unexpected files remain after uninstall: $($remainingFiles -join ', ')"
    }

    Write-Host 'PASS | uninstaller removes only verified package files and cleans its temporary copy'
}
finally {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
    $resolvedSandbox = [IO.Path]::GetFullPath($sandboxRoot)
    if ($resolvedSandbox.StartsWith(
        $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedSandbox).StartsWith(
            'FilePromptAI-UninstallSmoke-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force `
            -ErrorAction SilentlyContinue
    }

    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $resolvedHelper = [IO.Path]::GetFullPath($helperRoot)
    if ($resolvedHelper.StartsWith(
        $resolvedSystemTemp + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedHelper) -eq $helperFolderName) {
        Remove-Item -LiteralPath $resolvedHelper -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}
