param(
    [string]$Version = '1.17'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$runName = 'FilePromptAI-UninstallSecurity-' + [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $artifactRoot $runName
$outsideRoot = Join-Path $artifactRoot ($runName + '-outside')
$sourceUninstaller = Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe'
$sourceConfig = "$sourceUninstaller.config"

foreach ($required in @($sourceUninstaller, $sourceConfig)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing packaged uninstaller file: $required"
    }
}

function New-MinimalPackage {
    param(
        [string]$Root,
        [switch]$IncludeReadme
    )

    New-Item -ItemType Directory -Path (Join-Path $Root 'app') -Force |
        Out-Null
    Copy-Item -LiteralPath $sourceUninstaller `
        -Destination (Join-Path $Root 'Uninstall-FilePromptAI.exe') -Force
    Copy-Item -LiteralPath $sourceConfig `
        -Destination (Join-Path $Root 'Uninstall-FilePromptAI.exe.config') -Force
    Copy-Item -LiteralPath $sourceUninstaller `
        -Destination (Join-Path $Root 'Start-FilePromptAI.exe') -Force
    Copy-Item -LiteralPath $sourceUninstaller `
        -Destination (Join-Path $Root 'app\FilePromptAI.exe') -Force

    if ($IncludeReadme) {
        [IO.File]::WriteAllText(
            (Join-Path $Root 'app\README.md'),
            'packaged readme',
            (New-Object Text.UTF8Encoding($false)))
    }
}

function Write-PackageManifest {
    param(
        [string]$Root,
        [string[]]$RelativePaths
    )

    $lines = @(
        $RelativePaths |
            Sort-Object |
            ForEach-Object {
                $path = Join-Path $Root $_
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    throw "Cannot hash missing test package file: $path"
                }

                $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                "$hash *$_"
            }
    )
    [IO.File]::WriteAllLines(
        (Join-Path $Root 'PACKAGE-CHECKSUMS-SHA256.txt'),
        $lines,
        (New-Object Text.UTF8Encoding($false)))
}

function Invoke-UninstallerWorker {
    param([string]$PackageRoot)

    $helperName = 'FilePromptAI-Uninstall-' + [Guid]::NewGuid().ToString('N')
    $helperRoot = Join-Path ([IO.Path]::GetTempPath()) $helperName
    New-Item -ItemType Directory -Path $helperRoot -Force | Out-Null
    try {
        $helperExe = Join-Path $helperRoot 'Uninstall-FilePromptAI.exe'
        Copy-Item -LiteralPath $sourceUninstaller `
            -Destination $helperExe -Force
        Copy-Item -LiteralPath $sourceConfig `
            -Destination "$helperExe.config" -Force

        $arguments = @(
            '--execute',
            ('"' + $PackageRoot + '"'),
            '--delete-data',
            'false',
            '--parent-pid',
            '2147483647',
            '--parent-start-ticks',
            '1',
            '--app-pid',
            '0',
            '--app-start-ticks',
            '0',
            '--silent'
        ) -join ' '
        $process = Start-Process `
            -FilePath $helperExe `
            -ArgumentList $arguments `
            -WorkingDirectory $helperRoot `
            -PassThru `
            -Wait
        $exitCode = $process.ExitCode

        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while ((Test-Path -LiteralPath $helperRoot) -and
            [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Test-Path -LiteralPath $helperRoot) {
            throw "Temporary worker was not cleaned: $helperRoot"
        }

        return $exitCode
    }
    finally {
        $systemTemp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        $resolved = [IO.Path]::GetFullPath($helperRoot)
        if ($resolved.StartsWith(
            $systemTemp + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved) -eq $helperName) {
            Remove-Item -LiteralPath $resolved -Recurse -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outsideRoot -Force | Out-Null
$junctionPath = $null
try {
    $modifiedRoot = Join-Path $runRoot 'modified'
    New-MinimalPackage -Root $modifiedRoot -IncludeReadme
    $modifiedPaths = @(
        'app\FilePromptAI.exe',
        'app\README.md',
        'Start-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe.config'
    )
    Write-PackageManifest -Root $modifiedRoot -RelativePaths $modifiedPaths
    Add-Content -LiteralPath (Join-Path $modifiedRoot 'app\README.md') `
        -Value 'user modification' -Encoding UTF8

    $modifiedExit = Invoke-UninstallerWorker -PackageRoot $modifiedRoot
    if ($modifiedExit -ne 4) {
        throw "Modified-file uninstall returned $modifiedExit instead of 4."
    }
    foreach ($retained in @(
        'app\FilePromptAI.exe',
        'app\README.md',
        'Start-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe.config',
        'PACKAGE-CHECKSUMS-SHA256.txt'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $modifiedRoot $retained))) {
            throw "Uninstaller did not retain required retry file: $retained"
        }
    }
    if (@(Get-ChildItem -LiteralPath $modifiedRoot -File -Recurse -Force).Count -ne
        ($modifiedPaths.Count + 1)) {
        throw 'Modified-file rejection changed the manifest-controlled package file set.'
    }
    Write-Host 'PASS | modified package file rejects uninstall with zero package-file deletion'

    $junctionRoot = Join-Path $runRoot 'junction'
    New-MinimalPackage -Root $junctionRoot
    $outsideFile = Join-Path $outsideRoot 'outside.txt'
    [IO.File]::WriteAllText(
        $outsideFile,
        'must remain outside the package root',
        (New-Object Text.UTF8Encoding($false)))
    $outsideHash = (Get-FileHash -LiteralPath $outsideFile -Algorithm SHA256).Hash
    $junctionPath = Join-Path $junctionRoot 'linked'
    New-Item -ItemType Junction -Path $junctionPath -Target $outsideRoot |
        Out-Null

    $junctionPaths = @(
        'app\FilePromptAI.exe',
        'linked\outside.txt',
        'Start-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe.config'
    )
    Write-PackageManifest -Root $junctionRoot -RelativePaths $junctionPaths
    $junctionExit = Invoke-UninstallerWorker -PackageRoot $junctionRoot
    if ($junctionExit -ne 4) {
        throw "Junction uninstall returned $junctionExit instead of 4."
    }
    if (-not (Test-Path -LiteralPath $outsideFile -PathType Leaf) -or
        (Get-FileHash -LiteralPath $outsideFile -Algorithm SHA256).Hash -ne
            $outsideHash) {
        throw 'Uninstaller changed a file reached through an outside junction.'
    }
    foreach ($retained in @(
        'app\FilePromptAI.exe',
        'Start-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe',
        'Uninstall-FilePromptAI.exe.config',
        'PACKAGE-CHECKSUMS-SHA256.txt'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $junctionRoot $retained))) {
            throw "Junction rejection did not retain retry file: $retained"
        }
    }
    Write-Host 'PASS | junction target outside the package root is preserved'

    & powershell -NoProfile -ExecutionPolicy Bypass -File (
        Join-Path $testRoot 'RunUninstallerUserDataSmokeTest.ps1') `
        -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw "Uninstaller user-data smoke test failed with exit code $LASTEXITCODE."
    }
}
finally {
    if ($junctionPath -and (Test-Path -LiteralPath $junctionPath)) {
        [IO.Directory]::Delete($junctionPath, $false)
    }
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactRoot)
    foreach ($candidate in @($runRoot, $outsideRoot)) {
        $resolved = [IO.Path]::GetFullPath($candidate)
        if ($resolved.StartsWith(
            $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolved).StartsWith(
                'FilePromptAI-UninstallSecurity-',
                [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force `
                -ErrorAction SilentlyContinue
        }
    }
}
