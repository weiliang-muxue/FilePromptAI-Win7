param(
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testExecutable = Join-Path $artifactRoot 'ApiHardeningSmokeTest.exe'

if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $projectRoot 'dist\FilePromptAI.exe'
}

if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$testExecutable",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    (Join-Path $testRoot 'ApiHardeningSmokeTest.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "API hardening test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable ([IO.Path]::GetFullPath($ApplicationPath))
if ($LASTEXITCODE -ne 0) {
    throw "API hardening test failed with exit code $LASTEXITCODE."
}
