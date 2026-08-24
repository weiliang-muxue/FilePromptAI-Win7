$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $testRoot
$outputRoot = Join-Path $testRoot 'build-artifacts'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$output = Join-Path $outputRoot 'XMindExportSmokeTest.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '.NET Framework C# compiler was not found.'
}
if (-not (Test-Path -LiteralPath $outputRoot)) {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/langversion:5',
    '/codepage:65001',
    '/warn:4',
    "/out:$output",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.IO.Compression.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.IO.Compression.FileSystem.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Xml.dll')",
    (Join-Path $projectRoot 'src\AtomicFile.cs'),
    (Join-Path $projectRoot 'src\MarkdownDocument.cs'),
    (Join-Path $projectRoot 'src\XMindExporter.cs'),
    (Join-Path $testRoot 'XMindExportSmokeTest.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "XMind export smoke test compilation failed with exit code $LASTEXITCODE."
}

& $output
if ($LASTEXITCODE -ne 0) {
    throw "XMind export smoke test failed with exit code $LASTEXITCODE."
}
