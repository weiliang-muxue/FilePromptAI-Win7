param(
    [string]$Version = '1.18'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$uninstallerPath = Join-Path $stagingRoot 'Uninstall-FilePromptAI.exe'
$compilerRoot = 'C:\Windows\Microsoft.NET\Framework\v3.5'
$referenceRoot = 'C:\Windows\Microsoft.NET\Framework\v2.0.50727'
$compiler = Join-Path $compilerRoot 'csc.exe'
$testExecutable = Join-Path $artifactRoot 'UninstallerUserDataSmokeTest.exe'

foreach ($required in @(
        $compiler,
        $uninstallerPath,
        (Join-Path $testRoot 'UninstallerUserDataSmokeTest.cs'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing uninstaller user-data test dependency: $required"
    }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$compilerArguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/warn:4',
    "/out:$testExecutable",
    "/reference:$(Join-Path $referenceRoot 'System.dll')",
    "/reference:$(Join-Path $referenceRoot 'System.Windows.Forms.dll')",
    (Join-Path $testRoot 'UninstallerUserDataSmokeTest.cs')
)
& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Uninstaller user-data test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable $uninstallerPath
if ($LASTEXITCODE -ne 0) {
    throw "Uninstaller user-data smoke test failed with exit code $LASTEXITCODE."
}
