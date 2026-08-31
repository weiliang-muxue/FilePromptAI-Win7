param(
    [string]$Version = '1.19'
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
$runningRoot = $null
$lockedRoot = $null
$waitingRoot = $null
$manifestErrorRoot = $null
$commitFailureRoot = $null
$preflightFailureRoot = $null

function Get-PackageSnapshot {
    param([string]$Root)

    $snapshot = @{}
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)) {
        $relativePath = $file.FullName.Substring($Root.Length).TrimStart('\')
        $snapshot[$relativePath] = '{0}:{1}' -f `
            $file.Length,
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    return $snapshot
}

function Assert-PackageSnapshotEqual {
    param(
        [hashtable]$Before,
        [hashtable]$After,
        [string]$Scenario
    )

    $beforeKeys = @($Before.Keys | Sort-Object)
    $afterKeys = @($After.Keys | Sort-Object)
    if (($beforeKeys -join "`n") -ne ($afterKeys -join "`n")) {
        throw "$Scenario changed the package file set."
    }
    foreach ($relativePath in $beforeKeys) {
        if ($Before[$relativePath] -ne $After[$relativePath]) {
            throw "$Scenario changed package bytes: $relativePath"
        }
    }
}

function Copy-StagingPackage {
    param([string]$Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($stagingItem in @(Get-ChildItem -LiteralPath $stagingRoot -Force)) {
        Copy-Item -LiteralPath $stagingItem.FullName `
            -Destination $Destination `
            -Recurse `
            -Force
    }
}

function Invoke-UninstallerWorker {
    param(
        [string]$PackageRoot,
        [string]$SourceUninstaller,
        [string]$SourceConfig,
        [int]$ApplicationProcessId = 0,
        [long]$ApplicationStartTicks = 0,
        [scriptblock]$AfterStart
    )

    $workerName = 'FilePromptAI-Uninstall-' + [Guid]::NewGuid().ToString('N')
    $workerRoot = Join-Path ([IO.Path]::GetTempPath()) $workerName
    New-Item -ItemType Directory -Path $workerRoot -Force | Out-Null
    try {
        $workerExe = Join-Path $workerRoot 'Uninstall-FilePromptAI.exe'
        Copy-Item -LiteralPath $SourceUninstaller -Destination $workerExe -Force
        Copy-Item -LiteralPath $SourceConfig `
            -Destination "$workerExe.config" -Force
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
            $ApplicationProcessId.ToString(
                [Globalization.CultureInfo]::InvariantCulture),
            '--app-start-ticks',
            $ApplicationStartTicks.ToString(
                [Globalization.CultureInfo]::InvariantCulture),
            '--silent'
        ) -join ' '
        $process = Start-Process `
            -FilePath $workerExe `
            -ArgumentList $arguments `
            -WorkingDirectory $workerRoot `
            -PassThru
        if ($null -ne $AfterStart) {
            & $AfterStart $process
        }
        $process.WaitForExit()
        $process.Refresh()
        $exitCode = $process.ExitCode
        $process.Dispose()

        $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(90)
        while ((Test-Path -LiteralPath $workerRoot) -and
            [DateTime]::UtcNow -lt $cleanupDeadline) {
            Start-Sleep -Milliseconds 200
        }
        if (Test-Path -LiteralPath $workerRoot) {
            throw "Temporary uninstaller directory was not cleaned: $workerRoot"
        }

        return $exitCode
    }
    finally {
        $resolvedSystemTemp = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\')
        $resolvedWorker = [IO.Path]::GetFullPath($workerRoot)
        if ($resolvedWorker.StartsWith(
            $resolvedSystemTemp + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedWorker) -eq $workerName) {
            Remove-Item -LiteralPath $resolvedWorker -Recurse -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

function Assert-InteractiveArgumentParsing {
    param([string]$UninstallerPath)

    $assembly = [Reflection.Assembly]::LoadFrom($UninstallerPath)
    $programType = $assembly.GetType(
        'FilePromptAIUninstaller.Program',
        $true)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $method = $programType.GetMethod(
        'TryParseInteractiveArguments',
        $flags)
    $checkMethod = $programType.GetMethod(
        'TryParseCheckFromAppArguments',
        $flags)
    if ($null -eq $method) {
        throw 'Strict interactive uninstaller argument parser was not found.'
    }
    if ($null -eq $checkMethod) {
        throw 'Strict app-origin check argument parser was not found.'
    }

    $cases = @(
        [pscustomobject]@{ Arguments = [string[]]@(); Valid = $true; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--from-app', '123'); Valid = $true; Pid = 123 },
        [pscustomobject]@{ Arguments = [string[]]@('--from-app'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--from-app', '0'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--from-app', 'abc'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--from-app', '123', 'extra'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--unknown'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--execute'); Valid = $false; Pid = 0 }
    )
    foreach ($case in $cases) {
        $invokeArguments = New-Object object[] 3
        $invokeArguments[0] = $case.Arguments
        $invokeArguments[1] = 0
        $invokeArguments[2] = ''
        $actual = [bool]$method.Invoke($null, $invokeArguments)
        if ($actual -ne $case.Valid -or
            [int]$invokeArguments[1] -ne $case.Pid) {
            throw "Unexpected interactive parser result for: $($case.Arguments -join ' ')"
        }
        $rejectionMessage = [string]$invokeArguments[2]
        if (-not $actual -and [string]::IsNullOrEmpty($rejectionMessage)) {
            throw 'Rejected uninstaller arguments did not return a safety error.'
        }
    }

    $checkCases = @(
        [pscustomobject]@{ Arguments = [string[]]@('--check-from-app', '123'); Valid = $true; Pid = 123 },
        [pscustomobject]@{ Arguments = [string[]]@('--check-from-app'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--check-from-app', '0'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--check-from-app', '-1'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--check-from-app', 'abc'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--check-from-app', '123', 'extra'); Valid = $false; Pid = 0 },
        [pscustomobject]@{ Arguments = [string[]]@('--CHECK-FROM-APP', '123'); Valid = $false; Pid = 0 }
    )
    foreach ($case in $checkCases) {
        $invokeArguments = New-Object object[] 2
        $invokeArguments[0] = $case.Arguments
        $invokeArguments[1] = 0
        $actual = [bool]$checkMethod.Invoke($null, $invokeArguments)
        if ($actual -ne $case.Valid -or
            [int]$invokeArguments[1] -ne $case.Pid) {
            throw "Unexpected app-origin check parser result for: $($case.Arguments -join ' ')"
        }
    }

    Write-Host 'PASS | uninstaller accepts only strict interactive and app-origin check arguments'
}

function Assert-PreflightFailureReleasesHandles {
    param(
        [string]$PackageRoot,
        [string]$UninstallerPath
    )

    $assembly = [Reflection.Assembly]::LoadFrom($UninstallerPath)
    $programType = $assembly.GetType(
        'FilePromptAIUninstaller.Program',
        $true)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $readManifest = $programType.GetMethod('TryReadManifest', $flags)
    $deleteRelease = $programType.GetMethod('DeleteReleaseFiles', $flags)
    if ($null -eq $readManifest -or $null -eq $deleteRelease) {
        throw 'Uninstaller preflight methods were not found.'
    }

    $manifestArguments = New-Object object[] 4
    $manifestArguments[0] = [IO.Path]::GetFullPath($PackageRoot)
    $manifestArguments[1] = $null
    $manifestArguments[2] = ''
    $manifestArguments[3] = ''
    if (-not [bool]$readManifest.Invoke($null, $manifestArguments)) {
        throw "Preflight fixture manifest is invalid: $($manifestArguments[3])"
    }

    $modifiedPath = Join-Path $PackageRoot 'OFFLINE-README.txt'
    [IO.File]::AppendAllText(
        $modifiedPath,
        "`r`nPREFLIGHT_FAILURE_HANDLE_TEST",
        (New-Object Text.UTF8Encoding($false)))
    $before = Get-PackageSnapshot -Root $PackageRoot

    $deleteArguments = New-Object object[] 3
    $deleteArguments[0] = [IO.Path]::GetFullPath($PackageRoot)
    $deleteArguments[1] = $manifestArguments[1]
    $deleteArguments[2] = $manifestArguments[2]
    $result = $deleteRelease.Invoke($null, $deleteArguments)
    $resultType = $result.GetType()
    if ([int]$resultType.GetField('ModifiedFiles').GetValue($result) -lt 1 -or
        [int]$resultType.GetField('DeletedFiles').GetValue($result) -ne 0) {
        throw 'Modified preflight fixture did not fail before deletion.'
    }
    Assert-PackageSnapshotEqual `
        -Before $before `
        -After (Get-PackageSnapshot -Root $PackageRoot) `
        -Scenario 'Failed in-process uninstall preflight'

    foreach ($file in @(Get-ChildItem `
            -LiteralPath $PackageRoot `
            -File `
            -Recurse `
            -Force)) {
        $exclusive = $null
        try {
            $exclusive = [IO.File]::Open(
                $file.FullName,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::None)
        }
        catch {
            throw "Failed preflight left an open handle on $($file.FullName): $($_.Exception.Message)"
        }
        finally {
            if ($null -ne $exclusive) {
                $exclusive.Dispose()
            }
        }
    }

    Write-Host 'PASS | failed in-process preflight releases every package file handle'
}

function Assert-ManifestLocationBehavior {
    param(
        [string]$UninstallerPath,
        [string]$CompletePackageRoot,
        [string]$MissingManifestRoot
    )

    $check = Start-Process `
        -FilePath (Join-Path $CompletePackageRoot 'Uninstall-FilePromptAI.exe') `
        -ArgumentList '--check' `
        -WorkingDirectory $CompletePackageRoot `
        -PassThru `
        -Wait
    if ($check.ExitCode -ne 0) {
        throw "Complete package root manifest check returned $($check.ExitCode)."
    }

    New-Item -ItemType Directory -Path $MissingManifestRoot -Force |
        Out-Null
    Copy-Item -LiteralPath $UninstallerPath `
        -Destination (Join-Path $MissingManifestRoot 'Uninstall-FilePromptAI.exe') `
        -Force

    $missingCheck = Start-Process `
        -FilePath (Join-Path $MissingManifestRoot 'Uninstall-FilePromptAI.exe') `
        -ArgumentList '--check' `
        -WorkingDirectory $MissingManifestRoot `
        -PassThru `
        -Wait
    if ($missingCheck.ExitCode -ne 3) {
        throw "Standalone uninstaller missing-manifest check returned $($missingCheck.ExitCode) instead of 3."
    }

    $assembly = [Reflection.Assembly]::LoadFrom($UninstallerPath)
    $programType = $assembly.GetType(
        'FilePromptAIUninstaller.Program',
        $true)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $method = $programType.GetMethod('TryReadManifest', $flags)
    if ($null -eq $method) {
        throw 'Uninstaller manifest reader was not found.'
    }

    $resolvedMissingRoot = [IO.Path]::GetFullPath(
        $MissingManifestRoot).TrimEnd('\')
    $invokeArguments = New-Object object[] 4
    $invokeArguments[0] = $resolvedMissingRoot
    $invokeArguments[1] = $null
    $invokeArguments[2] = ''
    $invokeArguments[3] = ''
    if ([bool]$method.Invoke($null, $invokeArguments)) {
        throw 'A copied standalone uninstaller unexpectedly found a manifest.'
    }
    $message = [string]$invokeArguments[3]
    $requiredMessageText = @(
        $resolvedMissingRoot,
        'PACKAGE-CHECKSUMS-SHA256.txt',
        'Uninstall-FilePromptAI.exe')
    foreach ($requiredText in $requiredMessageText) {
        if ($message.IndexOf($requiredText) -lt 0) {
            throw "Missing-manifest error did not include: $requiredText"
        }
    }

    Write-Host 'PASS | manifest check uses the complete package root and reports the actual missing-manifest directory'
}

function Assert-CommitFailureRecovery {
    param(
        [string]$PackageRoot,
        [string]$UninstallerPath
    )

    $assembly = [Reflection.Assembly]::LoadFrom($UninstallerPath)
    $programType = $assembly.GetType(
        'FilePromptAIUninstaller.Program',
        $true)
    $flags = [Reflection.BindingFlags]::NonPublic -bor
        [Reflection.BindingFlags]::Static
    $readManifest = $programType.GetMethod('TryReadManifest', $flags)
    $deleteRelease = $programType.GetMethod('DeleteReleaseFiles', $flags)
    $commitFault = $programType.GetField(
        'commitFailureAfterForTests',
        $flags)
    $rollbackFault = $programType.GetField(
        'rollbackFailureForTests',
        $flags)
    foreach ($required in @(
            $readManifest,
            $deleteRelease,
            $commitFault,
            $rollbackFault)) {
        if ($null -eq $required) {
            throw 'Uninstaller commit-failure test hook is missing.'
        }
    }

    $manifestArguments = New-Object object[] 4
    $manifestArguments[0] = [IO.Path]::GetFullPath($PackageRoot)
    $manifestArguments[1] = $null
    $manifestArguments[2] = ''
    $manifestArguments[3] = ''
    if (-not [bool]$readManifest.Invoke($null, $manifestArguments)) {
        throw "Commit-failure fixture manifest is invalid: $($manifestArguments[3])"
    }

    try {
        $commitFault.SetValue($null, 1)
        $rollbackFault.SetValue($null, $true)
        $deleteArguments = New-Object object[] 3
        $deleteArguments[0] = [IO.Path]::GetFullPath($PackageRoot)
        $deleteArguments[1] = $manifestArguments[1]
        $deleteArguments[2] = $manifestArguments[2]
        $result = $deleteRelease.Invoke($null, $deleteArguments)
    }
    finally {
        $commitFault.SetValue($null, -1)
        $rollbackFault.SetValue($null, $false)
    }

    $resultType = $result.GetType()
    if (-not [bool]$resultType.GetField('PartialDeletion').GetValue($result) -or
        [int]$resultType.GetField('DeletedFiles').GetValue($result) -lt 1 -or
        [int]$resultType.GetField('FailedFiles').GetValue($result) -lt 1) {
        throw 'Injected commit and rollback failure was not reported as a partial deletion.'
    }
    foreach ($requiredPath in @(
            'Uninstall-FilePromptAI.exe',
            'Uninstall-FilePromptAI.exe.config',
            'PACKAGE-CHECKSUMS-SHA256.txt',
            '.FilePromptAI-uninstall-recovery')) {
        if (-not (Test-Path -LiteralPath (
                Join-Path $PackageRoot $requiredPath) -PathType Leaf)) {
            throw "Commit failure did not preserve recovery control: $requiredPath"
        }
    }

    $retryExit = Invoke-UninstallerWorker `
        -PackageRoot $PackageRoot `
        -SourceUninstaller (Join-Path $PackageRoot 'Uninstall-FilePromptAI.exe') `
        -SourceConfig (Join-Path $PackageRoot 'Uninstall-FilePromptAI.exe.config')
    if ($retryExit -ne 0) {
        throw "Recovery-mode uninstall returned $retryExit instead of 0."
    }
    if ((Test-Path -LiteralPath $PackageRoot) -and
        @(Get-ChildItem -LiteralPath $PackageRoot -File -Recurse -Force).Count -ne 0) {
        throw 'Recovery-mode uninstall left package files behind.'
    }

    Write-Host 'PASS | commit and rollback failure preserves a retryable recovery path'
}

if (-not (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
    throw "Missing package staging directory: $stagingRoot"
}

New-Item -ItemType Directory -Path $sandboxRoot -Force | Out-Null
New-Item -ItemType Directory -Path $helperRoot -Force | Out-Null

try {
    if (@(Get-ChildItem -LiteralPath $stagingRoot -Force).Count -eq 0) {
        throw "Package staging directory is empty: $stagingRoot"
    }
    Copy-StagingPackage -Destination $sandboxRoot

    $sentinelPath = Join-Path $sandboxRoot 'keep-user-file.txt'
    [IO.File]::WriteAllText(
        $sentinelPath,
        'This file is not part of the release manifest.',
        (New-Object Text.UTF8Encoding($false)))

    $sourceUninstaller = Join-Path $sandboxRoot 'Uninstall-FilePromptAI.exe'
    $sourceConfig = "$sourceUninstaller.config"
    Assert-InteractiveArgumentParsing -UninstallerPath (
        Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe')
    $manifestErrorRoot = Join-Path $artifactRoot (
        'FilePromptAI-UninstallSmoke-Manifest-' + [Guid]::NewGuid().ToString('N')
    )
    Assert-ManifestLocationBehavior `
        -UninstallerPath (Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe') `
        -CompletePackageRoot $sandboxRoot `
        -MissingManifestRoot $manifestErrorRoot
    $preflightFailureRoot = Join-Path $artifactRoot (
        'FilePromptAI-UninstallSmoke-Preflight-' + [Guid]::NewGuid().ToString('N')
    )
    Copy-StagingPackage -Destination $preflightFailureRoot
    Assert-PreflightFailureReleasesHandles `
        -PackageRoot $preflightFailureRoot `
        -UninstallerPath (Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe')
    $exitCode = Invoke-UninstallerWorker `
        -PackageRoot $sandboxRoot `
        -SourceUninstaller $sourceUninstaller `
        -SourceConfig $sourceConfig
    if ($exitCode -ne 0) {
        throw "Uninstaller worker failed with exit code $exitCode."
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

    $runningRoot = Join-Path $artifactRoot (
        'FilePromptAI-UninstallSmoke-Running-' + [Guid]::NewGuid().ToString('N')
    )
    Copy-StagingPackage -Destination $runningRoot
    $runningBefore = Get-PackageSnapshot -Root $runningRoot
    $runningApp = Start-Process `
        -FilePath (Join-Path $runningRoot 'app\FilePromptAI.exe') `
        -WorkingDirectory (Join-Path $runningRoot 'app') `
        -PassThru
    try {
        Start-Sleep -Milliseconds 1500
        if ($runningApp.HasExited) {
            throw 'The packaged application exited before the running-app uninstall test.'
        }
        $runningExit = Invoke-UninstallerWorker `
            -PackageRoot $runningRoot `
            -SourceUninstaller (Join-Path $runningRoot 'Uninstall-FilePromptAI.exe') `
            -SourceConfig (Join-Path $runningRoot 'Uninstall-FilePromptAI.exe.config')
        if ($runningExit -ne 4) {
            throw "Running-app uninstall returned $runningExit instead of 4."
        }
        Assert-PackageSnapshotEqual `
            -Before $runningBefore `
            -After (Get-PackageSnapshot -Root $runningRoot) `
            -Scenario 'Running-app uninstall preflight'
        Write-Host 'PASS | running application blocks uninstall before any package file changes'
    }
    finally {
        if ($runningApp -and -not $runningApp.HasExited) {
            $runningApp.Kill()
            $runningApp.WaitForExit()
        }
        if ($runningApp) {
            $runningApp.Dispose()
        }
    }

    $waitingRoot = Join-Path $artifactRoot (
        'FilePromptAI-UninstallSmoke-Waiting-' + [Guid]::NewGuid().ToString('N')
    )
    Copy-StagingPackage -Destination $waitingRoot
    $waitingBefore = Get-PackageSnapshot -Root $waitingRoot
    $holdProcess = Start-Process `
        -FilePath (Join-Path $env:SystemRoot 'System32\ping.exe') `
        -ArgumentList '127.0.0.1 -n 30' `
        -WindowStyle Hidden `
        -PassThru
    try {
        $holdProcess.Refresh()
        if ($holdProcess.HasExited) {
            throw 'The application identity fixture exited before the wait test.'
        }
        $holdStartTicks = $holdProcess.StartTime.ToUniversalTime().Ticks
        $waitingExit = Invoke-UninstallerWorker `
            -PackageRoot $waitingRoot `
            -SourceUninstaller (Join-Path $waitingRoot 'Uninstall-FilePromptAI.exe') `
            -SourceConfig (Join-Path $waitingRoot 'Uninstall-FilePromptAI.exe.config') `
            -ApplicationProcessId $holdProcess.Id `
            -ApplicationStartTicks $holdStartTicks `
            -AfterStart {
                param($worker)
                Start-Sleep -Milliseconds 1000
                $worker.Refresh()
                if ($worker.HasExited) {
                    throw 'Uninstaller worker did not wait for the original application PID.'
                }
                Assert-PackageSnapshotEqual `
                    -Before $waitingBefore `
                    -After (Get-PackageSnapshot -Root $waitingRoot) `
                    -Scenario 'Application-exit wait'
                $holdProcess.Kill()
                $holdProcess.WaitForExit()
            }
        if ($waitingExit -ne 0) {
            throw "Application-exit wait uninstall returned $waitingExit."
        }
        if ((Test-Path -LiteralPath $waitingRoot) -and
            @(Get-ChildItem -LiteralPath $waitingRoot -File -Recurse -Force).Count -ne 0) {
            throw 'Application-exit wait uninstall left packaged files.'
        }
        Write-Host 'PASS | worker preserves the full package until the original application exits'
    }
    finally {
        if ($holdProcess -and -not $holdProcess.HasExited) {
            $holdProcess.Kill()
            $holdProcess.WaitForExit()
        }
        if ($holdProcess) {
            $holdProcess.Dispose()
        }
    }

    $lockedRoot = Join-Path $artifactRoot (
        'FilePromptAI-UninstallSmoke-Locked-' + [Guid]::NewGuid().ToString('N')
    )
    Copy-StagingPackage -Destination $lockedRoot
    $lockedBefore = Get-PackageSnapshot -Root $lockedRoot
    $lockedPath = Join-Path $lockedRoot 'OFFLINE-README.txt'
    $lockedStream = [IO.File]::Open(
        $lockedPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    try {
        $lockedExit = Invoke-UninstallerWorker `
            -PackageRoot $lockedRoot `
            -SourceUninstaller (Join-Path $lockedRoot 'Uninstall-FilePromptAI.exe') `
            -SourceConfig (Join-Path $lockedRoot 'Uninstall-FilePromptAI.exe.config')
        if ($lockedExit -ne 4) {
            throw "Locked-file uninstall returned $lockedExit instead of 4."
        }
        $lockedStream.Dispose()
        $lockedStream = $null
        Assert-PackageSnapshotEqual `
            -Before $lockedBefore `
            -After (Get-PackageSnapshot -Root $lockedRoot) `
            -Scenario 'Locked-file uninstall preflight'
        Write-Host 'PASS | occupied package file blocks uninstall before any package file changes'
    }
    finally {
        if ($lockedStream) {
            $lockedStream.Dispose()
        }
    }

    $retryExit = Invoke-UninstallerWorker `
        -PackageRoot $lockedRoot `
        -SourceUninstaller (Join-Path $lockedRoot 'Uninstall-FilePromptAI.exe') `
        -SourceConfig (Join-Path $lockedRoot 'Uninstall-FilePromptAI.exe.config')
    if ($retryExit -ne 0) {
        throw "Uninstall after releasing occupied file returned $retryExit."
    }
    if ((Test-Path -LiteralPath $lockedRoot) -and
        @(Get-ChildItem -LiteralPath $lockedRoot -File -Recurse -Force).Count -ne 0) {
        throw 'Uninstall retry left packaged files after the occupied file was released.'
    }
    Write-Host 'PASS | uninstall succeeds after the occupied file is released'

    $commitFailureRoot = Join-Path $artifactRoot (
        'FilePromptAI-UninstallSmoke-Commit-' + [Guid]::NewGuid().ToString('N')
    )
    Copy-StagingPackage -Destination $commitFailureRoot
    Assert-CommitFailureRecovery `
        -PackageRoot $commitFailureRoot `
        -UninstallerPath (Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe')
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

    foreach ($extraRoot in @(
        $runningRoot,
        $lockedRoot,
        $waitingRoot,
        $manifestErrorRoot,
        $commitFailureRoot,
        $preflightFailureRoot)) {
        if ([string]::IsNullOrEmpty($extraRoot)) {
            continue
        }
        $resolvedExtra = [IO.Path]::GetFullPath($extraRoot)
        if ($resolvedExtra.StartsWith(
            $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedExtra).StartsWith(
                'FilePromptAI-UninstallSmoke-',
                [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedExtra -Recurse -Force `
                -ErrorAction SilentlyContinue
        }
    }
}
