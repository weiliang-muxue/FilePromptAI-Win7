#Requires -Version 5.1

param(
    [Parameter(Mandatory = $true)]
    [string[]]$ScanPath,
    [ValidateRange(5, 300)]
    [int]$StabilizationSeconds = 15,
    [ValidateRange(15, 600)]
    [int]$MaximumWaitSeconds = 60,
    [ValidateRange(2, 10)]
    [int]$RequiredStableSnapshots = 3,
    [ValidateRange(0, 30)]
    [int]$MaximumSignatureAgeDays = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$gateScript = Join-Path $testRoot 'DefenderScanGate.ps1'
if (-not (Test-Path -LiteralPath $gateScript -PathType Leaf)) {
    throw "The Defender gate helper is missing: $gateScript"
}
. $gateScript

function Get-DefenderSnapshot {
    $detections = @(Get-MpThreatDetection)
    $threats = @(Get-MpThreat)
    return [pscustomobject]@{
        Detections = @($detections)
        Threats = @($threats)
    }
}

function Get-DefenderGateFingerprint {
    param([object]$State)

    $detectionIds = @($State.DetectionMap.Keys | Sort-Object)
    $activeThreats = @(
        $State.ActiveThreats |
            ForEach-Object {
                $_.Source + ':' + $_.Identifier + ':' + $_.Reason
            } |
            Sort-Object
    )
    return (
        ($detectionIds -join [char]31) + [char]30 +
        ($activeThreats -join [char]31))
}

function Test-PathInside {
    param(
        [string]$Candidate,
        [string]$Container
    )

    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $containerPath = [IO.Path]::GetFullPath($Container).TrimEnd('\')
    return (
        [string]::Equals(
            $candidatePath,
            $containerPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith(
            $containerPath + '\',
            [StringComparison]::OrdinalIgnoreCase))
}

function Assert-DefenderExclusionsDoNotCoverTargets {
    param(
        [object]$Preference,
        [string[]]$Targets
    )

    $scopeItems = New-Object Collections.Generic.List[string]
    foreach ($target in $Targets) {
        [void]$scopeItems.Add($target)
        if (Test-Path -LiteralPath $target -PathType Container) {
            foreach ($item in @(Get-ChildItem -LiteralPath $target -Recurse -Force)) {
                [void]$scopeItems.Add($item.FullName)
            }
        }
    }

    foreach ($rawExclusion in @($Preference.ExclusionPath)) {
        if ([string]::IsNullOrWhiteSpace([string]$rawExclusion)) {
            continue
        }
        $expanded = [Environment]::ExpandEnvironmentVariables(
            $rawExclusion.ToString())
        if ($expanded.IndexOfAny([char[]]'*?[') -ge 0) {
            foreach ($item in $scopeItems) {
                if ($item -like $expanded) {
                    throw "Microsoft Defender path exclusion covers a scan target: $rawExclusion"
                }
            }
            continue
        }
        try {
            $excludedPath = [IO.Path]::GetFullPath($expanded)
        }
        catch {
            throw "Microsoft Defender returned an invalid path exclusion: $rawExclusion"
        }
        foreach ($target in $Targets) {
            if ((Test-PathInside -Candidate $target -Container $excludedPath) -or
                (Test-PathInside -Candidate $excludedPath -Container $target)) {
                throw "Microsoft Defender path exclusion overlaps a scan target: $rawExclusion"
            }
        }
    }

    $excludedExtensions = @{}
    foreach ($rawExtension in @($Preference.ExclusionExtension)) {
        $extension = ([string]$rawExtension).Trim().TrimStart('*').TrimStart('.')
        if (-not [string]::IsNullOrWhiteSpace($extension)) {
            $excludedExtensions[$extension.ToLowerInvariant()] = $true
        }
    }
    if ($excludedExtensions.Count -ne 0) {
        foreach ($item in $scopeItems) {
            if (-not (Test-Path -LiteralPath $item -PathType Leaf)) {
                continue
            }
            $extension = [IO.Path]::GetExtension($item).TrimStart('.').ToLowerInvariant()
            if ($excludedExtensions.ContainsKey($extension)) {
                throw "Microsoft Defender extension exclusion covers a scan target file: .$extension"
            }
        }
    }

    foreach ($rawProcess in @($Preference.ExclusionProcess)) {
        if ([string]::IsNullOrWhiteSpace([string]$rawProcess)) {
            continue
        }
        $expanded = [Environment]::ExpandEnvironmentVariables(
            $rawProcess.ToString())
        $leaf = Split-Path -Leaf $expanded
        foreach ($item in $scopeItems) {
            if ((Test-Path -LiteralPath $item -PathType Leaf) -and
                [string]::Equals(
                    (Split-Path -Leaf $item),
                    $leaf,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Microsoft Defender process exclusion names a scan target file: $rawProcess"
            }
        }
    }
}

function Write-UnresolvedThreats {
    param(
        [string]$Prefix,
        [object[]]$Records
    )

    foreach ($item in @($Records)) {
        $record = $item.Record
        $resources = if ($null -ne $record -and
            $null -ne $record.PSObject.Properties['Resources']) {
            @($record.Resources) -join ' | '
        }
        else {
            ''
        }
        [Console]::Error.WriteLine(
            $Prefix + ' | source=' + $item.Source +
            ' | id=' + $item.Identifier +
            ' | threat=' + $item.ThreatId +
            ' | reason=' + $item.Reason +
            ' | resources=' + $resources)
    }
}

$status = Get-MpComputerStatus
if (-not $status.AMServiceEnabled -or
    -not $status.AntivirusEnabled -or
    -not $status.RealTimeProtectionEnabled) {
    throw 'Microsoft Defender Antivirus and real-time protection must be enabled.'
}
if ([string]::IsNullOrWhiteSpace($status.AMEngineVersion) -or
    $status.AMEngineVersion -notmatch '^\d+(?:\.\d+){2,3}$' -or
    $null -eq $status.AntivirusSignatureLastUpdated -or
    $status.AntivirusSignatureVersion -notmatch '^\d+(?:\.\d+){2,3}$') {
    throw 'Microsoft Defender engine or signature status is unavailable.'
}
$signaturesOutOfDate = ConvertTo-FilePromptDefenderBoolean `
    -Value $status.DefenderSignaturesOutOfDate
$signatureAge = 0
if (-not $signaturesOutOfDate.Known -or
    $signaturesOutOfDate.Value -or
    $null -eq $status.AntivirusSignatureAge -or
    -not [int]::TryParse(
        $status.AntivirusSignatureAge.ToString(),
        [ref]$signatureAge) -or
    $signatureAge -lt 0 -or
    $signatureAge -gt $MaximumSignatureAgeDays) {
    throw (
        'Microsoft Defender signatures are unavailable or too old. ' +
        "Maximum age: $MaximumSignatureAgeDays day(s).")
}

$preference = Get-MpPreference
$archiveScanningDisabled = ConvertTo-FilePromptDefenderBoolean `
    -Value $preference.DisableArchiveScanning
if (-not $archiveScanningDisabled.Known -or
    $archiveScanningDisabled.Value) {
    throw 'Microsoft Defender archive scanning must be enabled.'
}

$resolvedPaths = New-Object Collections.Generic.List[string]
foreach ($path in $ScanPath) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw 'A Defender scan path is empty.'
    }
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "A Defender scan path does not exist: $resolved"
    }
    [void]$resolvedPaths.Add($resolved)
}
Assert-DefenderExclusionsDoNotCoverTargets `
    -Preference $preference `
    -Targets $resolvedPaths.ToArray()

if ($MaximumWaitSeconds -lt
    ($StabilizationSeconds + $RequiredStableSnapshots - 1)) {
    throw (
        'MaximumWaitSeconds must allow the minimum stabilization period ' +
        'and the required stable snapshots.')
}

$before = Get-DefenderSnapshot
$beforeState = Get-FilePromptDefenderGateState `
    -BaselineDetections $before.Detections `
    -CurrentDetections $before.Detections `
    -CurrentThreats $before.Threats
Write-Host (
    'DEFENDER BASELINE | detections=' + $beforeState.DetectionCount +
    ' | activeThreats=' + $beforeState.ActiveThreatCount +
    ' | engine=' + $status.AMEngineVersion +
    ' | signatures=' + $status.AntivirusSignatureVersion +
    ' | signatureAgeDays=' + $signatureAge +
    ' | updated=' + $status.AntivirusSignatureLastUpdated.ToString('o'))
if ($beforeState.ActiveThreatCount -ne 0) {
    Write-UnresolvedThreats `
        -Prefix 'DEFENDER ACTIVE BASELINE THREAT' `
        -Records $beforeState.ActiveThreats
}
Assert-FilePromptDefenderGateState `
    -State $beforeState `
    -Stage 'baseline'

foreach ($resolved in $resolvedPaths) {
    Write-Host "DEFENDER SCAN | $resolved"
    Start-MpScan -ScanType CustomScan -ScanPath $resolved
}

$observationStart = [DateTime]::UtcNow
$minimumDeadline = $observationStart.AddSeconds($StabilizationSeconds)
$maximumDeadline = $observationStart.AddSeconds($MaximumWaitSeconds)
$stableSnapshots = 0
$lastFingerprint = $null
while ($true) {
    Start-Sleep -Seconds 1
    $after = Get-DefenderSnapshot
    $afterState = Get-FilePromptDefenderGateState `
        -BaselineDetections $before.Detections `
        -CurrentDetections $after.Detections `
        -CurrentThreats $after.Threats
    $fingerprint = Get-DefenderGateFingerprint -State $afterState
    if ($null -ne $lastFingerprint -and
        [string]::Equals(
            $fingerprint,
            $lastFingerprint,
            [StringComparison]::Ordinal)) {
        $stableSnapshots++
    }
    else {
        $stableSnapshots = 1
        $lastFingerprint = $fingerprint
    }

    $now = [DateTime]::UtcNow
    if ($now -ge $minimumDeadline -and
        $stableSnapshots -ge $RequiredStableSnapshots) {
        break
    }
    if ($now -ge $maximumDeadline) {
        throw (
            'Microsoft Defender results did not stabilize before the ' +
            "$MaximumWaitSeconds-second timeout.")
    }
}
if ($afterState.NewDetectionCount -ne 0) {
    foreach ($id in $afterState.NewDetectionIds) {
        $detection = $afterState.DetectionMap[$id]
        [Console]::Error.WriteLine(
            'DEFENDER NEW DETECTION | id=' + $id +
            ' | threat=' + $detection.ThreatID +
            ' | time=' + $detection.InitialDetectionTime.ToString('o') +
            ' | resources=' + ($detection.Resources -join ' | '))
    }
}
if ($afterState.ActiveThreatCount -ne 0) {
    Write-UnresolvedThreats `
        -Prefix 'DEFENDER ACTIVE POST-SCAN THREAT' `
        -Records $afterState.ActiveThreats
}
Assert-FilePromptDefenderGateState `
    -State $afterState `
    -Stage 'post-scan' `
    -RequireNoNewDetections

Write-Host (
    'PASS | Microsoft Defender custom scans | paths=' +
    $resolvedPaths.Count + ' | newDetections=0 | newDetectionIds=0' +
    ' | historicalDetections=' +
    $afterState.DetectionCount + ' | activeThreats=0' +
    ' | stableSnapshots=' + $stableSnapshots +
    ' | engine=' + $status.AMEngineVersion +
    ' | signatures=' + $status.AntivirusSignatureVersion +
    ' | signatureAgeDays=' + $signatureAge +
    ' | updated=' + $status.AntivirusSignatureLastUpdated.ToString('o'))
