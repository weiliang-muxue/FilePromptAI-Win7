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
$testSource = Join-Path $testRoot 'ApiSmokeTest.cs'
$testExecutable = Join-Path $artifactRoot 'ApiSmokeTest.exe'

if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $projectRoot 'dist\FilePrompt.exe'
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler was not found.'
}

if (-not (Test-Path -LiteralPath $ApplicationPath)) {
    throw "Application was not found: $ApplicationPath"
}

if (-not (Test-Path -LiteralPath $artifactRoot)) {
    New-Item -ItemType Directory -Path $artifactRoot | Out-Null
}

$compilerArguments = @(
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
    "/reference:$(Join-Path $frameworkRoot 'System.Web.Extensions.dll')",
    $testSource
)

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "API smoke test compilation failed with exit code $LASTEXITCODE."
}

& $testExecutable ([System.IO.Path]::GetFullPath($ApplicationPath))
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) {
    throw "API smoke test failed with exit code $testExitCode."
}

Write-Host 'PASS | API smoke test completed.'
