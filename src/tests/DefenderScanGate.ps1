#Requires -Version 5.1

Set-StrictMode -Version 2.0

function Get-FilePromptDefenderProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function ConvertTo-FilePromptDefenderBoolean {
    param([object]$Value)

    $parsed = $false
    if ($null -ne $Value -and
        [bool]::TryParse(
            $Value.ToString(),
            [ref]$parsed)) {
        return [pscustomobject]@{
            Known = $true
            Value = $parsed
        }
    }
    return [pscustomobject]@{
        Known = $false
        Value = $false
    }
}

function ConvertTo-FilePromptDefenderDetectionMap {
    param([object[]]$Detections)

    $map = @{}
    foreach ($detection in @($Detections)) {
        if ($null -eq $detection) {
            throw 'Microsoft Defender returned a null detection record.'
        }
        $idValue = Get-FilePromptDefenderProperty `
            -InputObject $detection `
            -Name 'DetectionID'
        $id = if ($null -eq $idValue) { '' } else { $idValue.ToString() }
        if ([string]::IsNullOrWhiteSpace($id)) {
            throw 'Microsoft Defender returned a detection without DetectionID.'
        }
        if ($map.ContainsKey($id)) {
            throw "Microsoft Defender returned duplicate DetectionID: $id"
        }
        $map[$id] = $detection
    }
    return $map
}

function Get-FilePromptDefenderGateState {
    param(
        [object[]]$BaselineDetections,
        [object[]]$CurrentDetections,
        [object[]]$CurrentThreats
    )

    $baselineMap = ConvertTo-FilePromptDefenderDetectionMap `
        -Detections $BaselineDetections
    $currentMap = ConvertTo-FilePromptDefenderDetectionMap `
        -Detections $CurrentDetections
    $newDetectionIds = @(
        $currentMap.Keys |
            Where-Object { -not $baselineMap.ContainsKey($_) } |
            Sort-Object
    )

    $activeThreats = @()
    foreach ($threat in @($CurrentThreats)) {
        if ($null -eq $threat) {
            $activeThreats += [pscustomobject]@{
                Source = 'threat'
                Identifier = 'missing'
                ThreatId = ''
                Reason = 'Microsoft Defender returned a null threat record.'
                Record = $null
            }
            continue
        }
        $threatIdValue = Get-FilePromptDefenderProperty `
            -InputObject $threat `
            -Name 'ThreatID'
        $threatId = if ($null -eq $threatIdValue) {
            'unknown'
        }
        else {
            $threatIdValue.ToString()
        }
        $active = ConvertTo-FilePromptDefenderBoolean `
            -Value (Get-FilePromptDefenderProperty `
                -InputObject $threat `
                -Name 'IsActive')
        if (-not $active.Known -or $active.Value) {
            $activeThreats += [pscustomobject]@{
                Source = 'threat'
                Identifier = $threatId
                ThreatId = $threatId
                Reason = if ($active.Known) {
                    'IsActive is true.'
                }
                else {
                    'IsActive is unavailable.'
                }
                Record = $threat
            }
        }
    }

    return [pscustomobject]@{
        DetectionMap = $currentMap
        DetectionCount = $currentMap.Count
        NewDetectionIds = @($newDetectionIds)
        NewDetectionCount = $newDetectionIds.Count
        ActiveThreats = @($activeThreats)
        ActiveThreatCount = $activeThreats.Count
    }
}

function Assert-FilePromptDefenderGateState {
    param(
        [Parameter(Mandatory = $true)]
        [object]$State,
        [Parameter(Mandatory = $true)]
        [string]$Stage,
        [switch]$RequireNoNewDetections
    )

    $failures = @()
    if ($RequireNoNewDetections -and $State.NewDetectionCount -ne 0) {
        $failures += 'newDetectionIds=' + $State.NewDetectionCount
    }
    if ($State.ActiveThreatCount -ne 0) {
        $failures += 'activeThreats=' + $State.ActiveThreatCount
    }
    if ($failures.Count -ne 0) {
        throw (
            "Microsoft Defender $Stage gate failed: " +
            ($failures -join ' ') + '.')
    }
}
