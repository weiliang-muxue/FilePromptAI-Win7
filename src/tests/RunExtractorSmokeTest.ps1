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
$testExecutable = Join-Path $artifactRoot 'ExtractorSmokeTest.exe'

if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $projectRoot 'dist\FilePromptAI.exe'
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
    "/reference:$(Join-Path $frameworkRoot 'System.IO.Compression.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.IO.Compression.FileSystem.dll')",
    (Join-Path $testRoot 'ExtractorSmokeTest.cs')
)

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Extractor smoke test compilation failed with exit code $LASTEXITCODE."
}

$fixtures = @(
    (Join-Path $testRoot 'fixtures\sample.txt'),
    (Join-Path $testRoot 'fixtures\sample.pdf'),
    (Join-Path $testRoot 'fixtures\sample.docx'),
    (Join-Path $testRoot 'fixtures\sample.png')
)

& $testExecutable ([System.IO.Path]::GetFullPath($ApplicationPath)) $fixtures
if ($LASTEXITCODE -ne 0) {
    throw "Extractor smoke test failed with exit code $LASTEXITCODE."
}

Write-Host 'PASS | extractor smoke test completed.'
