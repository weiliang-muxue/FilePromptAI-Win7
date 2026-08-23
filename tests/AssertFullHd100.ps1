param(
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$probe = Join-Path $artifactRoot 'DisplayEnvironmentProbe.exe'
$source = Join-Path $testRoot 'DisplayEnvironmentProbe.cs'
$manifest = Join-Path $testRoot 'DisplayEnvironmentProbe.manifest'

foreach ($required in @($compiler, $source, $manifest)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing FullHd100 display probe input: $required"
    }
}
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$probe",
    "/win32manifest:$manifest",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    $source
)
& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "FullHd100 display probe compilation failed with exit code $LASTEXITCODE."
}

$probeArguments = @()
if ($SelfTest) {
    $probeArguments += '--self-test'
}
& $probe $probeArguments
if ($LASTEXITCODE -ne 0) {
    throw "FullHd100 display verification failed with exit code $LASTEXITCODE."
}
