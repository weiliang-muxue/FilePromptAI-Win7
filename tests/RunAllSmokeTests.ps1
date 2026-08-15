param(
    [string]$Version = '1.11'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$buildScript = Join-Path $projectRoot 'build.ps1'
$packageBuildScript = Join-Path $projectRoot 'build-offline-package.ps1'

& powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript
if ($LASTEXITCODE -ne 0) {
    throw "Application build failed with exit code $LASTEXITCODE."
}

$scripts = @(
    'RunApiSmokeTest.ps1',
    'RunApiHardeningSmokeTest.ps1',
    'RunNetworkReliabilitySmokeTest.ps1',
    'RunToolLoopSmokeTest.ps1',
    'RunExtensionSettingsSmokeTest.ps1',
    'RunModelProfileSmokeTest.ps1',
    'RunMcpRuntimeSmokeTest.ps1',
    'RunConversationContextBudgetSmokeTest.ps1',
    'RunConversationStoreSmokeTest.ps1',
    'RunConversationBackupSmokeTest.ps1',
    'RunExportSmokeTest.ps1',
    'RunPresentationMindMapSmokeTest.ps1',
    'RunXMindExportSmokeTest.ps1',
    'RunExtractorSmokeTest.ps1',
    'RunExtractorHardeningSmokeTest.ps1',
    'RunMarkdownRendererSmokeTest.ps1',
    'RunUiStateSmokeTest.ps1',
    'LaunchSmokeTest.ps1'
)

foreach ($name in $scripts) {
    $script = Join-Path $testRoot $name
    Write-Host "RUN $name"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE."
    }
}

Write-Host "RUN build-offline-package.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass `
    -File $packageBuildScript `
    -Version $Version
if ($LASTEXITCODE -ne 0) {
    throw "Offline package build failed with exit code $LASTEXITCODE."
}

$packageScripts = @(
    'VerifyOfflinePackage.ps1',
    'RunUninstallerSmokeTest.ps1',
    'RunUninstallerSecuritySmokeTest.ps1'
)
foreach ($name in $packageScripts) {
    $script = Join-Path $testRoot $name
    Write-Host "RUN $name"
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File $script `
        -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE."
    }
}

$suiteCount = $scripts.Count + $packageScripts.Count
Write-Host "PASS | all smoke tests ($suiteCount suites + offline package build)"
