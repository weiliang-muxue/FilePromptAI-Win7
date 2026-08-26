#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$gateScript = Join-Path $testRoot 'DefenderScanGate.ps1'
if (-not (Test-Path -LiteralPath $gateScript -PathType Leaf)) {
    throw "The Defender gate helper is missing: $gateScript"
}
. $gateScript

function New-Detection {
    param(
        [string]$Id,
        [int]$ThreatId,
        [int]$ThreatStatusId,
        [bool]$ActionSuccess
    )

    return [pscustomobject]@{
        DetectionID = $Id
        ThreatID = $ThreatId
        ThreatStatusID = $ThreatStatusId
        ActionSuccess = $ActionSuccess
        InitialDetectionTime = [DateTime]::UtcNow
        Resources = @('file:_fixture')
    }
}

function New-Threat {
    param(
        [int]$ThreatId,
        [bool]$IsActive
    )

    return [pscustomobject]@{
        ThreatID = $ThreatId
        IsActive = $IsActive
    }
}

function Assert-GateRejected {
    param(
        [object]$State,
        [string]$Stage,
        [switch]$RequireNoNewDetections,
        [string]$ExpectedText
    )

    try {
        Assert-FilePromptDefenderGateState `
            -State $State `
            -Stage $Stage `
            -RequireNoNewDetections:$RequireNoNewDetections
    }
    catch {
        if ($_.Exception.Message.IndexOf(
                $ExpectedText,
                [StringComparison]::Ordinal) -lt 0) {
            throw (
                "The $Stage gate failed for the wrong reason: " +
                $_.Exception.Message)
        }
        return
    }
    throw "The $Stage gate unexpectedly allowed $ExpectedText."
}

function Assert-StateConstructionRejected {
    param(
        [object[]]$BaselineDetections,
        [object[]]$CurrentDetections,
        [object[]]$CurrentThreats,
        [string]$ExpectedText
    )

    try {
        Get-FilePromptDefenderGateState `
            -BaselineDetections $BaselineDetections `
            -CurrentDetections $CurrentDetections `
            -CurrentThreats $CurrentThreats | Out-Null
    }
    catch {
        if ($_.Exception.Message.IndexOf(
                $ExpectedText,
                [StringComparison]::Ordinal) -lt 0) {
            throw (
                'Defender state construction failed for the wrong reason: ' +
                $_.Exception.Message)
        }
        return
    }
    throw "Defender state construction unexpectedly allowed $ExpectedText."
}

$empty = Get-FilePromptDefenderGateState `
    -BaselineDetections @() `
    -CurrentDetections @() `
    -CurrentThreats @()
Assert-FilePromptDefenderGateState `
    -State $empty `
    -Stage 'empty-history' `
    -RequireNoNewDetections

$resolved = New-Detection `
    -Id 'resolved-history' `
    -ThreatId 1001 `
    -ThreatStatusId 4 `
    -ActionSuccess $true
$historical = Get-FilePromptDefenderGateState `
    -BaselineDetections @($resolved) `
    -CurrentDetections @($resolved) `
    -CurrentThreats @((New-Threat -ThreatId 1001 -IsActive $false))
if ($historical.ActiveThreatCount -ne 0 -or
    $historical.NewDetectionCount -ne 0) {
    throw 'Resolved historical Defender detections must be allowed.'
}
Assert-FilePromptDefenderGateState `
    -State $historical `
    -Stage 'resolved-history' `
    -RequireNoNewDetections

$legacyResolvedStatuses = @(8, 104, 106)
foreach ($legacyStatus in $legacyResolvedStatuses) {
    $legacyResolved = New-Detection `
        -Id ('resolved-history-' + $legacyStatus) `
        -ThreatId (1100 + $legacyStatus) `
        -ThreatStatusId $legacyStatus `
        -ActionSuccess $true
    $legacyState = Get-FilePromptDefenderGateState `
        -BaselineDetections @($legacyResolved) `
        -CurrentDetections @($legacyResolved) `
        -CurrentThreats @((New-Threat `
            -ThreatId (1100 + $legacyStatus) `
            -IsActive $false))
    Assert-FilePromptDefenderGateState `
        -State $legacyState `
        -Stage ('resolved-history-' + $legacyStatus) `
        -RequireNoNewDetections
}

$activeBaselineDetection = New-Detection `
    -Id 'active-at-baseline' `
    -ThreatId 1002 `
    -ThreatStatusId 1 `
    -ActionSuccess $false
$activeBaseline = Get-FilePromptDefenderGateState `
    -BaselineDetections @($activeBaselineDetection) `
    -CurrentDetections @($activeBaselineDetection) `
    -CurrentThreats @((New-Threat -ThreatId 1002 -IsActive $true))
if ($activeBaseline.ActiveThreatCount -eq 0) {
    throw 'An active baseline Defender threat must be rejected.'
}
Assert-GateRejected `
    -State $activeBaseline `
    -Stage 'baseline' `
    -ExpectedText 'activeThreats='

foreach ($invalidActiveValue in @($null, '', 'not-a-boolean')) {
    $invalidThreat = [pscustomobject]@{
        ThreatID = 1004
        IsActive = $invalidActiveValue
    }
    $invalidActiveState = Get-FilePromptDefenderGateState `
        -BaselineDetections @($resolved) `
        -CurrentDetections @($resolved) `
        -CurrentThreats @($invalidThreat)
    Assert-GateRejected `
        -State $invalidActiveState `
        -Stage 'invalid-is-active' `
        -ExpectedText 'activeThreats='
}

$missingActiveState = Get-FilePromptDefenderGateState `
    -BaselineDetections @($resolved) `
    -CurrentDetections @($resolved) `
    -CurrentThreats @([pscustomobject]@{ ThreatID = 1005 })
Assert-GateRejected `
    -State $missingActiveState `
    -Stage 'missing-is-active' `
    -ExpectedText 'activeThreats='

$nullThreatState = Get-FilePromptDefenderGateState `
    -BaselineDetections @($resolved) `
    -CurrentDetections @($resolved) `
    -CurrentThreats @($null)
Assert-GateRejected `
    -State $nullThreatState `
    -Stage 'null-threat' `
    -ExpectedText 'activeThreats='

$newResolved = New-Detection `
    -Id 'new-after-scan' `
    -ThreatId 1003 `
    -ThreatStatusId 3 `
    -ActionSuccess $true
$newAfterScan = Get-FilePromptDefenderGateState `
    -BaselineDetections @($resolved) `
    -CurrentDetections @($resolved, $newResolved) `
    -CurrentThreats @(
        (New-Threat -ThreatId 1001 -IsActive $false),
        (New-Threat -ThreatId 1003 -IsActive $false)
    )
if ($newAfterScan.NewDetectionCount -ne 1 -or
    $newAfterScan.NewDetectionIds[0] -ne 'new-after-scan' -or
    $newAfterScan.ActiveThreatCount -ne 0) {
    throw 'A new post-scan DetectionID must be rejected even after remediation.'
}
Assert-GateRejected `
    -State $newAfterScan `
    -Stage 'post-scan-new-detection' `
    -RequireNoNewDetections `
    -ExpectedText 'newDetectionIds=1'

$activeAfterDetection = New-Detection `
    -Id 'resolved-history' `
    -ThreatId 1001 `
    -ThreatStatusId 1 `
    -ActionSuccess $false
$activeAfterScan = Get-FilePromptDefenderGateState `
    -BaselineDetections @($resolved) `
    -CurrentDetections @($activeAfterDetection) `
    -CurrentThreats @((New-Threat -ThreatId 1001 -IsActive $true))
if ($activeAfterScan.NewDetectionCount -ne 0 -or
    $activeAfterScan.ActiveThreatCount -eq 0) {
    throw 'An active post-scan threat with an old DetectionID must be rejected.'
}
Assert-GateRejected `
    -State $activeAfterScan `
    -Stage 'post-scan-active-threat' `
    -RequireNoNewDetections `
    -ExpectedText 'activeThreats='

$missingDetectionId = [pscustomobject]@{
    ThreatID = 1006
    ThreatStatusID = 4
    ActionSuccess = $true
}
Assert-StateConstructionRejected `
    -BaselineDetections @() `
    -CurrentDetections @($missingDetectionId) `
    -CurrentThreats @() `
    -ExpectedText 'without DetectionID'

$duplicateDetectionA = New-Detection `
    -Id 'duplicate-id' `
    -ThreatId 1007 `
    -ThreatStatusId 4 `
    -ActionSuccess $true
$duplicateDetectionB = New-Detection `
    -Id 'duplicate-id' `
    -ThreatId 1008 `
    -ThreatStatusId 4 `
    -ActionSuccess $true
Assert-StateConstructionRejected `
    -BaselineDetections @() `
    -CurrentDetections @($duplicateDetectionA, $duplicateDetectionB) `
    -CurrentThreats @() `
    -ExpectedText 'duplicate DetectionID'

Write-Host (
    'PASS | Defender scan gate logic | resolvedHistory=allowed' +
    ' | emptyHistory=allowed | malformedRecords=rejected' +
    ' | legacyResolvedStatuses=8,104,106' +
    ' | activeBaseline=rejected | newDetection=rejected' +
    ' | activeAfterScan=rejected')
