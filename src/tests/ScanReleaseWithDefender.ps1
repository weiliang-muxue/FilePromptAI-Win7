#Requires -Version 5.1

param(
    [Parameter(Mandatory = $true)]
    [string[]]$ScanPath,
    [ValidateRange(5, 300)]
    [int]$StabilizationSeconds = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-DetectionMap {
    $map = @{}
    foreach ($detection in @(Get-MpThreatDetection)) {
        $id = $detection.DetectionID.ToString()
        if (-not [string]::IsNullOrEmpty($id)) {
            $map[$id] = $detection
        }
    }
    return $map
}

$status = Get-MpComputerStatus
if (-not $status.AMServiceEnabled -or
    -not $status.AntivirusEnabled -or
    -not $status.RealTimeProtectionEnabled) {
    throw 'Microsoft Defender Antivirus and real-time protection must be enabled.'
}
if ($null -eq $status.AntivirusSignatureLastUpdated -or
    $status.AntivirusSignatureVersion -notmatch '^\d+(?:\.\d+){2,3}$') {
    throw 'Microsoft Defender signature status is unavailable.'
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

$before = Get-DetectionMap
Write-Host (
    'DEFENDER BASELINE | detections=' + $before.Count +
    ' | signatures=' + $status.AntivirusSignatureVersion +
    ' | updated=' + $status.AntivirusSignatureLastUpdated.ToString('o'))

foreach ($resolved in $resolvedPaths) {
    Write-Host "DEFENDER SCAN | $resolved"
    Start-MpScan -ScanType CustomScan -ScanPath $resolved
}

$deadline = [DateTime]::UtcNow.AddSeconds($StabilizationSeconds)
do {
    Start-Sleep -Milliseconds 500
    $after = Get-DetectionMap
} while ([DateTime]::UtcNow -lt $deadline)

$newIds = @($after.Keys | Where-Object { -not $before.ContainsKey($_) })
if ($newIds.Count -ne 0) {
    foreach ($id in $newIds) {
        $detection = $after[$id]
        [Console]::Error.WriteLine(
            'DEFENDER NEW DETECTION | id=' + $id +
            ' | threat=' + $detection.ThreatID +
            ' | time=' + $detection.InitialDetectionTime.ToString('o') +
            ' | resources=' + ($detection.Resources -join ' | '))
    }
    throw "Microsoft Defender reported $($newIds.Count) new detection(s)."
}

Write-Host (
    'PASS | Microsoft Defender custom scans | paths=' +
    $resolvedPaths.Count + ' | newDetections=0 | historicalDetections=' +
    $after.Count)
