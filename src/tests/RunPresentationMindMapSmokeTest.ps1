$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$testRoot = Join-Path $projectRoot 'tests'
$outputRoot = Join-Path $testRoot 'build-artifacts'
$frameworkRoot = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
$output = Join-Path $outputRoot 'PresentationMindMapSmokeTest.exe'

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
    (Join-Path $projectRoot 'src\PptxExporter.cs'),
    (Join-Path $projectRoot 'src\XMindExporter.cs'),
    (Join-Path $testRoot 'PresentationMindMapSmokeTest.cs')
)

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Presentation/mind-map smoke test compilation failed with exit code $LASTEXITCODE."
}

& $output
if ($LASTEXITCODE -ne 0) {
    throw "Presentation/mind-map smoke test failed with exit code $LASTEXITCODE."
}
