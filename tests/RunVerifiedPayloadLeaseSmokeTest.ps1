param(
    [string]$Version = '1.17'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testExecutable = Join-Path $artifactRoot 'VerifiedPayloadLeaseSmokeTest.exe'
$stagingRoot = Join-Path $projectRoot "FilePromptAI-offline-release-v$Version"
$verifierPath = Join-Path $stagingRoot 'Verify-FilePromptAI.exe'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-PayloadLease-' + [Guid]::NewGuid().ToString('N')
)

foreach ($required in @($compiler, $verifierPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required payload lease test input is missing: $required"
    }
}
if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
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
    (Join-Path $testRoot 'VerifiedPayloadLeaseSmokeTest.cs')
)
& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Payload lease test compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $stagingRoot -Destination $temporaryRoot -Recurse
try {
    & $testExecutable $verifierPath $temporaryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Payload lease test failed with exit code $LASTEXITCODE."
    }
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporary.StartsWith(
        $resolvedSystemTemp,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary).StartsWith(
            'FilePromptAI-PayloadLease-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
