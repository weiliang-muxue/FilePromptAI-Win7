param(
    [string]$Version = '1.19',
    [string]$ArchivePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$artifactRoot = Join-Path $testRoot 'build-artifacts'
$testExecutable = Join-Path $artifactRoot 'VerifiedPayloadLeaseSmokeTest.exe'
$archiveName = "FilePromptAI-Win7-Full-v$Version.zip"
$resolvedArchivePath = if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    Join-Path $projectRoot $archiveName
}
else {
    [IO.Path]::GetFullPath($ArchivePath)
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'FilePromptAI-PayloadLease-' + [Guid]::NewGuid().ToString('N')
)
$verifierPath = Join-Path $temporaryRoot 'Verify-FilePromptAI.exe'

foreach ($required in @($compiler, $resolvedArchivePath)) {
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
try {
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Payload lease test compilation failed with exit code $LASTEXITCODE."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory(
        $resolvedArchivePath,
        $temporaryRoot)
    if (-not (Test-Path -LiteralPath $verifierPath -PathType Leaf)) {
        throw "The tested ZIP does not contain its root verifier: $verifierPath"
    }
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
